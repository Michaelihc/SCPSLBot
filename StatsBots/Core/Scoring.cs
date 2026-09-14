using System;
using System.Collections.Generic;

namespace StatsBots.Core;

internal enum CombatActorKind
{
    Other,
    RealAuthenticated,
    ManagedBot,
}

internal readonly record struct ScoringInput(
    CombatActorKind Attacker,
    CombatActorKind Victim,
    bool IsSelfKill,
    bool IsDisallowedTeamKill);

internal readonly record struct ScoreMutation(
    long BotKillsDelta,
    long BotDeathsDelta,
    long ScoreDelta,
    long CurrentStreakDelta,
    bool ResetCurrentStreak)
{
    public bool HasChanges => BotKillsDelta != 0 || BotDeathsDelta != 0 || ScoreDelta != 0 || CurrentStreakDelta != 0 || ResetCurrentStreak;
}

internal static class ScoringMatrix
{
    public static ScoreMutation Evaluate(ScoringInput input, long scorePerBotKill)
    {
        if (input.IsSelfKill || input.IsDisallowedTeamKill)
            return default;

        if (input.Attacker == CombatActorKind.RealAuthenticated && input.Victim == CombatActorKind.ManagedBot)
            return new ScoreMutation(1, 0, Math.Max(0, scorePerBotKill), 1, false);

        if (input.Attacker == CombatActorKind.ManagedBot && input.Victim == CombatActorKind.RealAuthenticated)
            return new ScoreMutation(0, 1, 0, 0, true);

        return default;
    }

    public static long ClampScore(long current, long delta)
    {
        if (delta > 0 && current > long.MaxValue - delta) return long.MaxValue;
        if (delta < 0 && current < long.MinValue - delta) return 0;
        return Math.Max(0, current + delta);
    }
}

internal readonly record struct DeathFingerprint(uint VictimNetworkId, int DamageHandlerIdentity);

internal sealed class DeathEventDeduplicator
{
    private readonly long _windowTicks;
    private readonly int _capacity;
    private readonly Dictionary<DeathFingerprint, long> _seen = new();
    private readonly Queue<(DeathFingerprint Fingerprint, long SeenAt)> _order = new();

    public DeathEventDeduplicator(long windowTicks, int capacity = 512)
    {
        _windowTicks = Math.Max(1, windowTicks);
        _capacity = Math.Max(16, capacity);
    }

    public bool TryAccept(DeathFingerprint fingerprint, long nowTicks)
    {
        lock (_seen)
        {
            Purge(nowTicks - _windowTicks);
            if (_seen.TryGetValue(fingerprint, out long prior) && nowTicks - prior >= 0 && nowTicks - prior <= _windowTicks)
                return false;

            _seen[fingerprint] = nowTicks;
            _order.Enqueue((fingerprint, nowTicks));
            while (_seen.Count > _capacity && _order.Count > 0)
            {
                (DeathFingerprint oldest, long seenAt) = _order.Dequeue();
                if (_seen.TryGetValue(oldest, out long current) && current == seenAt) _seen.Remove(oldest);
            }
            return true;
        }
    }

    public void Clear()
    {
        lock (_seen)
        {
            _seen.Clear();
            _order.Clear();
        }
    }

    private void Purge(long cutoff)
    {
        while (_order.Count > 0 && _order.Peek().SeenAt < cutoff)
        {
            (DeathFingerprint fingerprint, long seenAt) = _order.Dequeue();
            if (_seen.TryGetValue(fingerprint, out long current) && current == seenAt) _seen.Remove(fingerprint);
        }
    }
}
