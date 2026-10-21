using Interactables.Interobjects.DoorUtils;
using MapGeneration;
using MapGeneration.Distributors;
using SCPSLBot.AI.FirstPersonControl.Perception.Senses;
using SCPSLBot.Navigation;
using System.Collections.Generic;
using UnityEngine;
using LabLogger = LabApi.Features.Console.Logger;

namespace SCPSLBot.AI.FirstPersonControl.Movement
{
    /// <summary>
    /// The single movement executor every caller goes through (<see cref="FpcBotPlayer.MoveToPosition(Vector3)"/>):
    /// combat chase, orders, behaviors and GOAP actions only name a goal. Per call it follows the
    /// navigator's path with the steering/avoidance layers, opens the doors on the path (keycard
    /// swaps included, see <see cref="DoorTraversal"/>) and takes over completely while the path
    /// crosses an elevator (<see cref="ElevatorTraversal"/>). Crossings it cannot use are reported
    /// to the navigator and replanned; when that keeps failing, or the stuck ladder gives up, the
    /// owner of the goal is told through <see cref="ConsumeAbandonRequest"/>.
    /// <para>
    /// Callers may move several times per frame (combat steers at a target and at a door on the
    /// way); <see cref="EndFrame"/> runs once per bot update and ticks stuck recovery once.
    /// Per-call cost: one navigator query (replans are rate-limited inside the navigator), a
    /// handful of cached door crossings, one capsule cast for local avoidance.
    /// </para>
    /// </summary>
    internal sealed class BotLocomotion
    {
        private const float ElevatorStartMeters = 3f;
        private const float ElevatorStartVerticalMeters = 2f;
        // The navigator holds its waypoint on the link start while the crossing is next.
        private const float LinkStartWaypointMeters = 0.6f;
        private const float CrossingLogIntervalSeconds = 2f;
        private const float GoalMismatchGraceSeconds = 1.5f;
        private const float BlockedMemorySeconds = 30f;
        private const float AbandonAfterBlockedSeconds = 1f;

        private readonly FpcBotPlayer botPlayer;
        private readonly DoorTraversal doors;
        private readonly ElevatorTraversal elevator;

        private bool movedSinceEndFrame;
        private Vector3 lastWaypoint;
        private bool abandonRequested;

        private DoorVariant lastBlockedDoor;
        private float lastBlockedDoorAt = float.NegativeInfinity;
        private Vector3 lastBlockedLinkOrigin;
        private float lastBlockedLinkAt = float.NegativeInfinity;
        private float nextCrossingLogAt;

        private readonly struct StructureObstacle
        {
            public StructureObstacle(Transform transform, float extent, Collider[] colliders)
            {
                Transform = transform;
                Extent = extent;
                Colliders = colliders;
            }

            public Transform Transform { get; }
            public float Extent { get; }
            public Collider[] Colliders { get; }
        }

        private readonly List<StructureObstacle> roomStructures = new();
        private readonly List<StructureSpawnpoint> structureSpawnpoints = new();
        private RoomIdentifier structuresRoom;
        private int structuresGeneration = -1;

        public BotLocomotion(FpcBotPlayer botPlayer)
        {
            this.botPlayer = botPlayer;
            doors = new DoorTraversal(botPlayer);
            elevator = new ElevatorTraversal(botPlayer, this);
            StuckRecovery = new FpcStuckRecovery(botPlayer);
            ObstacleAvoidance = new FpcObstacleAvoidance(botPlayer);
        }

        public FpcStuckRecovery StuckRecovery { get; }
        public FpcObstacleAvoidance ObstacleAvoidance { get; }

        /// <summary>True while an elevator crossing runs or the bot waits at a door being opened (waiting is progress, not a stall).</summary>
        public bool IsTraversingCrossing => elevator.IsActive || doors.IsHolding;

