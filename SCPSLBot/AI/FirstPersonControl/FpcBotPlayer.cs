using Interactables;
using Interactables.Interobjects;
using Interactables.Interobjects.DoorUtils;
using MapGeneration;
using MapGeneration.Distributors;
using PlayerRoles;
using PlayerRoles.FirstPersonControl;
using PlayerRoles.Spectating;
using SCPSLBot.AI.FirstPersonControl.Combat;
using SCPSLBot.AI.FirstPersonControl.Looking;
using SCPSLBot.AI.FirstPersonControl.Mind;
using SCPSLBot.AI.FirstPersonControl.Movement;
using SCPSLBot.AI.FirstPersonControl.Perception.Senses;
using SCPSLBot.AI.FirstPersonControl.Perception.Senses.Sight;
using SCPSLBot.AI.FirstPersonControl.Roaming;
using SCPSLBot.Components;
using SCPSLBot.Presentation;
using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Profiling;

namespace SCPSLBot.AI.FirstPersonControl
{
    internal partial class FpcBotPlayer : IBotPlayer, IDisposable
    {
        private static readonly int DoorMask = LayerMask.GetMask("Door");
        private static readonly int DoorAndGlassMask = LayerMask.GetMask("Door", "Glass");
        private const float PerceptionIntervalSeconds = 0.1f;

        public FpcStandardRoleBase FpcRole { get; set; }

        public BotHub BotHub { get; }
        public FpcBotPerception Perception { get; }
        public PerceptionComponent PerceptionComponent { get; private set; }
        public FpcMindRunner MindRunner { get; }

        public FpcBotCombat Combat { get; }
        public FpcZoneRoam ZoneRoam { get; }
        public FpcBotNavigator Navigator { get; }

        public FpcLook Look { get; }
        public FpcMove Move { get; }

        public Vector3 PlayerPosition { get; private set; }
        public Vector3 PlayerForward { get; private set; }

        public Vector3 CameraPosition { get; private set; }
        public Vector3 CameraForward { get; private set; }

        private Vector3 stuckAnchorPosition;
        private float stuckAnchorTime;
        private float nextStuckJumpTime;
        private int stuckNudgeDirection = 1;
        private float nextStuckNudgeFlipTime;
        private float nextStuckDoorTime;
        private float nextStuckReplanTime;
        private float nextPerceptionUpdateTime;

        public FpcBotPlayer(BotHub botHub)
        {
            BotHub = botHub;
            try
            {
                Perception = new FpcBotPerception(this);
                MindRunner = new FpcMindRunner();

                Combat = new(this);
                ZoneRoam = new(this);
                Navigator = new(this);
                Look = new(this);
                Move = new(this);

                FpcMindFactory.BuildMind(MindRunner, this, Perception);

                MindRunner.SubscribeToBeliefUpdates();
                nextPerceptionUpdateTime = Time.time + Mathf.Abs(botHub.PlayerHub.PlayerId % 10) * 0.01f;
            }
            catch
            {
                try
                {
                    MindRunner?.Dispose();
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                }

                try
                {
                    Perception?.Dispose();
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                }

                throw;
            }
        }

