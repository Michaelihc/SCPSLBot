using PlayerRoles.FirstPersonControl;
using SCPSLBot.Navigation;
using UnityEngine;

namespace SCPSLBot.AI.FirstPersonControl.Movement
{
    /// <summary>
    /// Takes over from the navigator for a goal a few meters away that it cannot deliver the bot to:
    /// off its surface, or raised above the floor it walks on. <see cref="LocalSurfaceRoute"/>
    /// validates the straight line against live collision at a bounded rate, and a low step on that
    /// line is taken with the native jump. Everything else stays with the navigator.
    /// </summary>
    internal sealed class LocalRouteTraversal
    {
        // Taller than any step the capsule walks up, so a plain floor goal never probes.
        private const float RaisedGoalMeters = 0.45f;
        private const float ProbeIntervalSeconds = 0.2f;
        private const float GoalShiftMeters = 0.5f;
        // The jump's lift is spent in about a quarter second, so take off just short of the step.
        private const float TakeoffMeters = 0.7f;
        private const float JumpRetrySeconds = 0.5f;

        private readonly FpcBotPlayer botPlayer;

        private bool active;
        private Vector3 routeGoal;
        private Vector3 obstacle;
        private float nextProbeAt;
        private float nextJumpAt;

        public LocalRouteTraversal(FpcBotPlayer botPlayer)
        {
            this.botPlayer = botPlayer;
        }

        /// <summary>The route crosses a step the bot has to jump: steer straight at it, not around it.</summary>
        public bool NeedsHop { get; private set; }

        public void Reset()
        {
            active = false;
            NeedsHop = false;
            nextProbeAt = 0f;
            nextJumpAt = 0f;
        }

        /// <summary>True when a validated local route owns this movement to <paramref name="goal"/>.</summary>
        public bool TryUpdate(Vector3 goal, bool navigatorReachesGoal)
        {
            var position = botPlayer.PlayerPosition;
            if (navigatorReachesGoal && goal.y - position.y <= RaisedGoalMeters)
            {
                active = false;
                return false;
            }

            var module = botPlayer.FpcRole.FpcModule;
            var goalShifted = (goal - routeGoal).sqrMagnitude > GoalShiftMeters * GoalShiftMeters;
            // In the air the floor below the capsule changes: finish the hop that was started.
            if (active && !goalShifted && !module.IsGrounded)
            {
                return true;
            }

            if (goalShifted || Time.time >= nextProbeAt)
            {
                nextProbeAt = Time.time + ProbeIntervalSeconds;
                routeGoal = goal;
                active = LocalSurfaceRoute.TryProbe(position, goal, module.CharacterControllerSettings, MaxHopRise(module), out var hop, out obstacle);
                NeedsHop = active && hop;
            }

            return active;
        }

        /// <summary>Takes the native jump once the capsule is close to the step.</summary>
        public void JumpIfDue()
        {
            var module = botPlayer.FpcRole.FpcModule;
            if (!module.IsGrounded || Time.time < nextJumpAt)
            {
                return;
            }

            var toObstacle = obstacle - botPlayer.PlayerPosition;
            toObstacle.y = 0f;
            if (toObstacle.sqrMagnitude <= TakeoffMeters * TakeoffMeters)
            {
                nextJumpAt = Time.time + JumpRetrySeconds;
                module.Motor.JumpController.ForceJump(module.JumpSpeed);
            }
        }

        // The jump apex plus the rise the capsule steps up at the top of the arc.
        private static float MaxHopRise(FirstPersonMovementModule module)
        {
            var gravity = Mathf.Abs(module.Motor.GravityController.Gravity.y);
            var apex = gravity > 0.01f ? module.JumpSpeed * module.JumpSpeed / (2f * gravity) : 0f;
            return apex + module.CharacterControllerSettings.StepOffset;
        }
    }
}