        /// <summary>True while the bot is inside a moving (or about to move) elevator cabin.</summary>
        public bool IsRidingElevator => elevator.CurrentStage is ElevatorTraversal.Stage.Send or ElevatorTraversal.Stage.Ride;

        /// <summary>True while an elevator crossing is in progress (any stage).</summary>
        public bool IsCrossingElevator => elevator.IsActive;

        public string DiagnosticState => elevator.DiagnosticState ?? doors.DiagnosticState;

        /// <summary>
        /// True once since the last call when the current goal should be given up: a crossing on
        /// its route stays blocked after replanning, or the stuck ladder ran out of options.
        /// Owners that pick their own goals (exploration) consume it; owners with fixed goals
        /// (orders, combat) discard it with <see cref="DiscardAbandonRequest"/>.
        /// </summary>
        public bool ConsumeAbandonRequest()
        {
            var requested = abandonRequested;
            abandonRequested = false;
            return requested;
        }

        public void DiscardAbandonRequest()
        {
            abandonRequested = false;
        }

        public void MoveTo(Vector3 goal, out Vector3 waypoint)
        {
            movedSinceEndFrame = true;
            var navigator = botPlayer.Navigator;

            if (elevator.IsActive)
            {
                if (elevator.IsCommitted)
                {
                    TickElevator(navigator, default, false, out waypoint);
                    lastWaypoint = waypoint;
                    return;
                }

                waypoint = navigator.GetPositionTowards(goal);
                if (navigator.TryGetElevatorLink(out var link) && elevator.IsSameLink(link))
                {
                    elevator.Goal = goal;
                    elevator.LastGoalMatchAt = Time.time;
                    TickElevator(navigator, waypoint, true, out waypoint);
                    lastWaypoint = waypoint;
                    return;
                }

                if (Time.time - elevator.LastGoalMatchAt <= GoalMismatchGraceSeconds)
                {
                    // Another goal in the same frame (combat's door helper) or a goal that moved
                    // for a moment: the landing still owns the body.
                    TickElevator(navigator, waypoint, false, out waypoint);
                    lastWaypoint = waypoint;
                    return;
                }

                elevator.Cancel();
            }
            else
            {
                waypoint = navigator.GetPositionTowards(goal);
                if (TryStartElevator(navigator, goal, waypoint))
                {
                    TickElevator(navigator, waypoint, true, out waypoint);
                    lastWaypoint = waypoint;
                    return;
                }
            }

            lastWaypoint = waypoint;
            SteerAlongPath(waypoint);
            HandleDoors(navigator);
        }

        /// <summary>Runs once per bot update after the controlling layer moved (or did not move) the bot.</summary>
        public void EndFrame()
        {
            doors.EndFrame();
            if (!movedSinceEndFrame)
            {
                StuckRecovery.Reset();
                if (elevator.IsActive && !elevator.IsCommitted && Time.time - elevator.LastGoalMatchAt > GoalMismatchGraceSeconds)
                {
                    elevator.Cancel();
                }
            }
            else if (elevator.IsActive)
            {
                // The elevator stages bound themselves; nudges, jumps and replans would only
                // shove the bot off the landing or out of the cabin.
                StuckRecovery.Reset();
            }
            else
            {
                StuckRecovery.Tick(lastWaypoint);
            }

            movedSinceEndFrame = false;
            if (StuckRecovery.ConsumeAbandonRequest())
            {
                RequestAbandon("stuck recovery exhausted");
            }
        }

        /// <summary>The controlling layer deliberately stands still this frame (hold order, idle).</summary>
        public void Stop()
        {
            StandStill();
            doors.Idle();
            StuckRecovery.Reset();
            if (elevator.IsActive && !elevator.IsCommitted)
            {
                elevator.Cancel();
            }

            movedSinceEndFrame = false;
        }

