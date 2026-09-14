using Interactables.Interobjects;
using Mirror;
using SCPSLBot.AI.FirstPersonControl.Mind.Door;
using System;
using System.Linq;
using UnityEngine;

namespace SCPSLBot.AI.FirstPersonControl.Mind.Elevation
{
    internal class CallAndWaitForElevator : IAction
    {
        private readonly FpcBotPlayer botPlayer;
        private const float interactDistance = 2f;

        public CallAndWaitForElevator(FpcBotPlayer botPlayer)
        {
            this.botPlayer = botPlayer;
        }

        private ElevationObstacle elevationObstacle;

        public void SetEnabledByBeliefs(FpcMind fpcMind)
        {
        }

        public void SetImpactsBeliefs(FpcMind fpcMind)
        {
            elevationObstacle = fpcMind.ActionImpacts<ElevationObstacle, ElevationObstacleMode>(this, ElevationObstacleMode.IsElevatorAtOrigin);
        }

        public float Cost => 0f;

        public void Tick()
        {
            var elevator = elevationObstacle.Elevator;
            if (!elevator)
            {
                // waiting
                return;
            }

            var playerPosition = botPlayer.BotHub.PlayerHub.transform.position;
            var relPosDestDoor = playerPosition - elevator.DestinationDoor.transform.position;
            var relPosNextDestDoor = playerPosition - elevator.NextDestinationDoor.transform.position;

            var elevatorDoor = relPosDestDoor.sqrMagnitude < relPosNextDestDoor.sqrMagnitude ? elevator.DestinationDoor : elevator.NextDestinationDoor;
            if (elevator.DestinationDoor == elevatorDoor && elevator.IsReady)
            {
                botPlayer.MoveToPosition(elevationObstacle.GoalPosition!.Value);
                return;
            }

            var panel = elevatorDoor.GetComponentInChildren<ElevatorPanel>();
            var panelPosition = panel.GetComponent<Collider>().bounds.center;

            var dist = Vector3.Distance(panelPosition, playerPosition);
            if (dist > interactDistance)
            {
                var goalPos = elevationObstacle.GoalPosition!.Value;
                botPlayer.MoveToPosition(goalPos);

                var directionToPanel = Vector3.Normalize(panelPosition - playerPosition);
                var playerDirection = botPlayer.BotHub.PlayerHub.transform.forward;
                if (Vector3.Dot(playerDirection, directionToPanel) < .989f)
                {
                    botPlayer.LookToPosition(panelPosition);
                }

                return;
            }

            elevatorDoor.ServerInteract(botPlayer.BotHub.PlayerHub, panel.ColliderId);
        }

        public void Reset()
        {
        }

        public override string ToString()
        {
            return $"{nameof(CallAndWaitForElevator)}";
        }
    }
}
