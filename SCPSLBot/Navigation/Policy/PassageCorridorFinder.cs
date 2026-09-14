using System;
using System.Collections.Generic;

namespace SCPSLBot.Navigation.Policy
{
    /// <summary>
    /// Connects free lateral gaps across consecutive depth slices of a passage into corridors a
    /// capsule can follow, including S-curves where no straight column is free (angled fences,
    /// staggered pipes). Pure so the corridor rules are unit-testable.
    /// </summary>
    internal static class PassageCorridorFinder
    {
        /// <summary>
        /// Picks the corridor with the widest narrowest slice; ties go to the corridor whose gap at
        /// <paramref name="portalSlice"/> is nearest to <paramref name="preferredOffset"/>. Returns
        /// the gap at the portal slice, or null when no corridor spans all slices.
        /// </summary>
        public static LateralGap? SelectCorridorPortal(
            IReadOnlyList<IReadOnlyList<LateralGap>> gapsPerSlice,
            int portalSlice,
            float minWidth,
            float minOverlap,
            float preferredOffset)
        {
            if (gapsPerSlice == null || gapsPerSlice.Count == 0 || portalSlice < 0 || portalSlice >= gapsPerSlice.Count)
            {
                return null;
            }

            // Dynamic programming over slices: for every gap of the current slice remember the best
            // chain (widest bottleneck) that reaches it, and which portal-slice gap that chain uses.
            var previous = new List<Chain>();
            foreach (var gap in gapsPerSlice[0])
            {
                if (gap.Width + 1e-4f < minWidth)
                {
                    continue;
                }

                previous.Add(new Chain(gap.Width, portalSlice == 0 ? gap : (LateralGap?)null, gap));
            }

            for (var slice = 1; slice < gapsPerSlice.Count && previous.Count > 0; slice++)
            {
                var current = new List<Chain>();
                foreach (var gap in gapsPerSlice[slice])
                {
                    if (gap.Width + 1e-4f < minWidth)
                    {
                        continue;
                    }

                    Chain? best = null;
                    foreach (var chain in previous)
                    {
                        var overlap = Math.Min(gap.End, chain.LastGap.End) - Math.Max(gap.Start, chain.LastGap.Start);
                        if (overlap + 1e-4f < minOverlap)
                        {
                            continue;
                        }

                        var bottleneck = Math.Min(chain.Bottleneck, Math.Min(gap.Width, overlap));
                        if (best == null || bottleneck > best.Value.Bottleneck)
                        {
                            best = new Chain(bottleneck, slice == portalSlice ? gap : chain.PortalGap, gap);
                        }
                    }

                    if (best.HasValue)
                    {
                        current.Add(best.Value);
                    }
                }

                previous = current;
            }

            LateralGap? selected = null;
            var selectedBottleneck = float.NegativeInfinity;
            var selectedDistance = float.PositiveInfinity;
            foreach (var chain in previous)
            {
                if (!chain.PortalGap.HasValue)
                {
                    continue;
                }

                var distance = Math.Abs(chain.PortalGap.Value.Center - preferredOffset);
                if (chain.Bottleneck > selectedBottleneck + 1e-4f
                    || (Math.Abs(chain.Bottleneck - selectedBottleneck) <= 1e-4f && distance < selectedDistance))
                {
                    selected = chain.PortalGap;
                    selectedBottleneck = chain.Bottleneck;
                    selectedDistance = distance;
                }
            }

            return selected;
        }

        private readonly struct Chain
        {
            public Chain(float bottleneck, LateralGap? portalGap, LateralGap lastGap)
            {
                Bottleneck = bottleneck;
                PortalGap = portalGap;
                LastGap = lastGap;
            }

            public float Bottleneck { get; }
            public LateralGap? PortalGap { get; }
            public LateralGap LastGap { get; }
        }
    }
}
