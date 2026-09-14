using System;

namespace StatsBots.Core;

internal static class BeginnerEligibility
{
    public static bool IsEligible(TimeSpan? verifiedStoredPlaytime, TimeSpan currentSession, TimeSpan threshold)
        => verifiedStoredPlaytime.HasValue && verifiedStoredPlaytime.Value + currentSession < threshold;
}

internal sealed class VerifiedPlaytimeTracker
{
    private readonly double _joinedAt;
    private bool _known;
    private TimeSpan _observedProviderTotal;
    private TimeSpan _anchorTotal;
    private double _anchorAt;

    public VerifiedPlaytimeTracker(double joinedAt) => _joinedAt = joinedAt;

    public TimeSpan Observe(TimeSpan providerTotal, double now)
    {
        providerTotal = providerTotal < TimeSpan.Zero ? TimeSpan.Zero : providerTotal;
        if (!_known)
        {
            _known = true;
            _observedProviderTotal = providerTotal;
            _anchorTotal = providerTotal;
            _anchorAt = _joinedAt;
        }
        else if (providerTotal != _observedProviderTotal)
        {
            TimeSpan priorEffective = Current(now);
            _observedProviderTotal = providerTotal;
            _anchorTotal = providerTotal > priorEffective ? providerTotal : priorEffective;
            _anchorAt = now;
        }
        return Current(now);
    }

    private TimeSpan Current(double now)
        => _anchorTotal + TimeSpan.FromSeconds(Math.Max(0, now - _anchorAt));
}

internal enum NoticeKind
{
    None,
    Setup,
    Tip,
    Community,
}

internal sealed class NoticeCadence
{
    private readonly double _joinedAt;
    private readonly double _setupDelay;
    private readonly double _tipInterval;
    private readonly double _communityInterval;
    private bool _setupSent;
    private bool _communityPending = true;
    private double _nextTipAt;
    private double _nextCommunityAt;
    private double _nextFreeAt;

    public NoticeCadence(double joinedAt, double setupDelay, double tipInterval, double communityInterval)
    {
        _joinedAt = joinedAt;
        _setupDelay = Math.Max(0, setupDelay);
        _tipInterval = Math.Max(1, tipInterval);
        _communityInterval = Math.Max(1, communityInterval);
        _nextTipAt = joinedAt + _tipInterval;
        _nextCommunityAt = joinedAt + _communityInterval;
    }

    public void RequestCommunity() => _communityPending = true;

    public NoticeKind TakeNext(double now, bool beginner, bool beginnerTipsEnabled, bool communityEnabled)
    {
        if (now < _nextFreeAt) return NoticeKind.None;
        if (beginner && beginnerTipsEnabled && !_setupSent && now >= _joinedAt + _setupDelay)
        {
            _setupSent = true;
            return NoticeKind.Setup;
        }
        if (communityEnabled && (_communityPending || now >= _nextCommunityAt))
        {
            _communityPending = false;
            _nextCommunityAt = now + _communityInterval;
            return NoticeKind.Community;
        }
        if (beginner && beginnerTipsEnabled && now >= _nextTipAt)
        {
            _nextTipAt = now + _tipInterval;
            return NoticeKind.Tip;
        }
        return NoticeKind.None;
    }

    public void MarkOccupied(double now, double duration, double gap) => _nextFreeAt = now + Math.Max(0, duration) + Math.Max(0, gap);
}

internal sealed class TipShuffle
{
    private readonly int[] _bag;
    private uint _state;
    private int _cursor;
    private int _last = -1;

    public TipShuffle(string stableIdentity, int count)
    {
        _bag = new int[Math.Max(1, count)];
        _state = StableHash(stableIdentity);
        if (_state == 0) _state = 0x9E3779B9u;
        _cursor = _bag.Length;
    }

    public int Next()
    {
        if (_cursor >= _bag.Length) Refill();
        int value = _bag[_cursor++];
        _last = value;
        return value;
    }

    private void Refill()
    {
        for (int i = 0; i < _bag.Length; i++) _bag[i] = i;
        for (int i = _bag.Length - 1; i > 0; i--)
        {
            int swap = (int)(NextUInt() % (uint)(i + 1));
            (_bag[i], _bag[swap]) = (_bag[swap], _bag[i]);
        }
        if (_bag.Length > 1 && _bag[0] == _last)
            (_bag[0], _bag[1]) = (_bag[1], _bag[0]);
        _cursor = 0;
    }

    private uint NextUInt()
    {
        uint value = _state;
        value ^= value << 13;
        value ^= value >> 17;
        value ^= value << 5;
        _state = value;
        return value;
    }

    private static uint StableHash(string value)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (char c in value ?? string.Empty) hash = (hash ^ c) * 16777619;
            return hash;
        }
    }
}