        public IEnumerator<JobHandle> Update()
        {
            if (isDisposed)
            {
                yield break;
            }

            var playerTransform = FpcRole.transform;
            this.PlayerPosition = playerTransform.position;
            this.PlayerForward = playerTransform.forward;

            var cameraTransform = BotHub.PlayerHub.PlayerCameraReference;
            this.CameraPosition = cameraTransform.position;
            this.CameraForward = cameraTransform.forward;

            // Explicit per-bot orders are the control-plane override used by test harnesses.
            // They only feed DesiredMove/Look; native FPC still owns gravity and collision.
            if (BotManager.Instance.TryGetOrderedTarget(BotHub.PlayerHub, out var orderedTargetPosition))
            {
                MoveToPosition(orderedTargetPosition, out var waypoint);
                BotManager.Instance.ObserveOrderTick(this, waypoint);
                DisplaySpectatorDiagnostics("ordered", "order target");
                JumpIfForwardMovementBlocked();
                yield break;
            }

            if (BotManager.Instance.ShouldHoldPosition(BotHub.PlayerHub))
            {
                Move.DesiredLocalDirection = Vector3.zero;
                ResetStuckJumpTracking();
                DisplaySpectatorDiagnostics("held", "none");
                yield break;
            }

            if (Combat.Tick())
            {
                DisplaySpectatorDiagnostics();
                JumpIfForwardMovementBlocked();
                yield break;
            }

            if (BotManager.Instance.TryGetPathTargetPosition(out var pathTargetPosition))
            {
                MoveToPosition(pathTargetPosition);
                JumpIfForwardMovementBlocked();
                yield break;
            }

            if (Time.time >= nextPerceptionUpdateTime)
            {
                nextPerceptionUpdateTime = Time.time + PerceptionIntervalSeconds;
                using (var updatePerceptionHandles = Perception.Update())
                {
                    while (updatePerceptionHandles.MoveNext())
                    {
                        yield return updatePerceptionHandles.Current;
                    }
                }
            }

            if (ShouldIdleOnSurfaceWithoutTarget())
            {
                Move.DesiredLocalDirection = Vector3.zero;
                ResetStuckJumpTracking();
                DisplaySpectatorDiagnostics("surface idle", "none");
                yield break;
            }

            if (!ShouldUseEscapeGoal())
            {
                if (!ZoneRoam.Tick())
                {
                    MindRunner.Tick();
                    DisplaySpectatorDiagnostics();
                }

                JumpIfForwardMovementBlocked();
                yield break;
            }

            MindRunner.Tick();

            // If the planner has nothing to do (e.g. no reachable escape route sensed yet), roam
            // instead of standing still for the rest of the round.
            if (MindRunner.RunningAction == null)
            {
                ZoneRoam.Tick();
            }

            DisplaySpectatorDiagnostics();
            JumpIfForwardMovementBlocked();

            yield break;
        }

        private bool ShouldUseEscapeGoal()
        {
            var role = BotHub.PlayerHub.roleManager.CurrentRole.RoleTypeId;
            return role is RoleTypeId.ClassD or RoleTypeId.Scientist;
        }

        private bool ShouldIdleOnSurfaceWithoutTarget()
        {
            return RoomUtils.TryGetRoom(PlayerPosition, out var room) && room.Zone == FacilityZone.Surface;
        }

        public void OnRoleChanged()
        {
            if (isDisposed)
            {
                return;
            }

            if (BotLog.Verbose) Debug.Log($"Bot got FPC role assigned.");

            PerceptionComponent = BotHub.PlayerHub.GetComponentInChildren<PerceptionComponent>();
            PerceptionComponent.enabled = true;

            nextPerceptionUpdateTime = Time.time;
            MindRunner.EvaluateGoalsToActions();
            ResetStuckJumpTracking();
        }

        #region Moving

        public void MoveToPosition(Vector3 goalPosition) => MoveToPosition(goalPosition, out _);
        public void MoveToPosition(Vector3 goalPosition, out Vector3 positionTowardsGoal)
        {
            positionTowardsGoal = Navigator.GetPositionTowards(goalPosition);
            SteerToPosition(positionTowardsGoal);
            SteerAwayFromObstacles();
        }

        private void SteerToPosition(Vector3 positionTowardsGoal)
        {
            var relativePos = positionTowardsGoal - this.FpcRole.CameraPosition;
            var relativeHorizontalPos = Vector3.ProjectOnPlane(relativePos, Vector3.up);
            var turnPosition = relativeHorizontalPos + this.FpcRole.CameraPosition;

            this.Look.ToPosition(turnPosition);

            if (relativeHorizontalPos.sqrMagnitude < 1e-4f)
            {
                this.Move.DesiredLocalDirection = Vector3.zero;
                return;
            }

            var dirTowardsTarget = Vector3.Normalize(relativeHorizontalPos);

            // Feed the exact world direction through the motor's local-input boundary even when the
            // first waypoint is behind the spawn facing. Waiting for mouse-look before emitting any
            // input can deadlock native dummies whose rotation update is input-driven. Transforming
            // this local vector back in FpcMotorPatches preserves the intended world direction while
            // native FPC continues to own collision, gravity, and speed.
            this.Move.DesiredLocalDirection = FpcRole.FpcModule.transform.InverseTransformDirection(dirTowardsTarget);
        }

