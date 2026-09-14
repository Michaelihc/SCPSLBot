using Interactables.Interobjects.DoorUtils;
using MapGeneration;
using SCPSLBot.AI.FirstPersonControl.Mind.Door;
using SCPSLBot.AI.FirstPersonControl.Perception.Senses;
using SCPSLBot.Navigation.Mesh;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace SCPSLBot.AI.FirstPersonControl.Roaming
{
    internal sealed class FpcZoneRoam
    {
        private const float TargetReachedDistance = 1.75f;
        private const float SameRoomTargetMinDistance = 5f;
        private const float DoorInteractDistance = 2f;

        private readonly FpcBotPlayer botPlayer;
        private readonly System.Random random = new();

        private Vector3? targetPosition;
        private FacilityZone? targetZone;

        private Vector3 progressAnchor;
        private float progressAnchorTime;

        public FpcZoneRoam(FpcBotPlayer botPlayer)
        {
            this.botPlayer = botPlayer;
        }

        public bool Tick()
        {
            var roomSightSense = botPlayer.Perception.GetSense<RoomSightSense>();
            var roomWithin = roomSightSense.RoomWithin;
            if (!roomWithin)
            {
                return TickWithoutRoom();
            }

            if (ShouldPickTarget(roomWithin) || IsRoamStuck())
            {
                PickTarget(roomSightSense, roomWithin);
                ResetProgress();
            }

            if (!targetPosition.HasValue)
            {
                return false;
            }

            botPlayer.MoveToPosition(targetPosition.Value);
            OpenBlockingNonKeycardDoor();
            return true;
        }

        // Detects a roam target the bot cannot reach (e.g. blocked by an unopenable keycard door)
        // so it abandons that target and wanders elsewhere instead of grinding into the obstacle.
        private bool IsRoamStuck()
        {
            var position = botPlayer.PlayerPosition;
            if (progressAnchorTime <= 0f
                || Vector3.Distance(
                    Vector3.ProjectOnPlane(position, Vector3.up),
                    Vector3.ProjectOnPlane(progressAnchor, Vector3.up)) > 0.5f)
            {
                progressAnchor = position;
                progressAnchorTime = Time.time;
                return false;
            }

            return Time.time - progressAnchorTime > 4f;
        }

        private void ResetProgress()
        {
            progressAnchor = botPlayer.PlayerPosition;
            progressAnchorTime = Time.time;
        }

        private bool ShouldPickTarget(RoomIdentifier roomWithin)
        {
            if (!targetPosition.HasValue || targetZone != roomWithin.Zone)
            {
                return true;
            }

            return Vector3.Distance(botPlayer.PlayerPosition, targetPosition.Value) <= TargetReachedDistance;
        }

        private void PickTarget(RoomSightSense roomSightSense, RoomIdentifier roomWithin)
        {
            var candidates = GetSameZoneForeignCells(roomSightSense, roomWithin).ToList();
            if (candidates.Count == 0)
            {
                candidates = GetSameRoomCells(roomWithin)
                    .Where(cell => Vector3.Distance(botPlayer.PlayerPosition, cell.CenterPosition) >= SameRoomTargetMinDistance)
                    .ToList();
            }

            if (candidates.Count == 0)
            {
                candidates = GetZoneCells(roomWithin.Zone)
                    .Where(cell => Vector3.Distance(botPlayer.PlayerPosition, cell.CenterPosition) >= SameRoomTargetMinDistance)
                    .ToList();
            }

            if (candidates.Count == 0)
            {
                candidates = GetAllKnownCells()
                    .Where(cell => Vector3.Distance(botPlayer.PlayerPosition, cell.CenterPosition) >= SameRoomTargetMinDistance)
                    .ToList();
            }

            if (candidates.Count == 0)
            {
                targetPosition = null;
                targetZone = roomWithin.Zone;
                return;
            }

            var selected = candidates[random.Next(candidates.Count)];
            targetPosition = selected.CenterPosition;
            targetZone = roomWithin.Zone;
        }

        private bool TickWithoutRoom()
        {
            if (!targetPosition.HasValue || Vector3.Distance(botPlayer.PlayerPosition, targetPosition.Value) <= TargetReachedDistance)
            {
                PickFallbackZoneTarget();
            }

            if (targetPosition.HasValue)
            {
                botPlayer.MoveToPosition(targetPosition.Value);
                return true;
            }

            return false;
        }

        private void PickFallbackZoneTarget()
        {
            var nearestKnownZone = GetNearestKnownZone();
            if (!nearestKnownZone.HasValue)
            {
                targetPosition = null;
                targetZone = null;
                return;
            }

            var candidates = GetZoneCells(nearestKnownZone.Value)
                .Where(cell => Vector3.Distance(botPlayer.PlayerPosition, cell.CenterPosition) >= SameRoomTargetMinDistance)
                .ToList();
            if (candidates.Count == 0)
            {
                candidates = GetAllKnownCells()
                    .Where(cell => Vector3.Distance(botPlayer.PlayerPosition, cell.CenterPosition) >= SameRoomTargetMinDistance)
                    .ToList();
            }

            if (candidates.Count == 0)
            {
                targetPosition = null;
                targetZone = nearestKnownZone;
                return;
            }

            var selected = candidates[random.Next(candidates.Count)];
            targetPosition = selected.CenterPosition;
            targetZone = nearestKnownZone;
        }

        private static IEnumerable<TransformCell> GetSameZoneForeignCells(RoomSightSense roomSightSense, RoomIdentifier roomWithin)
        {
            return roomSightSense.ForeignRoomsCells
                .Where(cell => cell.Transform.GetComponent<RoomIdentifier>() is RoomIdentifier room
                               && room.Zone == roomWithin.Zone
                               && (room.Name == RoomName.Unnamed || room.Name != roomWithin.Name));
        }

        private static IEnumerable<TransformCell> GetSameRoomCells(RoomIdentifier roomWithin)
        {
            if (!roomWithin || !NavigationMesh.LocalMeshesByRoom.TryGetValue(roomWithin.gameObject, out var mesh))
            {
                yield break;
            }

            foreach (var cell in mesh.Cells)
            {
                yield return new TransformCell(cell, roomWithin.transform);
            }
        }

        private static IEnumerable<TransformCell> GetZoneCells(FacilityZone zone)
        {
            foreach (var (roomObject, mesh) in NavigationMesh.LocalMeshesByRoom)
            {
                var room = roomObject.GetComponent<RoomIdentifier>();
                if (!room || room.Zone != zone)
                {
                    continue;
                }

                foreach (var cell in mesh.Cells)
                {
                    yield return new TransformCell(cell, room.transform);
                }
            }
        }

        private static IEnumerable<TransformCell> GetAllKnownCells()
        {
            foreach (var (roomObject, mesh) in NavigationMesh.LocalMeshesByRoom)
            {
                if (!roomObject.GetComponent<RoomIdentifier>())
                {
                    continue;
                }

                foreach (var cell in mesh.Cells)
                {
                    yield return new TransformCell(cell, roomObject.transform);
                }
            }
        }

        private FacilityZone? GetNearestKnownZone()
        {
            TransformCell? nearest = null;
            var nearestDistance = float.PositiveInfinity;

            foreach (var (roomObject, mesh) in NavigationMesh.LocalMeshesByRoom)
            {
                var room = roomObject.GetComponent<RoomIdentifier>();
                if (!room)
                {
                    continue;
                }

                foreach (var cell in mesh.Cells)
                {
                    var transformCell = new TransformCell(cell, room.transform);
                    var distance = Vector3.SqrMagnitude(transformCell.CenterPosition - botPlayer.PlayerPosition);
                    if (distance >= nearestDistance)
                    {
                        continue;
                    }

                    nearest = transformCell;
                    nearestDistance = distance;
                }
            }

            return nearest?.Transform.GetComponent<RoomIdentifier>()?.Zone;
        }

        private void OpenBlockingNonKeycardDoor()
        {
            var doorObstacle = botPlayer.MindRunner.GetBelief<DoorObstacle>();
            if (!doorObstacle.IsAny || !doorObstacle.Doors.Values.Any(entry => entry.IsInteractable(DoorPermissionFlags.None)))
            {
                return;
            }

            var doorToOpen = doorObstacle.GetLastDoor(DoorPermissionFlags.None, out var goalPos);
            if (!doorToOpen)
            {
                return;
            }

            var doorPlane = new Plane(doorToOpen.transform.forward, doorToOpen.transform.position);
            var distance = Mathf.Abs(doorPlane.GetDistanceToPoint(botPlayer.PlayerPosition));

            if (!doorToOpen.TargetState && distance <= DoorInteractDistance)
            {
                if (!botPlayer.OpenDoor(doorToOpen, DoorInteractDistance))
                {
                    botPlayer.LookToPosition(doorToOpen.transform.position + Vector3.up);
                }
            }

            if (!doorToOpen.TargetState || distance > DoorInteractDistance)
            {
                botPlayer.MoveToPosition(goalPos);
            }
        }
    }
}
