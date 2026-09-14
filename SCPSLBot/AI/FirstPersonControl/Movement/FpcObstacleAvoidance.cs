using PlayerRoles.FirstPersonControl;
using SCPSLBot.Navigation;
using UnityEngine;

namespace SCPSLBot.AI.FirstPersonControl.Movement
{
    /// <summary>
    /// Local steering around whatever collider is directly ahead of the capsule: spawned clutter,
    /// props, another player. The navmesh only knows room-level walkability, so this is what keeps
    /// bots from grinding into a crate that sits on an otherwise valid path. One capsule cast per
    /// bot per tick; doors are excluded because door handling owns those interactions.
    /// </summary>
    internal sealed class FpcObstacleAvoidance
    {
        private const float LookAheadMeters = 1.1f;
        private const float MaxSteerWeight = 0.85f;
        private const float HeadOnDot = 0.35f;
        private const float SideProbeMeters = 1.5f;

        private static readonly int ExcludedLayers = LayerMask.GetMask("Player", "Hitbox", "Door");

        private readonly FpcBotPlayer botPlayer;

        public FpcObstacleAvoidance(FpcBotPlayer botPlayer)
        {
            this.botPlayer = botPlayer;
        }

        public string LastObstacle { get; private set; } = "none";

        /// <summary>
        /// Adjusts <paramref name="worldMoveDirection"/> (unit, horizontal) so the bot slides along
        /// an obstacle toward <paramref name="waypoint"/>. Returns the possibly adjusted direction.
        /// </summary>
        public Vector3 Adjust(Vector3 worldMoveDirection, Vector3 waypoint)
        {
            if (worldMoveDirection.sqrMagnitude < 1e-4f)
            {
                LastObstacle = "none";
                return worldMoveDirection;
            }

            var module = botPlayer.FpcRole?.FpcModule;
            if (module == null)
            {
                return worldMoveDirection;
            }

            var controller = module.CharController;
            var radius = controller != null ? controller.radius : NavigationAgentProfile.Radius;
            var height = controller != null ? controller.height : NavigationAgentProfile.Height;
            var center = botPlayer.PlayerPosition + (controller != null ? controller.center : Vector3.zero);

            // Skip the step-able band at the feet so stairs and low lips never register.
            var bottom = center + Vector3.down * (height * 0.5f - radius) + Vector3.up * NavigationAgentProfile.StepClearance;
            var top = center + Vector3.up * (height * 0.5f - radius);
            if (top.y < bottom.y + 0.05f)
            {
                top = bottom + Vector3.up * 0.05f;
            }

            var mask = FpcStateProcessor.Mask & ~ExcludedLayers;
            if (!Physics.CapsuleCast(bottom, top, radius * 0.9f, worldMoveDirection, out var hit, LookAheadMeters, mask, QueryTriggerInteraction.Ignore))
            {
                LastObstacle = "none";
                return worldMoveDirection;
            }

            var normal = Vector3.ProjectOnPlane(hit.normal, Vector3.up);
            if (normal.sqrMagnitude < 1e-4f)
            {
                // Floor/ceiling contact (a slope or a low ceiling); nothing to slide along.
                LastObstacle = "none";
                return worldMoveDirection;
            }

            normal.Normalize();
            LastObstacle = $"{hit.collider.name}@{hit.distance:F2}m";

            // Slide along the obstacle in whichever tangent direction brings us closer to the waypoint.
            var tangent = Vector3.Cross(Vector3.up, normal);
            var toWaypoint = Vector3.ProjectOnPlane(waypoint - botPlayer.PlayerPosition, Vector3.up);
            if (Vector3.Dot(tangent, toWaypoint) < 0f)
            {
                tangent = -tangent;
            }

            // Head-on (the waypoint is straight through the obstacle, e.g. a door frame beside the
            // opening): the waypoint gives no side preference, so take the side with more room.
            if (toWaypoint.sqrMagnitude > 1e-4f && Mathf.Abs(Vector3.Dot(tangent, toWaypoint.normalized)) < HeadOnDot)
            {
                var probeOrigin = center;
                var rightFree = Physics.Raycast(probeOrigin, tangent, out var rightHit, SideProbeMeters, mask, QueryTriggerInteraction.Ignore) ? rightHit.distance : SideProbeMeters;
                var leftFree = Physics.Raycast(probeOrigin, -tangent, out var leftHit, SideProbeMeters, mask, QueryTriggerInteraction.Ignore) ? leftHit.distance : SideProbeMeters;
                if (leftFree > rightFree + 0.1f)
                {
                    tangent = -tangent;
                }
            }

            var weight = Mathf.Clamp01(1f - hit.distance / LookAheadMeters) * MaxSteerWeight;
            var steered = worldMoveDirection * (1f - weight) + tangent * weight + normal * (weight * 0.25f);
            steered = Vector3.ProjectOnPlane(steered, Vector3.up);
            return steered.sqrMagnitude < 1e-4f ? tangent : steered.normalized;
        }
    }
}
