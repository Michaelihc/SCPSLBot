#nullable enable

using System.Diagnostics;

namespace SCPSLBot.Warmup.Controls;

public interface IMonotonicClock
{
    long Timestamp { get; }

    long Frequency { get; }
}

public sealed class StopwatchMonotonicClock : IMonotonicClock
{
    public static StopwatchMonotonicClock Instance { get; } = new();

    public long Timestamp => Stopwatch.GetTimestamp();

    public long Frequency => Stopwatch.Frequency;

    private StopwatchMonotonicClock()
    {
    }
}
