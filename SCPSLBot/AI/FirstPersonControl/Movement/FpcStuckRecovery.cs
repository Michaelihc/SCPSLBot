using Interactables.Interobjects;
using Interactables.Interobjects.DoorUtils;
using LabLogger = LabApi.Features.Console.Logger;
using MapGeneration;
using UnityEngine;

namespace SCPSLBot.AI.FirstPersonControl.Movement
{
    /// <summary>
    /// Engine glue for <see cref="StuckRecoveryPolicy"/>: measures progress toward the current
    /// waypoint, and turns the policy's escalation ladder into door interactions, sideways nudges,
    /// native jumps, short back-offs, replans, crossing penalties, and finally a goal-abandon signal
    /// that the owning behavior (roam / order) consumes.
    /// </summary>
    internal sealed class FpcStuckRecovery
    {
        private static readonly int DoorMask = LayerMask.GetMask("Door");
        private static float nextStuckLogAt;

        private readonly FpcBotPlayer botPlayer;
        private readonly StuckRecoveryPolicy policy = new();
        private bool abandonRequested;

        public FpcStuckRecovery(FpcBotPlayer botPlayer)
        {
            this.botPlayer = botPlayer;
        }

        public float StuckSeconds => policy.StuckSeconds;
        public int ReplansAtSpot => policy.ReplansAtSpot;
        public StuckRecoveryActions LastActions { get; private set; }

        /// <summary>Set once the ladder gave up on the current goal; cleared by <see cref="ConsumeAbandonRequest"/>.</summary>
        public bool ConsumeAbandonRequest()
        {
            var requested = abandonRequested;
            abandonRequested = false;
            return requested;
        }

        public void Reset()
        {
            var position = botPlayer.PlayerPosition;
            policy.Reset(position.x, position.z);
            policy.ResetEscalation();
            abandonRequested = false;
            LastActions = StuckRecoveryActions.None;
        }

        /// <summary>
        /// Runs once per tick after movement intent was set. <paramref name="waypoint"/> is the
        /// steering target the navigator produced this tick.
        /// </summary>
        public void Tick(Vector3 waypoint)
        {
            var move = botPlayer.Move;
            var fpcModule = botPlayer.FpcRole.FpcModule;
            var intendedWorldMove = Vector3.ProjectOnPlane(fpcModule.transform.TransformDirection(move.DesiredLocalDirection), Vector3.up);
            var hasMoveIntent = intendedWorldMove.sqrMagnitude >= 0.1f;

            var position = botPlayer.PlayerPosition;
            var waypointDistance = Vector3.Distance(
                Vector3.ProjectOnPlane(position, Vector3.up),
                Vector3.ProjectOnPlane(waypoint, Vector3.up));

            DoorVariant blockingDoor = null;
            var doorAhead = hasMoveIntent && TryGetClosedDoorAhead(out blockingDoor);
            var actions = policy.Tick(Time.time, hasMoveIntent, position.x, position.z, waypointDistance, doorAhead, botPlayer.Navigator.ProgressStamp);
            LastActions = actions;
            if (actions == StuckRecoveryActions.None)
            {
                return;
            }

            var moveDir = intendedWorldMove.normalized;

            if ((actions & StuckRecoveryActions.OpenDoor) != 0 && blockingDoor != null)
            {
                // The camera ray may hit the leaf rather than the interactable panel; fall back to
                // the nearest interactable collider of that same door.
                if (!botPlayer.OpenDoor(blockingDoor, 2.5f))
                {
                    botPlayer.InteractDoorDirectly(blockingDoor, 2.5f);
                }
            }

            if ((actions & StuckRecoveryActions.BackOff) != 0)
            {
                move.DesiredLocalDirection = fpcModule.transform.InverseTransformDirection(-moveDir);
            }
            else if ((actions & StuckRecoveryActions.Nudge) != 0)
            {
                var side = Vector3.Cross(Vector3.up, moveDir).normalized * policy.NudgeSign;
                var nudgedWorld = Vector3.Normalize(moveDir + side);
                move.DesiredLocalDirection = fpcModule.transform.InverseTransformDirection(nudgedWorld);
            }

            if ((actions & StuckRecoveryActions.Jump) != 0)
            {
                fpcModule.Motor.JumpController.ForceJump(fpcModule.JumpSpeed);
            }

            if ((actions & StuckRecoveryActions.AvoidCrossing) != 0)
            {
                botPlayer.Navigator.ReportBlockedCrossing();
            }
            else if ((actions & StuckRecoveryActions.Replan) != 0)
            {
                botPlayer.Navigator.ForceReplan();
            }

            if ((actions & StuckRecoveryActions.AbandonGoal) != 0)
            {
                abandonRequested = true;
            }

            if ((actions & (StuckRecoveryActions.Replan | StuckRecoveryActions.Jump)) != 0 && Time.time >= nextStuckLogAt)
            {
                nextStuckLogAt = Time.time + 2f;
                var room = RoomUtils.TryGetRoom(position, out var currentRoom) && currentRoom != null ? currentRoom.Name.ToString() : "none";
                var navigator = botPlayer.Navigator;
                var pathKind = navigator.HasPath ? "complete" : navigator.HasPartialPath ? "partial" : "none";
                LabLogger.Info($"[BotNav] STUCK bot={botPlayer.BotHub.PlayerHub?.nicknameSync?.MyNick} actions={actions} replans={policy.ReplansAtSpot} stuck={policy.StuckSeconds:F1} pos=({position.x:F1},{position.y:F1},{position.z:F1}) room={room} waypoint=({waypoint.x:F1},{waypoint.y:F1},{waypoint.z:F1}) waypointDistance={waypointDistance:F2} path={pathKind}/{navigator.PathNodeCount} obstacle={botPlayer.ObstacleAvoidance.LastObstacle}");
            }
        }

        private bool TryGetClosedDoorAhead(out DoorVariant door)
        {
            door = null;
            if (!Physics.Raycast(botPlayer.CameraPosition, botPlayer.CameraForward, out var doorHit, 2.5f, DoorMask))
            {
                return false;
            }

            var candidate = doorHit.collider.GetComponentInParent<DoorVariant>();
            if (candidate == null || candidate is ElevatorDoor || candidate.IsConsideredOpen())
            {
                return false;
            }

            door = candidate;
            return true;
        }
    }
}
