using Interactables.Interobjects;
using SCPSLBot.AI.FirstPersonControl.Perception.Senses.Sight;
using SCPSLBot.Navigation;
using UnityEngine;

namespace SCPSLBot.AI.FirstPersonControl.Mind.Elevation
{
    internal enum ElevationObstacleMode
    {
        NoElevator,
        IsElevatorNotAtOrigin,
        IsElevatorAtOrigin
    }

    /// <summary>
    /// Tracks the elevator crossing on the bot's current path. The runtime navmesh exposes the
    /// crossing as a link with both doors, so the chamber and its docking state are read directly;
    /// the authored cell mesh only knows an edgeless segment, for which the world around the
    /// segment origin is probed as before.
    /// </summary>
    internal class ElevationObstacle : Belief<ElevationObstacleMode>
    {
        private readonly int doorLayer = LayerMask.NameToLayer("Door");

        private readonly IBotNavigator navigator;
        private readonly SightSense sightSense;

        public ElevationObstacle(SightSense sightSense, IBotNavigator botNavigator)
        {
            this.navigator = botNavigator;
            this.sightSense = sightSense;

            sightSense.OnAfterSightSensing += OnAfterSightSensing;
        }

        private void OnAfterSightSensing()
        {
            if (!navigator.TryGetElevatorLink(out var link))
            {
                if (DestinationPoint.HasValue && navigator.HasReached(DestinationPoint.Value))
                {
                    Update(null, null, null, null);
                }

                return;
            }

            var goalPosition = navigator.GoalPosition;
            if (link.HasDoors)
            {
                var chamber = link.OriginDoor.Chamber;
                if (chamber == null && !ElevatorChamber.TryGetChamber(link.OriginDoor.Group, out chamber))
                {
                    Update(null, goalPosition, link.Destination, null);
                    return;
                }

                var dockedAtOrigin = chamber.DestinationDoor == link.OriginDoor && chamber.IsReady;
                Update(chamber, goalPosition, link.Destination, dockedAtOrigin ? chamber : null);
                return;
            }

            // Authored backend: an edgeless cell link; probe the world around its origin.
            var originPoint = link.Origin;
            if (!sightSense.IsPositionWithinFov(originPoint))
            {
                return;
            }

            if (sightSense.IsPositionObstructed(originPoint, out var hit))
            {
                var elevatorDoor = hit.collider.GetComponentInParent<ElevatorDoor>();
                if (!elevatorDoor || hit.collider.gameObject.layer != doorLayer)
                {
                    return;
                }

                var elevator = elevatorDoor.Chamber;
                if (!elevator)
                {
                    Debug.LogWarning($"No elevator chamber assigned to obstructing elevator door {elevatorDoor}.");
                    return;
                }

                Update(elevator, goalPosition, link.Destination, elevatorDoor.IsConsideredOpen() ? elevator : null);
                return;
            }

            if (Physics.Raycast(originPoint, Vector3.down, out hit, 2f))
            {
                var elevator = hit.collider.GetComponentInParent<ElevatorChamber>();
                if (elevator)
                {
                    Update(elevator, goalPosition, link.Destination, elevator);
                    return;
                }
            }

            if (Physics.Raycast(link.Destination, Vector3.down, out hit, 2f))
            {
                var elevator = hit.collider.GetComponentInParent<ElevatorChamber>();
                if (elevator)
                {
                    Update(elevator, goalPosition, link.Destination, null);
                    return;
                }
            }

            Update(null, goalPosition, link.Destination, null);
        }

        public ElevationObstacleMode Has(Vector3 goalPos) => GoalPosition == goalPos ? HasAtOrigin : ElevationObstacleMode.NoElevator;
        public ElevationObstacleMode HasAtOrigin => ElevatorAtOrigin ? ElevationObstacleMode.IsElevatorAtOrigin : ElevationObstacleMode.IsElevatorNotAtOrigin;

        public ElevatorChamber Elevator { get; private set; }
        public Vector3? GoalPosition { get; private set; }
        public Vector3? DestinationPoint { get; private set; }
        public ElevatorChamber ElevatorAtOrigin { get; private set; }

        private void Update(ElevatorChamber newElevatorValue, Vector3? goalPos, Vector3? destinationPoint, ElevatorChamber elevatorAtOrigin)
        {
            if (newElevatorValue != Elevator || elevatorAtOrigin != ElevatorAtOrigin)
            {
                Elevator = newElevatorValue;
                GoalPosition = goalPos;
                DestinationPoint = destinationPoint;
                ElevatorAtOrigin = elevatorAtOrigin;
                InvokeOnUpdate();
            }
        }

        public override string ToString()
        {
            return $"{nameof(ElevationObstacle)}: {Elevator?.GetType().Name ?? "ElevatorInTransit"}";
        }
    }
}
