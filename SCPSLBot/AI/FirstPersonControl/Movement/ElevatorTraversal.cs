using Interactables.Interobjects;
using SCPSLBot.Navigation;
using System;
using UnityEngine;
using LabLogger = LabApi.Features.Console.Logger;

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
    /// The approach to the landing is plain path following by <see cref="BotLocomotion"/> (doors
    /// included); this class only decides when the bot stands there long enough to call. Stage
    /// timers pause while the traversal is not ticked (combat strafing, a hold), so a crossing
    /// interrupted mid-ride is re-evaluated on resume instead of being reported blocked.
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
        // Accepted anywhere inside the cabin this far from its walls: two riders cannot both
        // stand on the exact centre.
        private const float CabinInsetMeters = 0.35f;
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
        private const float PauseGapSeconds = 0.5f;
        private const float FloorToleranceMeters = 2f;

        private readonly FpcBotPlayer botPlayer;
        private readonly BotLocomotion locomotion;

        private ElevatorChamber chamber;
        private ElevatorDoor originDoor;
        private ElevatorDoor destinationDoor;
        private ElevatorPanel landingPanel;
        private ElevatorPanel cabinPanel;
        private float stageStartedAt;
        private float lastTickAt;
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

        /// <summary>Why the last tick returned <see cref="ElevatorTraversalStatus.Blocked"/>.</summary>
        public string BlockReason { get; private set; }

        public ElevatorGroup Group => originDoor != null ? originDoor.Group : default;

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
            lastTickAt = Time.time;
            BlockReason = null;
            SetStage(Stage.Approach, "path reached the landing");
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
                return Block("elevator destroyed");
            }

            var now = Time.time;
            if (now - lastTickAt > PauseGapSeconds)
            {
                // Not ticked for a while (combat strafing, a hold order): pause the stage timer.
                stageStartedAt += now - lastTickAt;
                nextPressAt = Mathf.Min(nextPressAt, now);
            }

            lastTickAt = now;

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

        // Movement is the caller's path following toward the landing; only transitions here.
        private ElevatorTraversalStatus TickApproach()
        {
            CurrentTarget = Link.Origin;
            if (IsOpenAt(originDoor) && HorizontalDistance(botPlayer.PlayerPosition, Link.Origin) <= LandingReachedMeters + 1f)
            {
                SetStage(Stage.Enter, "doors open at the landing");
                return TickEnter();
            }

            if (HorizontalDistance(botPlayer.PlayerPosition, Link.Origin) <= LandingReachedMeters)
            {
                SetStage(Stage.Call, "at the landing");
                return TickCall();
            }

            if (StageElapsed > ApproachTimeoutSeconds)
            {
                return Block("landing not reached");
            }

            return ElevatorTraversalStatus.Running;
        }

        private ElevatorTraversalStatus TickCall()
        {
            if (IsOpenAt(originDoor))
            {
                SetStage(Stage.Enter, "doors open");
                return TickEnter();
            }

            if (StageElapsed > CallTimeoutSeconds)
            {
                return Block("chamber never arrived");
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
                return Block("landing door locked");
            }

            if (chamber.IsReadyForUserInput && Time.time >= nextPressAt)
            {
                if (presses >= MaxUnansweredPresses)
                {
                    // The far door is locked: the chamber refuses every call.
                    return Block("calls unanswered");
                }

                presses++;
                nextPressAt = Time.time + LandingPressIntervalSeconds;
                PressLanding();
            }

            return ElevatorTraversalStatus.Running;
        }

        private ElevatorTraversalStatus TickEnter()
        {
            var inside = IsInsideCabin(0f);
            if (inside && chamber.DestinationDoor == destinationDoor)
            {
                SetStage(Stage.Ride, "sent by someone else while boarding");
                return TickRide();
            }

            if (chamber.DestinationDoor != originDoor || (!originDoor.TargetState && !inside))
            {
                SetStage(Stage.Call, "left before boarding");
                return ElevatorTraversalStatus.Running;
            }

            if (IsInsideCabin(CabinInsetMeters))
            {
                SetStage(Stage.Send, "aboard");
                return TickSend();
            }

            if (StageElapsed > EnterTimeoutSeconds)
            {
                return Block("could not board");
            }

            Steer(CabinCenter());
            return ElevatorTraversalStatus.Running;
        }

        private ElevatorTraversalStatus TickSend()
        {
            if (!IsInsideCabin(0f))
            {
                // Pushed out (or strafed out) before it left: call it again from the landing.
                SetStage(Stage.Approach, "left the cabin before departure");
                return ElevatorTraversalStatus.Running;
            }

            if (chamber.DestinationDoor == destinationDoor)
            {
                SetStage(Stage.Ride, "sent");
                return TickRide();
            }

            if (StageElapsed > SendTimeoutSeconds)
            {
                return Block("send refused");
            }

            KeepCentered(cabinPanel != null ? PanelPosition(cabinPanel) : destinationDoor.transform.position);
            if (chamber.IsReadyForUserInput && Time.time >= nextPressAt)
            {
                var currentDoor = chamber.DestinationDoor;
                if (currentDoor != null && currentDoor.ActiveLocks != 0)
                {
                    return Block("cabin door locked");
                }

                if (presses >= MaxUnansweredPresses)
                {
                    return Block("send unanswered");
                }

                presses++;
                nextPressAt = Time.time + CabinPressIntervalSeconds;
                PressCabin();
            }

            return ElevatorTraversalStatus.Running;
        }

        private ElevatorTraversalStatus TickRide()
        {
            if (!IsInsideCabin(0f))
            {
                // The cabin left without the bot (pushed or strafed out after the press), or the
                // bot already stepped out at the far landing.
                if (IsOnFloorOf(Link.Destination))
                {
                    SetStage(Stage.Exit, "outside on the destination floor");
                    return TickExit();
                }

                if (IsOnFloorOf(Link.Origin))
                {
                    SetStage(Stage.Approach, "left behind");
                    return ElevatorTraversalStatus.Running;
                }
            }

            if (chamber.DestinationDoor == destinationDoor)
            {
                if (IsOpenAt(destinationDoor))
                {
                    SetStage(Stage.Exit, "arrived");
                    return TickExit();
                }
            }
            else if (chamber.DestinationDoor == originDoor && chamber.IsReady)
            {
                SetStage(Stage.Send, "sent back before departure");
                return ElevatorTraversalStatus.Running;
            }

            if (StageElapsed > RideTimeoutSeconds)
            {
                return Block("ride timed out");
            }

            KeepCentered(Link.Destination + Vector3.up);
            return ElevatorTraversalStatus.Running;
        }

        private ElevatorTraversalStatus TickExit()
        {
            var position = botPlayer.PlayerPosition;
            var inside = IsInsideCabin(0f);
            var feetY = position.y - NavigationAgentProfile.RootHeight;
            var onDestinationFloor = Mathf.Abs(feetY - Link.Destination.y) <= ExitVerticalTolerance;
            if (!inside && onDestinationFloor && HorizontalDistance(position, Link.Destination) <= ExitReachedMeters)
            {
                Log("completed", "reached the far landing");
                return ElevatorTraversalStatus.Completed;
            }

            if (inside && chamber.DestinationDoor != destinationDoor)
            {
                SetStage(Stage.Ride, "sent away while stepping out");
                return ElevatorTraversalStatus.Running;
            }

            if (StageElapsed > ExitTimeoutSeconds)
            {
                if (onDestinationFloor && !inside)
                {
                    Log("completed", "exit timed out on the destination floor");
                    return ElevatorTraversalStatus.Completed;
                }

                return Block("could not leave the cabin");
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

        private bool IsInsideCabin(float inset)
        {
            var bounds = chamber.WorldspaceBounds;
            var local = bounds.WorldToLocal(botPlayer.PlayerPosition);
            var extents = bounds.Extends;
            return Mathf.Abs(local.x) < extents.x - inset
                   && Mathf.Abs(local.y) < extents.y
                   && Mathf.Abs(local.z) < extents.z - inset;
        }

        private bool IsOnFloorOf(Vector3 landing)
        {
            var feetY = botPlayer.PlayerPosition.y - NavigationAgentProfile.RootHeight;
            return Mathf.Abs(feetY - landing.y) <= FloorToleranceMeters;
        }

        private Vector3 CabinCenter()
        {
            var center = chamber.WorldspaceBounds.Origin;
            center.y = botPlayer.PlayerPosition.y;
            return center;
        }

        private void KeepCentered(Vector3 lookAt)
        {
            if (!IsInsideCabin(CabinInsetMeters))
            {
                Steer(CabinCenter());
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
        // at its current level is locked, and looks the panel up by collider id (First: throws
        // when no panel has it). ElevatorDoor.Chamber is only assigned by a landing panel.
        private void PressLanding()
        {
            var hub = botPlayer.BotHub.PlayerHub;
            try
            {
                if (landingPanel != null && originDoor.Chamber != null)
                {
                    originDoor.ServerInteract(hub, landingPanel.ColliderId);
                }
                else if (cabinPanel != null && originDoor.ActiveLocks == 0 && chamber.IsReadyForUserInput)
                {
                    chamber.ServerInteract(hub, cabinPanel.ColliderId);
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or NullReferenceException)
            {
                // Panels not wired up; the press counts as unanswered.
            }
        }

        private void PressCabin()
        {
            var panel = cabinPanel != null ? cabinPanel : landingPanel;
            if (panel == null)
            {
                return;
            }

            try
            {
                chamber.ServerInteract(botPlayer.BotHub.PlayerHub, panel.ColliderId);
            }
            catch (Exception exception) when (exception is InvalidOperationException or NullReferenceException)
            {
                // Panels not wired up; the press counts as unanswered.
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

        private void SetStage(Stage stage, string reason)
        {
            CurrentStage = stage;
            stageStartedAt = Time.time;
            presses = 0;
            nextPressAt = 0f;
            Log(stage.ToString(), reason);
        }

        private ElevatorTraversalStatus Block(string reason)
        {
            BlockReason = reason;
            Log("blocked", reason);
            return ElevatorTraversalStatus.Blocked;
        }

        // One line per transition; a ride has about seven, so no rate limit beyond that.
        private void Log(string stage, string reason)
        {
            var name = botPlayer.BotHub?.PlayerHub?.nicknameSync?.MyNick ?? "?";
            var position = botPlayer.PlayerPosition;
            LabLogger.Info($"[BotNav] ELEVATOR bot={name} group={(originDoor != null ? originDoor.Group.ToString() : "?")} stage={stage} reason={reason} pos=({position.x:F1},{position.y:F1},{position.z:F1})");
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
