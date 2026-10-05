#nullable enable

using System;
using System.Collections.Generic;

namespace SCPSLBot.Warmup.Controls;

public enum PanelActionKind
{
    Role,
    Item,
    Teleport,
    Arena,
}

/// <summary>
/// Keeps each player-facing mutation on an independent monotonic cooldown timeline. Performing one
/// action never delays another action kind.
/// </summary>
public sealed class PanelActionCooldowns
{
    private readonly Dictionary<PanelActionKind, PerUserActionRateLimiter> limiters;

    public PanelActionCooldowns(IMonotonicClock? clock = null)
    {
        IMonotonicClock sharedClock = clock ?? StopwatchMonotonicClock.Instance;
        limiters = new Dictionary<PanelActionKind, PerUserActionRateLimiter>();
        foreach (PanelActionKind action in Enum.GetValues(typeof(PanelActionKind)))
        {
            limiters[action] = new PerUserActionRateLimiter(sharedClock);
        }
    }

    public bool TryAcquire(
        string fullUserId,
        PanelActionKind action,
        int cooldownMilliseconds,
        out double remainingSeconds) =>
        limiters[action].TryAcquire(fullUserId, cooldownMilliseconds, out remainingSeconds);

    public void Forget(string fullUserId)
    {
        foreach (PerUserActionRateLimiter limiter in limiters.Values)
        {
            limiter.Forget(fullUserId);
        }
    }

    public void Clear()
    {
        foreach (PerUserActionRateLimiter limiter in limiters.Values)
        {
            limiter.Clear();
        }
    }
}
