using Interactables.Interobjects;
using SCPSLBot.Navigation;
using System;
using UnityEngine;

namespace SCPSLBot.AI.FirstPersonControl.Movement
{
    internal enum ElevatorTraversalStatus
    {
        Running,
        /// <summary>The bot stands on the far landing; normal path following resumes.</summary>
        Completed,
        /// <summary>The crossing cannot be used now (locked, unanswered, timed out); avoid it and replan.</summary>
        Blocked,
    }

    /// <summary>
    /// Carries a bot across one elevator link the way a player would: walk to the landing, call
    /// the chamber with the landing panel when it is elsewhere, wait for the doors, step into the
    /// cabin, send it with the cabin panel, ride, and walk out toward the far landing. Every stage
    /// is bounded; panel presses go through the native ServerInteract paths, so door locks and
    /// LabAPI elevator events apply. Other players using the elevator meanwhile are tolerated:
    /// the stages follow the chamber's actual destination instead of assuming their own presses.
    /// </summary>
    internal sealed class ElevatorTraversal
    {
        public enum Stage
        {
            None,
            Approach,
            Call,
            Enter,
            Send,
            Ride,
            Exit,
        }

        private const float LandingReachedMeters = 1f;
        private const float CabinCenterMeters = 0.6f;
        private const float CabinRecenterMeters = 0.5f;
        private const float ExitReachedMeters = 2f;
        private const float ExitVerticalTolerance = 1.5f;
        private const float LandingPressIntervalSeconds = 2f;
        private const float CabinPressIntervalSeconds = 1.5f;
        private const int MaxUnansweredPresses = 3;

        private const float ApproachTimeoutSeconds = 10f;
        private const float CallTimeoutSeconds = 40f;
        private const float EnterTimeoutSeconds = 8f;
        private const float SendTimeoutSeconds = 12f;
        private const float RideTimeoutSeconds = 45f;
        private const float ExitTimeoutSeconds = 8f;

        private readonly FpcBotPlayer botPlayer;
        private readonly BotLocomotion locomotion;

        private ElevatorChamber chamber;
        private ElevatorDoor originDoor;
        private ElevatorDoor destinationDoor;
        private ElevatorPanel landingPanel;
        private ElevatorPanel cabinPanel;
        private float stageStartedAt;
        private float nextPressAt;
        private int presses;

        public ElevatorTraversal(FpcBotPlayer botPlayer, BotLocomotion locomotion)
        {
            this.botPlayer = botPlayer;
            this.locomotion = locomotion;
        }

        public Stage CurrentStage { get; private set; }

        public bool IsActive => CurrentStage != Stage.None;

        /// <summary>Entering, inside or leaving the cabin: the goal no longer decides anything until the bot is out.</summary>
        public bool IsCommitted => CurrentStage >= Stage.Enter;

        public ElevatorLinkSegment Link { get; private set; }

        /// <summary>The goal the traversal serves; refreshed while the caller's plan keeps using this link.</summary>
        public Vector3 Goal { get; set; }

        public float LastGoalMatchAt { get; set; }

        /// <summary>Where the bot is steering this frame (stuck/diagnostic consumers).</summary>
        public Vector3 CurrentTarget { get; private set; }

        public string DiagnosticState => IsActive ? $"elevator:{CurrentStage}" : null;

        public bool IsSameLink(ElevatorLinkSegment link)
        {
            return IsActive && HorizontalDistance(link.Origin, Link.Origin) <= 1f && Mathf.Abs(link.Origin.y - Link.Origin.y) <= 1f;
        }

        public bool TryBegin(ElevatorLinkSegment link, Vector3 goal)
        {
            Cancel();
            if (!TryResolveDoors(link, out var origin, out var destination))
            {
                return false;
            }

            var resolvedChamber = origin.Chamber;
            if (resolvedChamber == null && !ElevatorChamber.TryGetChamber(origin.Group, out resolvedChamber))
            {
                return false;
            }

            chamber = resolvedChamber;
            originDoor = origin;
            destinationDoor = destination;
            landingPanel = null;
            cabinPanel = null;
            foreach (var panel in ElevatorPanel.AllPanels)
            {
                if (panel == null || panel.AssignedChamber != chamber)
                {
                    continue;
                }

                if (ReferenceEquals(panel.Target, originDoor))
                {
                    landingPanel = panel;
                }
                else if (ReferenceEquals(panel.Target, chamber))
                {
                    cabinPanel = panel;
                }
            }

            Link = link;
            Goal = goal;
            LastGoalMatchAt = Time.time;
            CurrentTarget = link.Origin;
            SetStage(Stage.Approach);
            return true;
        }

        public void Cancel()
        {
            CurrentStage = Stage.None;
            chamber = null;
            originDoor = null;
            destinationDoor = null;
            landingPanel = null;
            cabinPanel = null;
        }