        private readonly List<StructureSpawnpoint> structureSpawnpoints = new();
        private readonly List<Collider> spawnableStructureColliders = new();
        private void SteerAwayFromObstacles()
        {
            var roomSightSense = Perception.GetSense<RoomSightSense>();
            var roomWithin = roomSightSense.RoomWithin;
            if (!roomWithin)
            {
                return;
            }

            roomWithin.GetComponentsInChildren(structureSpawnpoints);
            if (structureSpawnpoints.Count < 1)
            {
                return;
            }

            var playerPosition = this.PlayerPosition;
            var moveDirection = this.FpcRole.FpcModule.transform.TransformDirection(this.Move.DesiredLocalDirection);
            var characterController = this.BotHub.PlayerHub.GetComponent<CharacterController>();
            var playerRadius = characterController.radius;
            var playerHeight = characterController.height;

            var playerBottomPosition = playerPosition + Vector3.down * (playerHeight / 2f);

            var obstructingStructure = (SpawnableStructure)null;
            var structureExtent = 0f;
            foreach (var spawnpoint in structureSpawnpoints)
            {
                var structure = spawnpoint.GetComponentInChildren<SpawnableStructure>();
                if (!structure)
                {
                    continue;
                }

                switch (structure.StructureType)
                {
                    case StructureType.Workstation:
                        structureExtent = 1.25f;
                        break;
                    case StructureType.ScpPedestal:
                        structureExtent = .75f;
                        break;
                    default:
                        continue;
                }

                structure.GetComponentsInChildren(spawnableStructureColliders);

                Collider obstructingCollider = null;
                var movementRay = new Ray(playerBottomPosition, moveDirection);
                var nextPosition = playerBottomPosition + moveDirection;
                var playerRadiusSqr = playerRadius * playerRadius;
                foreach (var collider in spawnableStructureColliders)
                {
                    if (collider.Raycast(movementRay, out _, 1f)
                        || (nextPosition - collider.ClosestPointOnBounds(nextPosition)).sqrMagnitude < playerRadiusSqr)
                    {
                        obstructingCollider = collider;
                        break;
                    }
                }

                if (obstructingCollider)
                {
                    obstructingStructure = structure;
                    break;
                }
            }

            if (obstructingStructure == null)
            {
                return;
            }

            var obstructingPosition = obstructingStructure.transform.position;
            var obstructingForward = obstructingStructure.transform.forward;

            var obstructingPlane = new Plane(obstructingForward, obstructingPosition);
            var obstructingDepth = Mathf.Max(structureExtent + playerRadius - obstructingPlane.GetDistanceToPoint(playerPosition + moveDirection), 0f);

            moveDirection = Vector3.Normalize(moveDirection + obstructingForward * obstructingDepth);
            this.Move.DesiredLocalDirection = FpcRole.FpcModule.transform.InverseTransformDirection(moveDirection);
        }

        private void JumpIfForwardMovementBlocked()
        {
            var desired = Move.DesiredLocalDirection;
            var intendedWorldMove = Vector3.ProjectOnPlane(
                FpcRole.FpcModule.transform.TransformDirection(desired),
                Vector3.up);

            if (intendedWorldMove.sqrMagnitude < 0.1f)
            {
                ResetStuckJumpTracking();
                return;
            }

            var horizontalPosition = Vector3.ProjectOnPlane(PlayerPosition, Vector3.up);
            var horizontalAnchor = Vector3.ProjectOnPlane(stuckAnchorPosition, Vector3.up);
            if (stuckAnchorTime <= 0f || Vector3.Distance(horizontalPosition, horizontalAnchor) > 0.35f)
            {
                stuckAnchorPosition = PlayerPosition;
                stuckAnchorTime = Time.time;
                return;
            }

            var stuckDuration = Time.time - stuckAnchorTime;
            if (stuckDuration < 0.7f)
            {
                return;
            }

            // Escalating unstick (cheap -> disruptive): open a door ahead, then a lateral nudge to
            // slip around corners, then a hop, then force the navigator to re-plan. This recovers
            // from the brief corner/doorway snags fast instead of standing still for 3 seconds.
            var moveDir = intendedWorldMove.normalized;

            if (Time.time >= nextStuckDoorTime
                && Physics.Raycast(CameraPosition, CameraForward, out var doorHit, 2.5f, DoorMask)
                && doorHit.collider.GetComponentInParent<DoorVariant>() is DoorVariant blockingDoor
                && blockingDoor is not ElevatorDoor
                && !blockingDoor.IsConsideredOpen())
            {
                nextStuckDoorTime = Time.time + 0.6f;
                OpenDoor(blockingDoor, 2.5f);
            }

            if (Time.time >= nextStuckNudgeFlipTime)
            {
                stuckNudgeDirection = -stuckNudgeDirection;
                nextStuckNudgeFlipTime = Time.time + 0.8f;
            }

            var side = Vector3.Cross(Vector3.up, moveDir).normalized * stuckNudgeDirection;
            var nudgedWorld = Vector3.Normalize(moveDir + side);
            Move.DesiredLocalDirection = FpcRole.FpcModule.transform.InverseTransformDirection(nudgedWorld);

            if (stuckDuration >= 1.5f && Time.time >= nextStuckJumpTime)
            {
                FpcRole.FpcModule.Motor.JumpController.ForceJump(FpcRole.FpcModule.JumpSpeed);
                nextStuckJumpTime = Time.time + 1f;
            }

            if (stuckDuration >= 2.5f && Time.time >= nextStuckReplanTime)
            {
                Navigator.ForceReplan();
                nextStuckReplanTime = Time.time + 2f;
                stuckAnchorPosition = PlayerPosition;
                stuckAnchorTime = Time.time;
            }
        }

