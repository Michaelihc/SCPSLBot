using System;
using System.Diagnostics;

namespace ScpslPluginStarter.Core;

internal interface IMonotonicClock
{
    long NowMilliseconds { get; }
}

internal sealed class StopwatchMonotonicClock : IMonotonicClock
{
    public long NowMilliseconds
    {
        get
        {
            long ticks = Stopwatch.GetTimestamp();
            long wholeSeconds = ticks / Stopwatch.Frequency;
            long remainder = ticks % Stopwatch.Frequency;
            return checked((wholeSeconds * 1000L) + (remainder * 1000L / Stopwatch.Frequency));
        }
    }
}

internal static class MonotonicDeadline
{
    public static long After(long nowMilliseconds, long durationMilliseconds)
    {
        long duration = Math.Max(0L, durationMilliseconds);
        return nowMilliseconds > long.MaxValue - duration ? long.MaxValue : nowMilliseconds + duration;
    }
}
