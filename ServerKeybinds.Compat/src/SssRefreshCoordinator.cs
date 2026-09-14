using System;
using System.Collections.Generic;
using System.Linq;

namespace ServerKeybinds;

/// <summary>Process-wide refresh counters.</summary>
public readonly struct SssRefreshCounters
{
    internal SssRefreshCounters(long requested, long sent, long coalesced, long rateLimited, long identicalSnapshots)
    {
        Requested = requested;
        Sent = sent;
        Coalesced = coalesced;
        RateLimited = rateLimited;
        IdenticalSnapshots = identicalSnapshots;
    }

    public long Requested { get; }
    public long Sent { get; }
    public long Coalesced { get; }
    public long RateLimited { get; }
    public long IdenticalSnapshots { get; }
}

/// <summary>Per-player refresh audit state, expressed in the coordinator's monotonic seconds.</summary>
public readonly struct SssRefreshPlayerDiagnostics
{
    internal SssRefreshPlayerDiagnostics(bool hasSent, double lastSendSeconds, string fingerprint, bool pending, int sendsInRollingMinute)
    {
        HasSent = hasSent;
        LastSendSeconds = lastSendSeconds;
        Fingerprint = fingerprint;
        Pending = pending;
        SendsInRollingMinute = sendsInRollingMinute;
    }

    public bool HasSent { get; }
    public double LastSendSeconds { get; }
    public string Fingerprint { get; }
    public bool Pending { get; }
    public int SendsInRollingMinute { get; }
}

/// <summary>
/// Pure per-player refresh budget. Requests use trailing 500 ms debounce, replace the queued snapshot with
/// the latest one, coalesce reasons, suppress an unchanged fingerprint, keep sends two seconds apart, and
/// allow at most six sends in any rolling minute.
/// </summary>
public sealed class SssRefreshCoordinator<TKey, TSnapshot> where TKey : notnull
{
    public const double DebounceSeconds = 0.5;
    public const double MinimumSendIntervalSeconds = 2.0;
    public const int MaximumSendsPerRollingMinute = 6;
    public const double RollingWindowSeconds = 60.0;

    private readonly Func<double> _now;
    private readonly Func<TSnapshot, string> _fingerprint;
    private readonly Func<TKey, TSnapshot, IReadOnlyCollection<string>, bool> _send;
    private readonly Dictionary<TKey, PlayerState> _players = new();
    private readonly Dictionary<TKey, PendingRefresh> _pending = new();

    private long _requested;
    private long _sent;
    private long _coalesced;
    private long _rateLimited;
    private long _identical;

    public SssRefreshCoordinator(
        Func<double> monotonicSeconds,
        Func<TSnapshot, string> fingerprint,
        Func<TKey, TSnapshot, IReadOnlyCollection<string>, bool> send)
    {
        _now = monotonicSeconds ?? throw new ArgumentNullException(nameof(monotonicSeconds));
        _fingerprint = fingerprint ?? throw new ArgumentNullException(nameof(fingerprint));
        _send = send ?? throw new ArgumentNullException(nameof(send));
    }

    public SssRefreshCounters Counters => new(_requested, _sent, _coalesced, _rateLimited, _identical);

    public int PendingCount => _pending.Count;

    public void Request(TKey player, TSnapshot latestSnapshot, string reason)
    {
        double now = _now();
        _requested++;
        if (_pending.TryGetValue(player, out PendingRefresh? pending) && pending != null)
        {
            _coalesced++;
            pending.Snapshot = latestSnapshot;
            pending.DueSeconds = now + DebounceSeconds;
            pending.Reasons.Add(NormalizeReason(reason));
            return;
        }

        _pending[player] = new PendingRefresh(latestSnapshot, now + DebounceSeconds, NormalizeReason(reason));
    }

    /// <summary>Records a successful out-of-band send such as the mandatory first join pack.</summary>
    public void RecordSent(TKey player, TSnapshot snapshot)
    {
        double now = _now();
        PlayerState state = GetOrCreateState(player);
        Prune(state, now);
        state.HasSent = true;
        state.LastSendSeconds = now;
        state.LastFingerprint = _fingerprint(snapshot) ?? string.Empty;
        state.SendTimes.Enqueue(now);
        _sent++;
        _pending.Remove(player);
    }