        private void ResetStuckJumpTracking()
        {
            stuckAnchorPosition = PlayerPosition;
            stuckAnchorTime = 0f;
        }

        #endregion

        public void LookToPosition(Vector3 targetPosition)
        {
            // Only aim. The desired move direction is recomputed in world space every tick by the
            // caller (combat strafe / navigation), so rotating it here by the full look-to-target
            // rotation corrupted it and produced erratic combat/chase movement.
            Look.ToPosition(targetPosition);
        }

        #region Interaction

        public bool Interact(InteractableCollider interactableCollider)
        {
            if (interactableCollider.Target is not IServerInteractable interactable)
            {
                throw new InvalidOperationException("interactableCollider target is not server interactable.");
            }

            var hub = BotHub.PlayerHub;
            var playerCamera = hub.PlayerCameraReference;

            var isHit = interactableCollider
                .GetComponent<Collider>()
                .Raycast(new Ray(playerCamera.position, hub.PlayerCameraReference.forward), out var hit, 2f);

            if (isHit && hit.collider.GetComponent<InteractableCollider>() == interactableCollider)
            {
                interactable.ServerInteract(hub, interactableCollider.ColliderId);

                //Log.Debug($"ServerInteract(...) called on {interactable}");

                return true;
            }

            return false;
        }

        public bool OpenDoor(DoorVariant targetDoor, float maxInteractDistance)
        {
            var hub = BotHub.PlayerHub;
            var playerCamera = hub.PlayerCameraReference;

            //if (firstDoorOnPath.GetComponentsInChildren<Collider>()
            //        .Any(collider => collider.Raycast(new Ray(playerPosition, hub.PlayerCameraReference.forward), out var hit, 2f))
            if (Physics.Raycast(playerCamera.position, playerCamera.forward, out var hit, maxInteractDistance, DoorAndGlassMask)
                && hit.collider.GetComponent<InteractableCollider>() is InteractableCollider interactableCollider
                && interactableCollider.Target is DoorVariant interactable
                && interactable == targetDoor)
            {
                var colliderId = interactableCollider.ColliderId;

                interactable.ServerInteract(hub, colliderId);
                //Log.Debug($"ServerInteract(...) called on {interactable}");

                return true;
            }

            return false;
        }

        public bool InteractDoorDirectly(DoorVariant targetDoor, float maxInteractDistance)
        {
            if (targetDoor == null)
            {
                return false;
            }

            var hub = BotHub.PlayerHub;
            var playerPosition = hub.PlayerCameraReference.position;

            foreach (var interactableCollider in targetDoor.GetComponentsInChildren<InteractableCollider>())
            {
                var collider = interactableCollider.GetComponent<Collider>();
                if (collider == null)
                {
                    continue;
                }

                var closest = collider.ClosestPoint(playerPosition);
                if (Vector3.Distance(playerPosition, closest) > maxInteractDistance)
                {
                    continue;
                }

                if (interactableCollider.Target is DoorVariant interactable)
                {
                    interactable.ServerInteract(hub, interactableCollider.ColliderId);
                    return true;
                }
            }

            return false;
        }

