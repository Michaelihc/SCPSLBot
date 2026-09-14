using System;

namespace SCPSLBot.Performance
{
    /// <summary>
    /// Allocation-free fixed-rate gate driven by a caller-owned monotonic clock.
    /// </summary>
    internal sealed class FixedRateRefreshGate
    {
        private readonly float intervalSeconds;
        private float nextRefreshTime = float.NegativeInfinity;

        public FixedRateRefreshGate(float refreshesPerSecond)
        {
            if (refreshesPerSecond <= 0f || float.IsNaN(refreshesPerSecond) || float.IsInfinity(refreshesPerSecond))
            {
                throw new ArgumentOutOfRangeException(nameof(refreshesPerSecond));
            }

            intervalSeconds = 1f / refreshesPerSecond;
        }

        public float IntervalSeconds => intervalSeconds;

        public bool TryAcquire(float now)
        {
            if (float.IsNaN(now) || now < nextRefreshTime)
            {
                return false;
            }

            nextRefreshTime = now + intervalSeconds;
            return true;
        }

        public void Reset()
        {
            nextRefreshTime = float.NegativeInfinity;
        }
    }
}
