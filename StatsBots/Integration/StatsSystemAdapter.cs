using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using StatsBots.Core;

namespace StatsBots.Integration;

internal enum ProviderState
{
    Loading,
    Ready,
    Unavailable,
}

internal sealed class StatsRecord
{
    private readonly Dictionary<string, long> _counters;

    public StatsRecord(Dictionary<string, long> counters, TimeSpan? totalPlayTime)
    {
        _counters = counters;
        TotalPlayTime = totalPlayTime;
    }

    public TimeSpan? TotalPlayTime { get; }
    public long Counter(string key) => _counters.TryGetValue(key, out long value) ? value : 0L;
}

/// <summary>
/// Late-bound adapter over StatsSystem's public provider surface. It deliberately passes a null store
/// argument on every call, which selects StatsSystem's hydrated default player_stats store.
/// </summary>
internal sealed class StatsSystemAdapter
{
    private const string AssemblyName = "StatsSystem";
    private const string PluginTypeName = "StatsSystem.StatsSystemPlugin";
    private readonly object _gate = new();
    private object? _provider;
    private PropertyInfo? _statsProperty;
    private MethodInfo? _tryGetStats;
    private MethodInfo? _tryGetOrCreateStats;
    private MethodInfo? _incrementCounter;
    private MethodInfo? _setCounter;
    private MethodInfo? _deleteStatKey;
    private MethodInfo? _save;
    private MethodInfo? _ensureHydrated;
    private MethodInfo? _getCounter;
    private MethodInfo? _getDuration;
    private PropertyInfo? _durationsProperty;
    private MethodInfo? _durationContainsKey;
    private readonly Dictionary<string, Task> _offlineHydrations = new(StringComparer.Ordinal);
    private readonly HashSet<string> _offlineHydrated = new(StringComparer.Ordinal);
    private string _lastFailure = "StatsSystem.dll is not loaded.";

    public string LastFailure { get { lock (_gate) return _lastFailure; } }

    public ProviderState State
    {
        get
        {
            lock (_gate)
            {
                return TryBind(out ProviderState state) ? ProviderState.Ready : state;
            }
        }
    }

    public ProviderState TryRead(string userId, IEnumerable<string> keys, out StatsRecord? record)
    {
        record = null;
        if (!AuthenticatedIdentity.IsFullUserId(userId))
        {
            lock (_gate) _lastFailure = "A full authenticated UserId is required.";
            return ProviderState.Unavailable;
        }

        lock (_gate)
        {
            if (!TryBind(out ProviderState state)) return state;
            try
            {
                object? stats = TryGetPlayerStats(userId);
                if (stats == null)
                {
                    _lastFailure = "The default player_stats record is still hydrating or does not exist yet.";
                    return ProviderState.Loading;
                }

                var counters = new Dictionary<string, long>(StringComparer.Ordinal);
                foreach (string key in keys.Distinct(StringComparer.Ordinal))
                {
                    if (!StatsKeys.IsWarmupKey(key)) throw new InvalidOperationException("Non-Warmup counter requested through StatsBots.");
                    counters[key] = Convert.ToInt64(_getCounter!.Invoke(stats, new object[] { key }));
                }

                object? durations = _durationsProperty!.GetValue(stats);
                bool hasVerifiedPlaytime = durations != null
                    && _durationContainsKey!.Invoke(durations, new object[] { StatsKeys.TotalPlayTime }) is true;
                TimeSpan? playtime = null;
                if (hasVerifiedPlaytime)
                {
                    object? durationValue = _getDuration!.Invoke(stats, new object[] { StatsKeys.TotalPlayTime });
                    if (durationValue is not TimeSpan value)
                        throw new InvalidOperationException("StatsSystem returned an invalid TotalPlayTime value.");
                    playtime = value;
                }
                record = new StatsRecord(counters, playtime);
                _lastFailure = string.Empty;
                return ProviderState.Ready;
            }
            catch (Exception ex)
            {
                Invalidate("StatsSystem read failed: " + ex.GetBaseException().Message);
                return ProviderState.Unavailable;
            }
        }
    }