        /// <summary>Role change, death or disposal: forget every crossing and give the hands back.</summary>
        public void Reset()
        {
            elevator.Cancel();
            doors.Reset();
            StuckRecovery.Reset();
            abandonRequested = false;
            movedSinceEndFrame = false;
            lastBlockedDoor = null;
            lastBlockedDoorAt = float.NegativeInfinity;
            lastBlockedLinkAt = float.NegativeInfinity;
            structuresRoom = null;
            roomStructures.Clear();
        }

        /// <summary>Opens <paramref name="door"/> now if this bot may (keycard swap included); used by the stuck ladder.</summary>
        public bool TryOpenDoor(DoorVariant door) => doors.TryOpenNow(door);
        internal bool TryCloseDoor(DoorVariant door, float range) => doors.TryCloseNow(door, range);

        #region Steering

        /// <summary>Walk straight at <paramref name="target"/> without the navigator (elevator landings and cabins).</summary>
        public void SteerDirect(Vector3 target)
        {
            SteerToPosition(target);
        }

        public void StandStill()
        {
            botPlayer.Move.DesiredLocalDirection = Vector3.zero;
        }

        private void SteerAlongPath(Vector3 waypoint)
        {
            SteerToPosition(waypoint);
            SteerAwayFromStructures();
            SteerAroundAhead(waypoint);
        }

        private void SteerToPosition(Vector3 positionTowardsGoal)
        {
            var fpcRole = botPlayer.FpcRole;
            var relativePos = positionTowardsGoal - fpcRole.CameraPosition;
            var relativeHorizontalPos = Vector3.ProjectOnPlane(relativePos, Vector3.up);
            var turnPosition = relativeHorizontalPos + fpcRole.CameraPosition;

            botPlayer.Look.ToPosition(turnPosition);

            if (relativeHorizontalPos.sqrMagnitude < 1e-4f)
            {
                botPlayer.Move.DesiredLocalDirection = Vector3.zero;
                return;
            }

            var dirTowardsTarget = Vector3.Normalize(relativeHorizontalPos);

            // Feed the exact world direction through the motor's local-input boundary even when the
            // first waypoint is behind the spawn facing. Waiting for mouse-look before emitting any
            // input can deadlock native dummies whose rotation update is input-driven. Transforming
            // this local vector back in FpcMotorPatches preserves the intended world direction while
            // native FPC continues to own collision, gravity, and speed.
            botPlayer.Move.DesiredLocalDirection = fpcRole.FpcModule.transform.InverseTransformDirection(dirTowardsTarget);
        }

        // Generic local avoidance for whatever collider is directly ahead (spawned clutter, props,
        // other players). Runs after the structure-specific steering so its result is included.
        private void SteerAroundAhead(Vector3 positionTowardsGoal)
        {
            var desired = botPlayer.Move.DesiredLocalDirection;
            if (desired.sqrMagnitude < 1e-4f)
            {
                return;
            }

            var moduleTransform = botPlayer.FpcRole.FpcModule.transform;
            var moveDirection = Vector3.ProjectOnPlane(moduleTransform.TransformDirection(desired), Vector3.up);
            if (moveDirection.sqrMagnitude < 1e-4f)
            {
                return;
            }

            var adjusted = ObstacleAvoidance.Adjust(moveDirection.normalized, positionTowardsGoal);
            botPlayer.Move.DesiredLocalDirection = moduleTransform.InverseTransformDirection(adjusted);
        }

