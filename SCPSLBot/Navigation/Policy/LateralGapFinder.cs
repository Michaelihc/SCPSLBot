using System;
using System.Collections.Generic;

namespace SCPSLBot.Navigation.Policy
{
    /// <summary>A free lateral interval across a passage, in meters relative to the passage center.</summary>
    internal readonly struct LateralGap
    {
        public LateralGap(float start, float end)
        {
            Start = start;
            End = end;
        }

        public float Start { get; }
        public float End { get; }
        public float Width => End - Start;
        public float Center => (Start + End) * 0.5f;

        public override string ToString() => $"[{Start:F2},{End:F2}]";
    }

    /// <summary>
    /// Turns a row of capsule-probe results (one per lateral sample column) into passable gaps and
    /// chooses the gap a bot should aim for. Pure so the selection rules are unit-testable.
    /// </summary>
    internal static class LateralGapFinder
    {
        // A barely passable slot costs this much per missing meter of comfort, so a comfortable
        // gap a couple of meters off-center still wins over squeezing through clutter.
        private const float NarrowGapPenaltyPerMeter = 6f;

        /// <summary>
        /// Groups consecutive free columns into gaps. Column <c>i</c> is centered at
        /// <paramref name="firstOffset"/> + i * <paramref name="step"/> and covers half a step on
        /// each side.
        /// </summary>
        public static List<LateralGap> FindGaps(IReadOnlyList<bool> freeColumns, float firstOffset, float step)
        {
            var gaps = new List<LateralGap>();
            if (freeColumns == null || freeColumns.Count == 0 || step <= 0f)
            {
                return gaps;
            }

            var runStart = -1;
            for (var i = 0; i <= freeColumns.Count; i++)
            {
                var free = i < freeColumns.Count && freeColumns[i];
                if (free && runStart < 0)
                {
                    runStart = i;
                }
                else if (!free && runStart >= 0)
                {
                    gaps.Add(new LateralGap(
                        firstOffset + runStart * step - step * 0.5f,
                        firstOffset + (i - 1) * step + step * 0.5f));
                    runStart = -1;
                }
            }

            return gaps;
        }

        /// <summary>
        /// Picks the passage a bot should use: the gap nearest to <paramref name="preferredOffset"/>
        /// among those at least <paramref name="minWidth"/> wide. Width wins over proximity only when
        /// the nearer gap is barely wide enough (less than <paramref name="comfortableWidth"/>).
        /// </summary>
        public static LateralGap? SelectPassage(IReadOnlyList<LateralGap> gaps, float minWidth, float comfortableWidth, float preferredOffset)
        {
            if (gaps == null || gaps.Count == 0)
            {
                return null;
            }

            LateralGap? best = null;
            var bestScore = float.PositiveInfinity;
            foreach (var gap in gaps)
            {
                if (gap.Width + 1e-4f < minWidth)
                {
                    continue;
                }

                // Distance from the preferred offset to the nearest point of the gap's usable band
                // (the band a bot center can occupy), plus a penalty for uncomfortably narrow gaps.
                var usableHalf = Math.Max(0f, (gap.Width - minWidth) * 0.5f);
                var usableStart = gap.Center - usableHalf;
                var usableEnd = gap.Center + usableHalf;
                var distance = preferredOffset < usableStart
                    ? usableStart - preferredOffset
                    : preferredOffset > usableEnd ? preferredOffset - usableEnd : 0f;
                var narrowPenalty = gap.Width < comfortableWidth ? (comfortableWidth - gap.Width) * NarrowGapPenaltyPerMeter : 0f;
                var score = distance + narrowPenalty;
                if (score < bestScore)
                {
                    bestScore = score;
                    best = gap;
                }
            }

            return best;
        }
    }
}