    public ProviderState EnsureOfflineHydrated(string userId)
    {
        if (!AuthenticatedIdentity.IsFullUserId(userId)) return ProviderState.Unavailable;
        lock (_gate)
        {
            if (!TryBind(out ProviderState state)) return state;
            try
            {
                if (TryGetPlayerStats(userId) != null)
                {
                    _offlineHydrations.Remove(userId);
                    _offlineHydrated.Remove(userId);
                    return ProviderState.Ready;
                }
                if (_offlineHydrated.Contains(userId)) return ProviderState.Ready;
                if (_ensureHydrated == null)
                {
                    _lastFailure = "StatsSystem has no offline hydration API.";
                    return ProviderState.Unavailable;
                }

                if (!_offlineHydrations.TryGetValue(userId, out Task? hydration) || hydration == null)
                {
                    object provider = _provider!;
                    MethodInfo ensure = _ensureHydrated;
                    hydration = Task.Run(() => ensure.Invoke(provider, new object?[] { userId, null }));
                    _offlineHydrations[userId] = hydration;
                    _lastFailure = "Offline StatsSystem hydration is running asynchronously; retry the RA command.";
                    return ProviderState.Loading;
                }
                if (!hydration.IsCompleted)
                {
                    _lastFailure = "Offline StatsSystem hydration is still running; retry the RA command.";
                    return ProviderState.Loading;
                }

                _offlineHydrations.Remove(userId);
                if (hydration.IsFaulted)
                {
                    string message = hydration.Exception?.GetBaseException().Message ?? "unknown hydration error";
                    Invalidate("StatsSystem offline hydrate failed: " + message);
                    return ProviderState.Unavailable;
                }
                if (hydration.IsCanceled)
                {
                    _lastFailure = "StatsSystem offline hydration was canceled.";
                    return ProviderState.Unavailable;
                }

                MarkOfflineHydrated(userId);
                _lastFailure = string.Empty;
                return ProviderState.Ready;
            }
            catch (Exception ex)
            {
                Invalidate("StatsSystem offline hydrate failed: " + ex.GetBaseException().Message);
                return ProviderState.Unavailable;
            }
        }
    }

    public ProviderState TryEnsureRecord(string userId)
    {
        if (!AuthenticatedIdentity.IsFullUserId(userId)) return ProviderState.Unavailable;
        lock (_gate)
        {
            if (!TryBind(out ProviderState state)) return state;
            try
            {
                object?[] args = { userId, null, null };
                bool ok = _tryGetOrCreateStats!.Invoke(_provider, args) is true;
                return ok && args[1] != null ? ProviderState.Ready : ProviderState.Loading;
            }
            catch (Exception ex)
            {
                Invalidate("StatsSystem record initialization failed: " + ex.GetBaseException().Message);
                return ProviderState.Unavailable;
            }
        }
    }

    public ProviderState Increment(string userId, string key, long amount)
        => Mutate(userId, key, () => _incrementCounter!.Invoke(_provider, new object?[] { userId, key, amount, null }));

    public ProviderState Set(string userId, string key, long value)
        => Mutate(userId, key, () => _setCounter!.Invoke(_provider, new object?[] { userId, key, value, null }));

    public ProviderState Delete(string userId, string key, out bool removed)
    {
        removed = false;
        bool localRemoved = false;
        ProviderState result = Mutate(userId, key, () =>
        {
            localRemoved = _deleteStatKey!.Invoke(_provider, new object?[] { userId, key, null }) is true;
        });
        removed = localRemoved;
        return result;
    }

    public void Flush()
    {
        lock (_gate)
        {
            if (!TryBind(out _)) return;
            try { _save!.Invoke(_provider, Array.Empty<object>()); }
            catch (Exception ex) { Invalidate("StatsSystem flush failed: " + ex.GetBaseException().Message); }
        }
    }

    private ProviderState Mutate(string userId, string key, Action action)
    {
        if (!AuthenticatedIdentity.IsFullUserId(userId) || !StatsKeys.IsWarmupKey(key)) return ProviderState.Unavailable;
        lock (_gate)
        {
            if (!TryBind(out ProviderState state)) return state;
            try
            {
                action();
                _offlineHydrations.Remove(userId);
                _offlineHydrated.Remove(userId);
                _lastFailure = string.Empty;
                return ProviderState.Ready;
            }
            catch (Exception ex)
            {
                Invalidate("StatsSystem mutation failed: " + ex.GetBaseException().Message);
                return ProviderState.Unavailable;
            }
        }
    }

    private object? TryGetPlayerStats(string userId)
    {
        object?[] args = { userId, null, null };
        bool found = _tryGetStats!.Invoke(_provider, args) is true;
        return found ? args[1] : null;
    }