        public bool OpenLockerDoor(LockerChamber targetDoor, float maxInteractDistance)
        {
            var hub = BotHub.PlayerHub;
            var playerCamera = hub.PlayerCameraReference;

            var (isHit, hit) = targetDoor.GetComponentsInChildren<InteractableCollider>()
                    .Select(interactableCollider => interactableCollider.GetComponent<Collider>())
                    .Select(collider => (isHit: collider.Raycast(new Ray(playerCamera.position, hub.PlayerCameraReference.forward), out var hit, maxInteractDistance), hit))
                    .FirstOrDefault(t => t.isHit);

            if (isHit
                && hit.collider.GetComponent<InteractableCollider>() is InteractableCollider interactableCollider
                && hit.collider.GetComponentInParent<IServerInteractable>() is IServerInteractable interactable)

            //if (Physics.Raycast(playerCamera.position, playerCamera.forward, out var hit, maxInteractDistance)
            //    && hit.collider.GetComponent<InteractableCollider>() is InteractableCollider interactableCollider
            //    && hit.collider.GetComponentInParent<IServerInteractable>() is IServerInteractable interactable)
            {
                var colliderId = interactableCollider.ColliderId;

                interactable.ServerInteract(hub, colliderId);
                //Log.Debug($"ServerInteract(...) called on {interactable}");

                return true;
            }

            return false;
        }

        #endregion

        #region Debug functions

        private float nextDiagnosticsDisplayTime;

        private void DisplaySpectatorDiagnostics(string stateOverride = null, string targetOverride = null)
        {
            BotPresentationService presentation = LabApiPlugin.Instance?.Presentation;
            if (presentation == null || Spectators.Count == 0)
            {
                return;
            }

            bool hasEnabledSpectator = false;
            foreach (ReferenceHub spectatorHub in Spectators)
            {
                LabApi.Features.Wrappers.Player spectator = LabApi.Features.Wrappers.Player.Get(spectatorHub);
                if (spectator != null && presentation.IsBotDiagnosticsEnabled(spectator))
                {
                    hasEnabledSpectator = true;
                    break;
                }
            }

            if (!hasEnabledSpectator)
            {
                return;
            }

            if (Time.time < nextDiagnosticsDisplayTime)
            {
                return;
            }

            nextDiagnosticsDisplayTime = Time.time + 0.5f;
            string botName = LabApi.Features.Wrappers.Player.Get(BotHub.PlayerHub)?.DisplayName
                ?? $"bot:{BotHub.PlayerHub.netId}";
            string role = BotHub.PlayerHub.roleManager?.CurrentRole?.RoleTypeId.ToString() ?? "unknown";
            string state = stateOverride
                ?? (BotHub.IsParked
                    ? "parked"
                    : Combat.DiagnosticHasTarget
                        ? Combat.DiagnosticState
                        : MindRunner.RunningAction?.GetType().Name ?? "idle");
            string target = targetOverride
                ?? (Combat.DiagnosticHasTarget
                    ? $"{Combat.DiagnosticTarget} · {Combat.DiagnosticTargetRole} · {(Combat.DiagnosticHasLineOfSight ? "visible" : "remembered")}"
                    : "none");
            string navigation = Navigator.HasPath
                ? $"path {Navigator.CellsPath.Count} cells"
                : "no path";
            var view = new BotDiagnosticView(
                $"{botName} · {role}",
                $"{state} · runner {(BotManager.Instance.RunnerIsRunning ? "healthy" : "stopped")}",
                target,
                navigation);

            foreach (ReferenceHub spectatorHub in Spectators)
            {
                LabApi.Features.Wrappers.Player spectator = LabApi.Features.Wrappers.Player.Get(spectatorHub);
                if (spectator != null)
                {
                    presentation.ShowBotDiagnostics(spectator, view);
                }
            }
        }

        private readonly List<ReferenceHub> spectatorsCache = new();
        private float nextSpectatorsRefreshTime;

        // Materialized + time-sliced: the previous deferred LINQ re-scanned ReferenceHub.AllHubs on
        // every enumeration (and was consumed multiple times per tick). Refresh at most ~2 Hz.
        public IReadOnlyList<ReferenceHub> Spectators
        {
            get
            {
                if (Time.time >= nextSpectatorsRefreshTime)
                {
                    nextSpectatorsRefreshTime = Time.time + 0.5f;
                    spectatorsCache.Clear();
                    var botNetId = this.BotHub.PlayerHub.netId;
                    foreach (var hub in ReferenceHub.AllHubs)
                    {
                        if (hub != null
                            && hub.roleManager.CurrentRole is OverwatchRole s
                            && s.SyncedSpectatedNetId == botNetId)
                        {
                            spectatorsCache.Add(hub);
                        }
                    }
                }

                return spectatorsCache;
            }
        }

        public void Dispose()
        {
            if (isDisposed)
            {
                return;
            }

            isDisposed = true;
            try
            {
                MindRunner.Dispose();
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }

            try
            {
                Perception.Dispose();
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }

            spectatorsCache.Clear();
            PerceptionComponent = null;
        }

        private bool isDisposed;

        #endregion
    }
}