        public ElevatorTraversalStatus Tick()
        {
            if (chamber == null || originDoor == null || destinationDoor == null)
            {
                return ElevatorTraversalStatus.Blocked;
            }

            switch (CurrentStage)
            {
                case Stage.Approach:
                    return TickApproach();
                case Stage.Call:
                    return TickCall();
                case Stage.Enter:
                    return TickEnter();
                case Stage.Send:
                    return TickSend();
                case Stage.Ride:
                    return TickRide();
                case Stage.Exit:
                    return TickExit();
                default:
                    return ElevatorTraversalStatus.Completed;
            }
        }

        private ElevatorTraversalStatus TickApproach()
        {
            if (IsOpenAt(originDoor))
            {
                SetStage(Stage.Enter);
                return TickEnter();
            }

            if (HorizontalDistance(botPlayer.PlayerPosition, Link.Origin) <= LandingReachedMeters)
            {
                SetStage(Stage.Call);
                return TickCall();
            }

            if (StageElapsed > ApproachTimeoutSeconds)
            {
                return ElevatorTraversalStatus.Blocked;
            }

            Steer(Link.Origin);
            return ElevatorTraversalStatus.Running;
        }

        private ElevatorTraversalStatus TickCall()
        {
            if (IsOpenAt(originDoor))
            {
                SetStage(Stage.Enter);
                return TickEnter();
            }

            if (StageElapsed > CallTimeoutSeconds)
            {
                return ElevatorTraversalStatus.Blocked;
            }

            Stand(landingPanel != null ? PanelPosition(landingPanel) : originDoor.transform.position + Vector3.up);
            if (chamber.DestinationDoor == originDoor)
            {
                // Already on its way here (or closing up before leaving toward us).
                presses = 0;
                return ElevatorTraversalStatus.Running;
            }

            if (originDoor.ActiveLocks != 0)
            {
                // The landing panel refuses while its door is locked (lockdown, warhead, admin).
                return ElevatorTraversalStatus.Blocked;
            }

            if (chamber.IsReadyForUserInput && Time.time >= nextPressAt)
            {
                if (presses >= MaxUnansweredPresses)
                {
                    // The far door is locked: the chamber refuses every call.
                    return ElevatorTraversalStatus.Blocked;
                }

                presses++;
                nextPressAt = Time.time + LandingPressIntervalSeconds;
                Press(originDoor, landingPanel);
            }

            return ElevatorTraversalStatus.Running;
        }

        private ElevatorTraversalStatus TickEnter()
        {
            var inside = IsInsideCabin();
            if (chamber.DestinationDoor != originDoor || (!originDoor.TargetState && !inside))
            {
                // It left (or is closing) before the bot got in.
                SetStage(Stage.Call);
                return ElevatorTraversalStatus.Running;
            }

            var center = CabinCenter();
            if (inside && HorizontalDistance(botPlayer.PlayerPosition, center) <= CabinCenterMeters)
            {
                SetStage(Stage.Send);
                return TickSend();
            }

            if (StageElapsed > EnterTimeoutSeconds)
            {
                return ElevatorTraversalStatus.Blocked;
            }

            Steer(center);
            return ElevatorTraversalStatus.Running;
        }

        private ElevatorTraversalStatus TickSend()
        {
            if (chamber.DestinationDoor == destinationDoor)
            {
                SetStage(Stage.Ride);
                return TickRide();
            }

            if (!IsInsideCabin())
            {
                // Pushed out before it left: call it again from the landing.
                SetStage(Stage.Approach);
                return ElevatorTraversalStatus.Running;
            }

            if (StageElapsed > SendTimeoutSeconds)
            {
                return ElevatorTraversalStatus.Blocked;
            }

            KeepCentered(cabinPanel != null ? PanelPosition(cabinPanel) : destinationDoor.transform.position);
            if (chamber.IsReadyForUserInput && Time.time >= nextPressAt)
            {
                var currentDoor = chamber.DestinationDoor;
                if ((currentDoor != null && currentDoor.ActiveLocks != 0) || presses >= MaxUnansweredPresses)
                {
                    return ElevatorTraversalStatus.Blocked;
                }

                presses++;
                nextPressAt = Time.time + CabinPressIntervalSeconds;
                Press(chamber, cabinPanel ?? landingPanel);
            }

            return ElevatorTraversalStatus.Running;
        }

        private ElevatorTraversalStatus TickRide()
        {
            if (chamber.DestinationDoor == destinationDoor)
            {
                if (IsOpenAt(destinationDoor))
                {
                    SetStage(Stage.Exit);
                    return TickExit();
                }
            }
            else if (chamber.DestinationDoor == originDoor && chamber.IsReady)
            {
                // Someone sent it back before it left; send it again.
                SetStage(Stage.Send);
                return ElevatorTraversalStatus.Running;
            }

            if (StageElapsed > RideTimeoutSeconds)
            {
                return ElevatorTraversalStatus.Blocked;
            }

            KeepCentered(Link.Destination + Vector3.up);
            return ElevatorTraversalStatus.Running;
        }