    private bool TryBind(out ProviderState state)
    {
        try
        {
            Assembly? assembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(static a => string.Equals(a.GetName().Name, AssemblyName, StringComparison.OrdinalIgnoreCase));
            if (assembly == null)
            {
                Invalidate("StatsSystem.dll is not loaded.", clearMetadata: true);
                state = ProviderState.Unavailable;
                return false;
            }

            Type? pluginType = assembly.GetType(PluginTypeName, false);
            _statsProperty ??= pluginType?.GetProperty("Stats", BindingFlags.Public | BindingFlags.Static);
            object? current = _statsProperty?.GetValue(null);
            if (current == null)
            {
                _provider = null;
                _lastFailure = "StatsSystem is loaded but its provider is still initializing.";
                state = ProviderState.Loading;
                return false;
            }

            if (ReferenceEquals(current, _provider) && _tryGetStats != null)
            {
                state = ProviderState.Ready;
                return true;
            }

            _offlineHydrations.Clear();
            _offlineHydrated.Clear();
            Type providerType = current.GetType();
            _tryGetStats = Find(providerType, "TryGetStats", typeof(string), null, typeof(string));
            _tryGetOrCreateStats = Find(providerType, "TryGetOrCreateStats", typeof(string), null, typeof(string));
            _incrementCounter = Find(providerType, "IncrementCounter", typeof(string), typeof(string), typeof(long), typeof(string));
            _setCounter = Find(providerType, "SetCounter", typeof(string), typeof(string), typeof(long), typeof(string));
            _deleteStatKey = Find(providerType, "DeleteStatKey", typeof(string), typeof(string), typeof(string));
            _save = Find(providerType, "Save");
            _ensureHydrated = providerType.GetMethod("EnsureHydrated", BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(string), typeof(string) }, null);

            Type? statsType = assembly.GetType("StatsSystem.API.PlayerStats", false);
            _getCounter = statsType?.GetMethod("GetCounter", new[] { typeof(string) });
            _getDuration = statsType?.GetMethod("GetDuration", new[] { typeof(string) });
            _durationsProperty = statsType?.GetProperty("Durations", BindingFlags.Public | BindingFlags.Instance);
            _durationContainsKey = _durationsProperty?.PropertyType.GetMethod("ContainsKey", new[] { typeof(string) });
            if (_tryGetStats == null || _tryGetOrCreateStats == null || _incrementCounter == null || _setCounter == null
                || _deleteStatKey == null || _save == null || _getCounter == null || _getDuration == null
                || _durationsProperty == null || _durationContainsKey == null)
            {
                Invalidate("StatsSystem is loaded but the required provider API is incompatible.");
                state = ProviderState.Unavailable;
                return false;
            }

            _provider = current;
            _lastFailure = string.Empty;
            state = ProviderState.Ready;
            return true;
        }
        catch (Exception ex)
        {
            Invalidate("StatsSystem binding failed: " + ex.GetBaseException().Message);
            state = ProviderState.Unavailable;
            return false;
        }
    }

    private static MethodInfo? Find(Type type, string name, params Type?[] parameters)
    {
        return type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(method => method.Name == name && ParametersMatch(method.GetParameters(), parameters));
    }

    private static bool ParametersMatch(ParameterInfo[] actual, Type?[] expected)
    {
        if (actual.Length != expected.Length) return false;
        for (int i = 0; i < actual.Length; i++)
            if (expected[i] != null && actual[i].ParameterType != expected[i] && !actual[i].ParameterType.IsByRef)
                return false;
        return true;
    }

    private void Invalidate(string reason, bool clearMetadata = false)
    {
        _provider = null;
        _lastFailure = reason;
        if (!clearMetadata) return;
        _statsProperty = null;
        _tryGetStats = null;
        _tryGetOrCreateStats = null;
        _incrementCounter = null;
        _setCounter = null;
        _deleteStatKey = null;
        _save = null;
        _ensureHydrated = null;
        _getCounter = null;
        _getDuration = null;
        _durationsProperty = null;
        _durationContainsKey = null;
        _offlineHydrations.Clear();
        _offlineHydrated.Clear();
    }

    private void MarkOfflineHydrated(string userId)
    {
        if (_offlineHydrated.Count >= 512) _offlineHydrated.Clear();
        _offlineHydrated.Add(userId);
    }
}