        // Workstations and SCP pedestals are wider than their navmesh footprint suggests; push the
        // move direction out along the structure's facing when the capsule is about to clip one.
        private void SteerAwayFromStructures()
        {
            var roomWithin = botPlayer.Perception.GetSense<RoomSightSense>()?.RoomWithin;
            if (!roomWithin)
            {
                return;
            }

            RefreshRoomStructures(roomWithin);
            if (roomStructures.Count == 0)
            {
                return;
            }

            var moduleTransform = botPlayer.FpcRole.FpcModule.transform;
            var playerPosition = botPlayer.PlayerPosition;
            var moveDirection = moduleTransform.TransformDirection(botPlayer.Move.DesiredLocalDirection);
            var controller = botPlayer.FpcRole.FpcModule.CharController;
            var playerRadius = controller != null ? controller.radius : NavigationAgentProfile.Radius;
            var playerHeight = controller != null ? controller.height : NavigationAgentProfile.Height;
            var playerBottomPosition = playerPosition + Vector3.down * (playerHeight / 2f);
            var movementRay = new Ray(playerBottomPosition, moveDirection);
            var nextPosition = playerBottomPosition + moveDirection;
            var playerRadiusSqr = playerRadius * playerRadius;

            foreach (var structure in roomStructures)
            {
                if (structure.Transform == null)
                {
                    continue;
                }

                var obstructing = false;
                foreach (var collider in structure.Colliders)
                {
                    if (collider != null
                        && (collider.Raycast(movementRay, out _, 1f)
                            || (nextPosition - collider.ClosestPointOnBounds(nextPosition)).sqrMagnitude < playerRadiusSqr))
                    {
                        obstructing = true;
                        break;
                    }
                }

                if (!obstructing)
                {
                    continue;
                }

                var obstructingForward = structure.Transform.forward;
                var obstructingPlane = new Plane(obstructingForward, structure.Transform.position);
                var obstructingDepth = Mathf.Max(structure.Extent + playerRadius - obstructingPlane.GetDistanceToPoint(playerPosition + moveDirection), 0f);
                moveDirection = Vector3.Normalize(moveDirection + obstructingForward * obstructingDepth);
                botPlayer.Move.DesiredLocalDirection = moduleTransform.InverseTransformDirection(moveDirection);
                return;
            }
        }

        // Rooms change rarely; walking the room hierarchy once per room entry replaces the old
        // per-call GetComponentsInChildren scan.
        private void RefreshRoomStructures(RoomIdentifier room)
        {
            var generation = NavigationSystem.Instance.MapGeneration;
            if (room == structuresRoom && generation == structuresGeneration)
            {
                return;
            }

            structuresRoom = room;
            structuresGeneration = generation;
            roomStructures.Clear();
            room.GetComponentsInChildren(structureSpawnpoints);
            foreach (var spawnpoint in structureSpawnpoints)
            {
                var structure = spawnpoint != null ? spawnpoint.GetComponentInChildren<SpawnableStructure>() : null;
                if (!structure)
                {
                    continue;
                }

                float extent;
                switch (structure.StructureType)
                {
                    case StructureType.Workstation:
                        extent = 1.25f;
                        break;
                    case StructureType.ScpPedestal:
                        extent = .75f;
                        break;
                    default:
                        continue;
                }

                roomStructures.Add(new StructureObstacle(structure.transform, extent, structure.GetComponentsInChildren<Collider>()));
            }

            structureSpawnpoints.Clear();
        }

        #endregion

        #region Crossings

        private bool TryStartElevator(IBotNavigator navigator, Vector3 goal, Vector3 waypoint)
        {
            if (!navigator.TryGetElevatorLink(out var link))
            {
                return false;
            }

            // Only once the link start is the very next corner: a landing 3 m away behind a wall
            // or a closed door is still reached by path following first.
            var position = botPlayer.PlayerPosition;
            var feetY = position.y - NavigationAgentProfile.RootHeight;
            if (HorizontalDistance(waypoint, link.Origin) > LinkStartWaypointMeters
                || HorizontalDistance(position, link.Origin) > ElevatorStartMeters
                || Mathf.Abs(feetY - link.Origin.y) > ElevatorStartVerticalMeters)
            {
                return false;
            }

            if (Time.time - lastBlockedLinkAt <= BlockedMemorySeconds
                && HorizontalDistance(link.Origin, lastBlockedLinkOrigin) <= 1f
                && Mathf.Abs(link.Origin.y - lastBlockedLinkOrigin.y) <= 1f)
            {
                // Replanned through the same unusable elevator: there is no other way from here.
                if (Time.time - lastBlockedLinkAt > AbandonAfterBlockedSeconds)
                {
                    RequestAbandon("route needs an elevator that is blocked");
                }

                return false;
            }

            return elevator.TryBegin(link, goal);
        }