    /// <summary>Processes every due player once and returns seconds until the next candidate, or null.</summary>
    public double? ProcessDue()
    {
        double now = _now();
        foreach (TKey player in _pending.Keys.ToArray())
        {
            if (_pending.TryGetValue(player, out PendingRefresh? pending) && pending != null && pending.DueSeconds <= now)
            {
                ProcessOne(player, pending, now);
            }
        }

        return NextDelay(now);
    }

    public double? SecondsUntilNextProcess() => NextDelay(_now());

    public bool TryGetDiagnostics(TKey player, out SssRefreshPlayerDiagnostics diagnostics)
    {
        double now = _now();
        if (!_players.TryGetValue(player, out PlayerState? state) || state == null)
        {
            diagnostics = new SssRefreshPlayerDiagnostics(false, 0, string.Empty, _pending.ContainsKey(player), 0);
            return _pending.ContainsKey(player);
        }

        Prune(state, now);
        diagnostics = new SssRefreshPlayerDiagnostics(
            state.HasSent,
            state.LastSendSeconds,
            state.LastFingerprint,
            _pending.ContainsKey(player),
            state.SendTimes.Count);
        return true;
    }

    public void Remove(TKey player)
    {
        _pending.Remove(player);
        _players.Remove(player);
    }

    public void Clear()
    {
        _pending.Clear();
        _players.Clear();
    }

    private void ProcessOne(TKey player, PendingRefresh pending, double now)
    {
        PlayerState state = GetOrCreateState(player);
        Prune(state, now);
        string fingerprint = _fingerprint(pending.Snapshot) ?? string.Empty;
        if (state.HasSent && string.Equals(state.LastFingerprint, fingerprint, StringComparison.Ordinal))
        {
            _identical++;
            _pending.Remove(player);
            return;
        }

        double earliest = pending.DueSeconds;
        if (state.HasSent)
        {
            earliest = Math.Max(earliest, state.LastSendSeconds + MinimumSendIntervalSeconds);
        }

        if (state.SendTimes.Count >= MaximumSendsPerRollingMinute)
        {
            earliest = Math.Max(earliest, state.SendTimes.Peek() + RollingWindowSeconds);
        }

        if (earliest > now)
        {
            pending.DueSeconds = earliest;
            if (!pending.RateLimitCounted)
            {
                pending.RateLimitCounted = true;
                _rateLimited++;
            }
            return;
        }

        string[] reasons = pending.Reasons.OrderBy(value => value, StringComparer.Ordinal).ToArray();
        _pending.Remove(player);
        if (!_send(player, pending.Snapshot, reasons))
        {
            return;
        }

        state.HasSent = true;
        state.LastSendSeconds = now;
        state.LastFingerprint = fingerprint;
        state.SendTimes.Enqueue(now);
        _sent++;
    }

    private PlayerState GetOrCreateState(TKey player)
    {
        if (!_players.TryGetValue(player, out PlayerState? state) || state == null)
        {
            state = new PlayerState();
            _players[player] = state;
        }

        return state;
    }

    private static void Prune(PlayerState state, double now)
    {
        while (state.SendTimes.Count > 0 && now - state.SendTimes.Peek() >= RollingWindowSeconds)
        {
            state.SendTimes.Dequeue();
        }
    }

    private double? NextDelay(double now)
    {
        if (_pending.Count == 0)
        {
            return null;
        }

        double due = _pending.Values.Min(value => value.DueSeconds);
        return Math.Max(0, due - now);
    }

    private static string NormalizeReason(string reason) => string.IsNullOrWhiteSpace(reason) ? "unspecified" : reason.Trim();

    private sealed class PlayerState
    {
        public bool HasSent;
        public double LastSendSeconds;
        public string LastFingerprint = string.Empty;
        public readonly Queue<double> SendTimes = new();
    }

    private sealed class PendingRefresh
    {
        public PendingRefresh(TSnapshot snapshot, double dueSeconds, string reason)
        {
            Snapshot = snapshot;
            DueSeconds = dueSeconds;
            Reasons.Add(reason);
        }

        public TSnapshot Snapshot;
        public double DueSeconds;
        public bool RateLimitCounted;
        public readonly HashSet<string> Reasons = new(StringComparer.Ordinal);
    }
}
