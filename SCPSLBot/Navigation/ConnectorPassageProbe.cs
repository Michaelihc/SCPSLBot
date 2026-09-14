using PlayerRoles.FirstPersonControl;
using SCPSLBot.Navigation.Policy;
using System.Collections.Generic;
using UnityEngine;

namespace SCPSLBot.Navigation
{
    /// <summary>
    /// Physically probes a room connector for the lateral band a bot capsule can walk through.
    /// Clutter connectors (boxes, shelves, pipes, fences) leave an off-center or winding gap,
    /// wallable connectors may be sealed entirely, and clutter destroyed by native blockers
    /// disappears; the live colliders decide, not the prefab type. Depth slices are chained into
    /// corridors so S-curves count, and low clutter the stuck ladder can jump is tolerated when
    /// nothing better exists.
    /// </summary>
    internal static class ConnectorPassageProbe
    {
        public const float LateralHalfSpan = 3.6f;
        public const float LateralStep = 0.15f;
        private const float DepthHalfSpan = 1f;
        private const float DepthStep = 0.5f;
        private const float FloorProbeHeight = 1.4f;
        private const float FloorProbeDepth = 3.5f;
        private const float JumpableClearance = 0.9f;

        public enum PassageTier
        {
            None,
            /// <summary>Walkable without leaving the ground.</summary>
            Walk,
            /// <summary>Only passable over low clutter (the recovery ladder jumps it).</summary>
            Jump,
        }

        public readonly struct PassageResult
        {
            public PassageResult(LateralGap? gap, PassageTier tier, float floorY, int gapCount, bool floorFound)
            {
                Gap = gap;
                Tier = tier;
                FloorY = floorY;
                GapCount = gapCount;
                FloorFound = floorFound;
            }

            public LateralGap? Gap { get; }
            public PassageTier Tier { get; }
            public float FloorY { get; }
            public int GapCount { get; }
            public bool FloorFound { get; }
            public bool IsPassable => Gap.HasValue;
        }

        private static int ProbeMask => FpcStateProcessor.Mask & ~LayerMask.GetMask("Player", "Hitbox");

        /// <summary>
        /// Samples capsule clearance across the opening at <paramref name="center"/> (any height near
        /// the floor), where <paramref name="forward"/> is the through-axis. Returns the selected
        /// portal band at the connector plane as lateral offsets along <paramref name="right"/>; the
        /// band is the range of capsule-center positions that are clear (radius already applied).
        /// </summary>
        public static PassageResult Probe(Vector3 center, Vector3 forward, Vector3 right)
        {
            // A free column already means the whole probe capsule fits there, so gap widths and
            // slice overlaps are measured in capsule-center space: one column is enough.
            var radius = NavigationAgentProfile.Radius;
            var minWidth = LateralStep - 1e-3f;
            var minOverlap = LateralStep - 1e-3f;
            var floorFound = TryFindFloor(center, right, out var floorY);
            if (!floorFound)
            {
                floorY = center.y - NavigationAgentProfile.RootHeight;
            }

            var walkResult = ProbeTier(center, forward, right, floorY, NavigationAgentProfile.StepClearance, radius, minWidth, minOverlap, out var walkGapCount);
            if (walkResult.HasValue)
            {
                return new PassageResult(walkResult, PassageTier.Walk, floorY, walkGapCount, floorFound);
            }

            var jumpResult = ProbeTier(center, forward, right, floorY, JumpableClearance, radius, minWidth, minOverlap, out var jumpGapCount);
            return jumpResult.HasValue
                ? new PassageResult(jumpResult, PassageTier.Jump, floorY, jumpGapCount, floorFound)
                : new PassageResult(null, PassageTier.None, floorY, walkGapCount, floorFound);
        }

        private static LateralGap? ProbeTier(Vector3 center, Vector3 forward, Vector3 right, float floorY, float groundClearance, float radius, float minWidth, float minOverlap, out int portalGapCount)
        {
            var bottomOffset = groundClearance + radius;
            var topOffset = Mathf.Max(bottomOffset + 0.05f, NavigationAgentProfile.Height - radius - 0.05f);
            var probeRadius = radius * 0.92f;
            var mask = ProbeMask;
            var columnCount = Mathf.RoundToInt(LateralHalfSpan * 2f / LateralStep) + 1;
            var sliceCount = Mathf.RoundToInt(DepthHalfSpan * 2f / DepthStep) + 1;
            var portalSlice = sliceCount / 2;

            var gapsPerSlice = new List<IReadOnlyList<LateralGap>>(sliceCount);
            var freeColumns = new List<bool>(columnCount);
            portalGapCount = 0;
            for (var slice = 0; slice < sliceCount; slice++)
            {
                var depth = -DepthHalfSpan + slice * DepthStep;
                freeColumns.Clear();
                for (var column = 0; column < columnCount; column++)
                {
                    var lateral = -LateralHalfSpan + column * LateralStep;
                    var foot = new Vector3(center.x, floorY, center.z) + right * lateral + forward * depth;
                    freeColumns.Add(!Physics.CheckCapsule(foot + Vector3.up * bottomOffset, foot + Vector3.up * topOffset, probeRadius, mask, QueryTriggerInteraction.Ignore));
                }

                var gaps = LateralGapFinder.FindGaps(freeColumns, -LateralHalfSpan, LateralStep);
                if (slice == portalSlice)
                {
                    portalGapCount = gaps.Count;
                }

                gapsPerSlice.Add(gaps);
            }

            return PassageCorridorFinder.SelectCorridorPortal(gapsPerSlice, portalSlice, minWidth, minOverlap, 0f);
        }

        // Lowest floor hit across the opening: clutter under the center sample (a crate top) must
        // not raise the probe band and hide low obstacles elsewhere in the doorway.
        private static bool TryFindFloor(Vector3 center, Vector3 right, out float floorY)
        {
            floorY = float.PositiveInfinity;
            var found = false;
            for (var lateral = -2f; lateral <= 2f + 1e-3f; lateral += 1f)
            {
                var origin = center + right * lateral + Vector3.up * FloorProbeHeight;
                if (Physics.Raycast(origin, Vector3.down, out var hit, FloorProbeDepth, ProbeMask, QueryTriggerInteraction.Ignore)
                    && hit.normal.y >= 0.7f)
                {
                    floorY = Mathf.Min(floorY, hit.point.y);
                    found = true;
                }
            }

            if (!found)
            {
                floorY = center.y;
            }

            return found;
        }
    }
}
