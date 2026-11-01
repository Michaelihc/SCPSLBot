using MapGeneration;
using PlayerRoles.FirstPersonControl;
using UnityEngine;

namespace SCPSLBot.Navigation
{
    /// <summary>
    /// A short straight route inside one room, validated against live collision for the role's own
    /// capsule. It reaches what a navigator cannot represent: floor the authored cells do not cover
    /// and low supports the runtime bake leaves unconnected. Doors stay in the mask, so a route
    /// never passes a closed leaf; player bodies are left to native collision.
    /// </summary>
    internal static class LocalSurfaceRoute
    {
        private const float MaxDistance = 6f;
        private const float SampleSpacing = 0.2f;
        private const float MaxGoalFloorOffset = 0.3f;
        private const float MinWalkableNormalY = 0.7f;
        private const float ProbeHeadroom = 0.2f;
        private const float StepRiseFraction = 0.9f;
        private const float StepClearanceSlack = 0.03f;
        private const float LaunchClearance = 0.08f;

        /// <summary>
        /// Probes the line from <paramref name="start"/> to the standing root <paramref name="goal"/>.
        /// Floors along it may differ by at most <paramref name="maxRise"/>. When walking the line
        /// is not possible, <paramref name="hop"/> is set and <paramref name="obstacle"/> is the last
        /// point the capsule walks to.
        /// </summary>
        public static bool TryProbe(Vector3 start, Vector3 goal, CharacterControllerSettingsPreset settings, float maxRise, out bool hop, out Vector3 obstacle)
        {
            hop = false;
            obstacle = default;
            var distance = Vector3.ProjectOnPlane(goal - start, Vector3.up).magnitude;
            if (distance > MaxDistance
                || !RoomUtils.TryGetRoom(start, out var room) || room == null
                || !RoomUtils.TryGetRoom(goal, out var goalRoom) || goalRoom != room)
            {
                return false;
            }

            var mask = FpcStateProcessor.Mask & ~LayerMask.GetMask("Player", "Hitbox");
            var rootHeight = settings.Height * 0.5f - settings.Center.y + settings.SkinWidth + 0.04f;
            if (!TryFloor(start + Vector3.up * ProbeHeadroom, rootHeight + maxRise + ProbeHeadroom * 2f, room, mask, out var startFloor)
                || !TryFloor(goal + Vector3.up * ProbeHeadroom, rootHeight + ProbeHeadroom * 2f, room, mask, out var goalFloor)
                || Mathf.Abs(goal.y - goalFloor - rootHeight) > MaxGoalFloorOffset)
            {
                return false;
            }

            var highest = Mathf.Max(startFloor, goalFloor);
            var lowest = Mathf.Min(startFloor, goalFloor);
            var probeTop = highest + maxRise + ProbeHeadroom;
            var previous = new Vector3(start.x, startFloor, start.z);
            var count = Mathf.Max(1, Mathf.CeilToInt(distance / SampleSpacing));
            for (var index = 0; index <= count; index++)
            {
                var point = Vector3.Lerp(start, goal, (float)index / count);
                point.y = probeTop;
                if (!TryFloor(point, maxRise * 2f + ProbeHeadroom * 2f, room, mask, out var floor))
                {
                    return false;
                }

                highest = Mathf.Max(highest, floor);
                lowest = Mathf.Min(lowest, floor);
                if (highest - lowest > maxRise + 0.01f)
                {
                    return false;
                }

                point.y = floor;
                if (!hop && (floor - previous.y > settings.StepOffset * StepRiseFraction
                             || !ClearSegment(previous, point, settings.StepOffset + StepClearanceSlack, settings, mask)))
                {
                    hop = true;
                    obstacle = previous;
                }

                previous = point;
            }

            if (!hop)
            {
                return true;
            }

            // The whole capsule must fit above the step, including the upward launch from here.
            // Native FPC supplies the jump, gravity and the actual collision.
            var raisedStart = new Vector3(start.x, highest, start.z);
            var raisedGoal = new Vector3(goal.x, highest, goal.z);
            return ClearSegment(new Vector3(start.x, startFloor, start.z), raisedStart, LaunchClearance, settings, mask)
                   && ClearSegment(raisedStart, raisedGoal, LaunchClearance, settings, mask);
        }

        private static bool TryFloor(Vector3 origin, float depth, RoomIdentifier room, int mask, out float floor)
        {
            floor = 0f;
            if (!Physics.Raycast(origin, Vector3.down, out var hit, depth, mask, QueryTriggerInteraction.Ignore)
                || hit.normal.y < MinWalkableNormalY
                || !RoomUtils.TryGetRoom(hit.point + Vector3.up * 0.5f, out var hitRoom) || hitRoom != room)
            {
                return false;
            }

            floor = hit.point.y;
            return true;
        }

        private static bool ClearSegment(Vector3 from, Vector3 to, float clearance, CharacterControllerSettingsPreset settings, int mask)
        {
            // A native capsule rests inside its skin band at a step face: treat that as contact,
            // not as overlapping a wall, while keeping the role's full height.
            var radius = Mathf.Max(0.2f, settings.Radius - Mathf.Min(0.04f, settings.SkinWidth * 0.5f));
            var bottom = from + Vector3.up * (settings.Radius + clearance);
            var top = from + Vector3.up * (settings.Height - settings.Radius);
            var delta = to - from;
            return !Physics.CheckCapsule(bottom, top, radius, mask, QueryTriggerInteraction.Ignore)
                   && !Physics.CheckCapsule(bottom + delta, top + delta, radius, mask, QueryTriggerInteraction.Ignore)
                   && (delta.sqrMagnitude < 0.0001f
                       || !Physics.CapsuleCast(bottom, top, radius, delta.normalized, delta.magnitude, mask, QueryTriggerInteraction.Ignore));
        }
    }
}