        /// <param name="pathWaypoint">The navigator's waypoint this call (valid when <paramref name="pathWaypointValid"/>).</param>
        private void TickElevator(IBotNavigator navigator, Vector3 pathWaypoint, bool pathWaypointValid, out Vector3 waypoint)
        {
            var status = elevator.Tick();
            waypoint = elevator.CurrentTarget;
            switch (status)
            {
                case ElevatorTraversalStatus.Running:
                    if (elevator.CurrentStage == ElevatorTraversal.Stage.Approach)
                    {
                        // Walking to the landing is ordinary path following, doors included.
                        waypoint = pathWaypointValid ? pathWaypoint : elevator.Link.Origin;
                        SteerAlongPath(waypoint);
                        HandleDoors(navigator);
                    }

                    break;

                case ElevatorTraversalStatus.Completed:
                    elevator.Cancel();
                    // The plan still holds the link; a fresh plan from the far landing never
                    // steers back at the origin landing should the exit end short of the corner.
                    navigator.ForceReplan();
                    break;

                case ElevatorTraversalStatus.Blocked:
                    var link = elevator.Link;
                    var group = elevator.Group;
                    var reason = elevator.BlockReason;
                    elevator.Cancel();
                    StandStill();
                    lastBlockedLinkOrigin = link.Origin;
                    lastBlockedLinkAt = Time.time;
                    navigator.ReportBlockedAt(link.Origin);
                    LogCrossing($"ELEVATOR_BLOCKED group={group} reason={reason ?? "unknown"} origin={Format(link.Origin)}");
                    break;
            }
        }

        private void RequestAbandon(string reason)
        {
            if (!abandonRequested)
            {
                LogCrossing($"ABANDON reason={reason}");
            }

            abandonRequested = true;
        }

        private void LogCrossing(string message)
        {
            var now = Time.time;
            if (now < nextCrossingLogAt)
            {
                return;
            }

            nextCrossingLogAt = now + CrossingLogIntervalSeconds;
            var name = botPlayer.BotHub?.PlayerHub?.nicknameSync?.MyNick ?? "?";
            LabLogger.Info($"[BotNav] {message} bot={name} pos={Format(botPlayer.PlayerPosition)}");
        }

        private static string Format(Vector3 value) => $"({value.x:F1},{value.y:F1},{value.z:F1})";

        private void HandleDoors(IBotNavigator navigator)
        {
            switch (doors.Tick(navigator))
            {
                case DoorTraversalResult.Hold:
                    StandStill();
                    break;

                case DoorTraversalResult.Blocked:
                    StandStill();
                    var door = doors.ActiveDoor;
                    var now = Time.time;
                    if (door != lastBlockedDoor || now - lastBlockedDoorAt > BlockedMemorySeconds)
                    {
                        lastBlockedDoor = door;
                        lastBlockedDoorAt = now;
                        if (door != null)
                        {
                            navigator.ReportBlockedAt(door.transform.position);
                            LogCrossing($"DOOR_BLOCKED door={door.name} reason={doors.DiagnosticState ?? "unknown"} at={Format(door.transform.position)}");
                        }
                    }
                    else if (now - lastBlockedDoorAt > AbandonAfterBlockedSeconds)
                    {
                        // The replan still runs through the same door: nothing else reaches the goal.
                        RequestAbandon($"route still runs through blocked door {(door != null ? door.name : "?")}");
                    }

                    break;
            }
        }

        #endregion

        private static float HorizontalDistance(Vector3 a, Vector3 b)
        {
            var dx = a.x - b.x;
            var dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }
    }
}
