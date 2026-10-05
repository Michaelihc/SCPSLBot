#nullable enable

using System;
using System.Collections.Generic;

namespace SCPSLBot.Warmup.Controls;

/// <summary>
/// Bounds sequential SSS mutations for each full authenticated UserId. This is deliberately
/// separate from <see cref="PerUserRequestGuard"/>, which only rejects overlapping callbacks.
/// </summary>
public sealed class PerUserActionRateLimiter
{
    private readonly object sync = new();
    private readonly Dictionary<string, long> lastAccepted = new(StringComparer.Ordinal);
    private readonly IMonotonicClock clock;

    public PerUserActionRateLimiter(IMonotonicClock? clock = null)
    {
        this.clock = clock ?? StopwatchMonotonicClock.Instance;
    }

    public bool TryAcquire(string fullUserId, int minimumIntervalMilliseconds, out double remainingSeconds)
    {
        remainingSeconds = 0d;
        if (string.IsNullOrWhiteSpace(fullUserId)
            || minimumIntervalMilliseconds < 0
            || clock.Frequency <= 0)
        {
            return false;
        }

        long now = clock.Timestamp;
        long minimumTicks = (long)Math.Ceiling(
            minimumIntervalMilliseconds * (double)clock.Frequency / 1000d);
        lock (sync)
        {
            if (lastAccepted.TryGetValue(fullUserId, out long previous))
            {
                long elapsed = now >= previous ? now - previous : 0L;
                if (elapsed < minimumTicks)
                {
                    remainingSeconds = (minimumTicks - elapsed) / (double)clock.Frequency;
                    return false;
                }
            }

            lastAccepted[fullUserId] = now;
            return true;
        }
    }

    public void Forget(string fullUserId)
    {
        if (string.IsNullOrWhiteSpace(fullUserId))
        {
            return;
        }

        lock (sync)
        {
            lastAccepted.Remove(fullUserId);
        }
    }

    public void Clear()
    {
        lock (sync)
        {
            lastAccepted.Clear();
        }
    }
}
