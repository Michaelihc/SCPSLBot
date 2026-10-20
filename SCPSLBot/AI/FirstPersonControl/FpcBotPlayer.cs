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
using SCPSLBot.Navigation;
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
        public IBotNavigator Navigator { get; }

        public FpcLook Look { get; }
        public FpcMove Move { get; }

        public Vector3 PlayerPosition { get; private set; }
        public Vector3 PlayerForward { get; private set; }

        public Vector3 CameraPosition { get; private set; }
        public Vector3 CameraForward { get; private set; }

        /// <summary>Movement executor behind <see cref="MoveToPosition(Vector3)"/>: path following, doors, elevators, stuck recovery.</summary>
        public BotLocomotion Locomotion { get; }

        /// <summary>Claim on the held item while something temporarily equips another one (keycard at a door).</summary>
        public BotItemLease ItemLease { get; } = new();

        public FpcStuckRecovery StuckRecovery => Locomotion.StuckRecovery;
        public FpcObstacleAvoidance ObstacleAvoidance => Locomotion.ObstacleAvoidance;

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
                Navigator = NavigationSystem.Instance.CreateNavigator(this);
                Look = new(this);
                Move = new(this);
                Locomotion = new(this);

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
                Locomotion.EndFrame();
                // Orders own their own failure semantics (stall telemetry / timeouts).
                Locomotion.DiscardAbandonRequest();
                yield break;
            }

            if (BotManager.Instance.ShouldHoldPosition(BotHub.PlayerHub))
            {
                Locomotion.Stop();
                DisplaySpectatorDiagnostics("held", "none");
                yield break;
            }

            if (Combat.Tick())
            {
                DisplaySpectatorDiagnostics();
                Locomotion.EndFrame();
                Locomotion.DiscardAbandonRequest();
                yield break;
            }

            if (BotManager.Instance.TryGetPathTargetPosition(out var pathTargetPosition))
            {
                MoveToPosition(pathTargetPosition);
                Locomotion.EndFrame();
                Locomotion.DiscardAbandonRequest();
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
                Locomotion.Stop();
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

                Locomotion.EndFrame();
                yield break;
            }

            MindRunner.Tick();

            // If the planner has nothing to do (e.g. no reachable escape route sensed yet), roam
            // instead of standing still for the rest of the round.
            if (MindRunner.RunningAction == null)
            {
                ZoneRoam.Tick();
            }
            else
            {
                // Goal-driven actions re-plan through beliefs; a stuck ladder abandon only matters
                // to the roam fallback, so keep the flag from lingering into a later roam tick.
                Locomotion.DiscardAbandonRequest();
            }

            DisplaySpectatorDiagnostics();
            Locomotion.EndFrame();

            yield break;
        }

        private bool ShouldUseEscapeGoal()
        {
            var role = BotHub.PlayerHub.roleManager.CurrentRole.RoleTypeId;
            return role is RoleTypeId.ClassD or RoleTypeId.Scientist;
        }

        private bool ShouldIdleOnSurfaceWithoutTarget()
        {
            return SCPSLBot.Api.BotServerPolicy.IsStandardWarmup
                && RoomUtils.TryGetRoom(PlayerPosition, out var room) && room.Zone == FacilityZone.Surface;
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
            Locomotion.Reset();
        }

        #region Moving

        /// <summary>
        /// The one movement entry point: walk toward <paramref name="goalPosition"/> this frame.
        /// Doors on the way are opened and elevators ridden by <see cref="Locomotion"/>.
        /// </summary>
        public void MoveToPosition(Vector3 goalPosition) => MoveToPosition(goalPosition, out _);
        public void MoveToPosition(Vector3 goalPosition, out Vector3 positionTowardsGoal)
        {
            Locomotion.MoveTo(goalPosition, out positionTowardsGoal);
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
                ? $"path {Navigator.PathNodeCount} nodes"
                : Navigator.HasPartialPath
                    ? $"partial path {Navigator.PathNodeCount} nodes"
                    : "no path";
            if (StuckRecovery.StuckSeconds > 0.5f)
            {
                navigation += $" · stuck {StuckRecovery.StuckSeconds:F1}s";
            }
            var crossing = Locomotion.DiagnosticState;
            if (crossing != null)
            {
                navigation += $" · {crossing}";
            }
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
            ItemLease.Reset();
            PerceptionComponent = null;
        }

        private bool isDisposed;

        #endregion
    }
}