        private ElevatorTraversalStatus TickExit()
        {
            var position = botPlayer.PlayerPosition;
            var inside = IsInsideCabin();
            var feetY = position.y - NavigationAgentProfile.RootHeight;
            var onDestinationFloor = Mathf.Abs(feetY - Link.Destination.y) <= ExitVerticalTolerance;
            if (!inside && onDestinationFloor && HorizontalDistance(position, Link.Destination) <= ExitReachedMeters)
            {
                return ElevatorTraversalStatus.Completed;
            }

            if (inside && chamber.DestinationDoor != destinationDoor)
            {
                // Someone sent it away while the bot was stepping out; ride along and retry.
                SetStage(Stage.Ride);
                return ElevatorTraversalStatus.Running;
            }

            if (StageElapsed > ExitTimeoutSeconds)
            {
                return onDestinationFloor && !inside ? ElevatorTraversalStatus.Completed : ElevatorTraversalStatus.Blocked;
            }

            Steer(Link.Destination);
            return ElevatorTraversalStatus.Running;
        }

        private bool IsOpenAt(ElevatorDoor door)
        {
            return chamber.DestinationDoor == door
                   && chamber.CurSequence is ElevatorChamber.ElevatorSequence.DoorOpening or ElevatorChamber.ElevatorSequence.Ready
                   && door.TargetState;
        }

        private bool IsInsideCabin() => chamber.WorldspaceBounds.Contains(botPlayer.PlayerPosition);

        private Vector3 CabinCenter()
        {
            var center = chamber.WorldspaceBounds.Origin;
            center.y = botPlayer.PlayerPosition.y;
            return center;
        }

        private void KeepCentered(Vector3 lookAt)
        {
            var center = CabinCenter();
            if (HorizontalDistance(botPlayer.PlayerPosition, center) > CabinRecenterMeters)
            {
                Steer(center);
                return;
            }

            Stand(lookAt);
        }

        private void Steer(Vector3 target)
        {
            CurrentTarget = target;
            locomotion.SteerDirect(target);
        }

        private void Stand(Vector3 lookAt)
        {
            CurrentTarget = botPlayer.PlayerPosition;
            locomotion.StandStill();
            botPlayer.LookToPosition(lookAt);
        }

        // Landing panels target their ElevatorDoor (ElevatorDoor.ServerInteract: chamber ready and
        // the landing door unlocked), the cabin panel targets the chamber itself; both end in
        // ElevatorChamber.ServerInteract, which sends the chamber to its next level unless the door
        // at its current level is locked, and looks the panel up by collider id.
        private void Press(object target, ElevatorPanel panel)
        {
            var hub = botPlayer.BotHub.PlayerHub;
            var colliderId = panel != null ? panel.ColliderId : (byte)0;
            try
            {
                if (target is ElevatorDoor door)
                {
                    door.ServerInteract(hub, colliderId);
                }
                else if (target is ElevatorChamber cabin)
                {
                    cabin.ServerInteract(hub, colliderId);
                }
            }
            catch (InvalidOperationException)
            {
                // No panel with that collider id exists for the chamber; the next press counts as unanswered.
            }
        }

        private static Vector3 PanelPosition(ElevatorPanel panel)
        {
            return panel.transform.position;
        }

        private static bool TryResolveDoors(ElevatorLinkSegment link, out ElevatorDoor origin, out ElevatorDoor destination)
        {
            if (link.HasDoors)
            {
                origin = link.OriginDoor;
                destination = link.DestinationDoor;
                return true;
            }

            // Edgeless links (authored cell mesh): the elevator doors nearest each endpoint.
            origin = NearestElevatorDoor(link.Origin);
            destination = NearestElevatorDoor(link.Destination);
            return origin != null && destination != null && origin != destination && origin.Group == destination.Group;
        }

        private static ElevatorDoor NearestElevatorDoor(Vector3 position)
        {
            ElevatorDoor nearest = null;
            var best = 6f * 6f;
            foreach (var chamber in ElevatorChamber.AllChambers)
            {
                if (chamber == null || chamber.FloorDoors == null)
                {
                    continue;
                }

                foreach (var door in chamber.FloorDoors)
                {
                    if (door == null)
                    {
                        continue;
                    }

                    var distance = (door.transform.position - position).sqrMagnitude;
                    if (distance < best)
                    {
                        best = distance;
                        nearest = door;
                    }
                }
            }

            return nearest;
        }

        private void SetStage(Stage stage)
        {
            CurrentStage = stage;
            stageStartedAt = Time.time;
            presses = 0;
            nextPressAt = 0f;
        }

        private float StageElapsed => Time.time - stageStartedAt;

        private static float HorizontalDistance(Vector3 a, Vector3 b)
        {
            var dx = a.x - b.x;
            var dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }
    }
}
