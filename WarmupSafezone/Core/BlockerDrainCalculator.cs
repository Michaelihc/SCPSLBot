using System;

namespace ScpslPluginStarter.Core;

internal static class BlockerDrainCalculator
{
    public static float Calculate(
        float maxHealth,
        long punishableStartMilliseconds,
        long durationMilliseconds,
        float initialHpPerSecond,
        float multiplierPerSecond,
        float maximumPercentPerSecond)
    {
        double remaining = Math.Max(0L, durationMilliseconds);
        long cursor = Math.Max(0L, punishableStartMilliseconds);
        double total = 0d;
        double maximumRate = Math.Max(0f, maximumPercentPerSecond) / 100d * Math.Max(1f, maxHealth);

        while (remaining > 0d)
        {
            long second = cursor / 1000L;
            long withinSecond = cursor % 1000L;
            double segment = Math.Min(remaining, 1000L - withinSecond);
            double rate = Math.Max(0d, initialHpPerSecond)
                * Math.Pow(Math.Max(1d, multiplierPerSecond), second);
            if (maximumRate > 0d)
            {
                rate = Math.Min(rate, maximumRate);
            }

            total += rate * segment / 1000d;
            remaining -= segment;
            cursor += (long)segment;
        }

        return (float)Math.Min(float.MaxValue, total);
    }
}
