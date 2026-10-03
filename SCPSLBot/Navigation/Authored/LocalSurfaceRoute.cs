using MapGeneration;
using PlayerRoles.FirstPersonControl;
using UnityEngine;

namespace SCPSLBot.Navigation.Authored
{
    /// <summary>A short, live-collision route across missing authored coverage within one room.</summary>
    internal static class LocalSurfaceRoute
    {
        private const float MaxDistance = 6f;
        private const float SampleSpacing = 0.2f;
        private const float MaxRise = 0.9f;
        private const float JumpApproachDistance = 1.1f;

        public static bool TryProbe(Vector3 start, Vector3 goal, CharacterControllerSettingsPreset settings, out bool jump)
        {
            jump = false;
            var delta = Vector3.ProjectOnPlane(goal - start, Vector3.up);
            var distance = delta.magnitude;
            if (distance > MaxDistance || Mathf.Abs(goal.y - start.y) > 1.5f
                || !RoomUtils.TryGetRoom(start, out var room) || room == null
                || !RoomUtils.TryGetRoom(goal, out var goalRoom) || goalRoom != room)
            {
                return false;
            }

            // Keep doors in the mask: a local route must not bypass a locked leaf. Player bodies
            // are handled by native collision and must not make the target itself an obstruction.
            var mask = FpcStateProcessor.Mask & ~LayerMask.GetMask("Player", "Hitbox");
            var rootHeight = settings.Height * 0.5f - settings.Center.y + settings.SkinWidth + 0.04f;
            if (!TryFloor(start + Vector3.up * 0.2f, rootHeight + MaxRise + 0.3f, room, mask, out var startFloor)
                || !TryFloor(goal + Vector3.up * 0.2f, rootHeight + 0.4f, room, mask, out var goalFloor)
                || Mathf.Abs(goal.y - goalFloor - rootHeight) > 0.3f)
            {
                return false;
            }

            var count = Mathf.Max(1, Mathf.CeilToInt(distance / SampleSpacing));
            var highest = Mathf.Max(startFloor, goalFloor);
            var lowest = Mathf.Min(startFloor, goalFloor);
            var previous = new Vector3(start.x, startFloor, start.z);
            var walkable = true;
            var firstObstacle = float.PositiveInfinity;
            for (var index = 0; index <= count; index++)
            {
                var point = Vector3.Lerp(start, goal, (float)index / count);
                var origin = new Vector3(point.x, Mathf.Max(startFloor, goalFloor) + MaxRise + 0.2f, point.z);
                if (!TryFloor(origin, MaxRise * 2f + 0.35f, room, mask, out var floor))
                {
                    return false;
                }

                highest = Mathf.Max(highest, floor);
                lowest = Mathf.Min(lowest, floor);
                if (highest - lowest > MaxRise + 0.01f)
                {
                    return false;
                }

                point.y = floor;
                if (floor - previous.y > settings.StepOffset * 0.9f
                    || !ClearSegment(previous, point, settings.StepOffset + 0.03f, settings, mask))
                {
                    walkable = false;
                    firstObstacle = Mathf.Min(firstObstacle, distance * index / count);
                }
                previous = point;
            }

            if (walkable)
            {
                return true;
            }

            // The whole capsule must fit above the low step, including the upward launch at the
            // current position. Native FPC supplies the jump, gravity and actual collision.
            var raisedStart = new Vector3(start.x, highest, start.z);
            var raisedGoal = new Vector3(goal.x, highest, goal.z);
            if (!ClearSegment(new Vector3(start.x, startFloor, start.z), raisedStart, 0.08f, settings, mask)
                || !ClearSegment(raisedStart, raisedGoal, 0.08f, settings, mask))
            {
                return false;
            }

            jump = firstObstacle <= JumpApproachDistance;
            return true;
        }

        private static bool TryFloor(Vector3 origin, float depth, RoomIdentifier room, int mask, out float floor)
        {
            floor = 0f;
            if (!Physics.Raycast(origin, Vector3.down, out var hit, depth, mask, QueryTriggerInteraction.Ignore)
                || hit.normal.y < 0.7f
                || !RoomUtils.TryGetRoom(hit.point + Vector3.up * 0.5f, out var hitRoom) || hitRoom != room)
            {
                return false;
            }

            floor = hit.point.y;
            return true;
        }

        private static bool ClearSegment(Vector3 from, Vector3 to, float clearance, CharacterControllerSettingsPreset settings, int mask)
        {
            // Native capsules rest within their skin band at a step face. Treat that contact as
            // contact rather than an overlapping wall, while keeping the role's full height.
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
