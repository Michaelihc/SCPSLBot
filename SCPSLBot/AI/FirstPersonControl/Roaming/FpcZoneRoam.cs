using Interactables.Interobjects.DoorUtils;
using MapGeneration;
using SCPSLBot.AI.FirstPersonControl.Mind.Door;
using SCPSLBot.AI.FirstPersonControl.Perception.Senses;
using SCPSLBot.Navigation;
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
        private readonly List<Vector3> candidates = new();

        private Vector3? targetPosition;
        private FacilityZone? targetZone;

        private Vector3 progressAnchor;
        private float progressAnchorTime;

        public FpcZoneRoam(FpcBotPlayer botPlayer)
        {
            this.botPlayer = botPlayer;
        }

        private static INavigationBackend Backend => NavigationSystem.Instance.Backend;

        public bool Tick()
        {
            var roomSightSense = botPlayer.Perception.GetSense<RoomSightSense>();
            var roomWithin = roomSightSense.RoomWithin;
            if (!roomWithin)
            {
                return TickWithoutRoom();
            }

            var abandoned = botPlayer.StuckRecovery.ConsumeAbandonRequest();
            var roamStuck = IsRoamStuck();
            if (roamStuck || abandoned)
            {
                // The crossing we were attempting is what blocks us; make the next plan avoid it
                // instead of picking a new target that routes through the same obstacle.
                botPlayer.Navigator.ReportBlockedCrossing();
            }

            if (ShouldPickTarget(roomWithin) || roamStuck || abandoned)
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
            candidates.Clear();
            foreach (var entry in roomSightSense.ForeignRoomEntries)
            {
                var room = entry.Room;
                if (room != null
                    && room.Zone == roomWithin.Zone
                    && (room.Name == RoomName.Unnamed || room.Name != roomWithin.Name))
                {
                    candidates.Add(entry.Position);
                }
            }

            if (candidates.Count == 0)
            {
                AddFarSamples(Backend?.GetRoomSamples(roomWithin));
            }

            if (candidates.Count == 0)
            {
                AddZoneSamples(roomWithin.Zone);
            }

            if (candidates.Count == 0)
            {
                AddAllSamples();
            }

            if (candidates.Count == 0)
            {
                targetPosition = null;
                targetZone = roomWithin.Zone;
                return;
            }

            targetPosition = PickReachable();
            targetZone = roomWithin.Zone;
        }

        // A few random draws, the first one the bot can actually reach wins (a target behind a
        // keycard door it cannot open would only end in the stuck ladder). Falls back to the last
        // draw so the bot always has somewhere to go.
        private Vector3 PickReachable()
        {
            var selected = candidates[random.Next(candidates.Count)];
            for (var attempt = 0; attempt < 4; attempt++)
            {
                if (botPlayer.Navigator.CanReach(selected))
                {
                    return selected;
                }

                selected = candidates[random.Next(candidates.Count)];
            }

            return selected;
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

            candidates.Clear();
            AddZoneSamples(nearestKnownZone.Value);
            if (candidates.Count == 0)
            {
                AddAllSamples();
            }

            if (candidates.Count == 0)
            {
                targetPosition = null;
                targetZone = nearestKnownZone;
                return;
            }

            targetPosition = PickReachable();
            targetZone = nearestKnownZone;
        }

        private void AddFarSamples(IReadOnlyList<Vector3> samples)
        {
            if (samples == null)
            {
                return;
            }

            var position = botPlayer.PlayerPosition;
            foreach (var sample in samples)
            {
                if (Vector3.Distance(position, sample) >= SameRoomTargetMinDistance)
                {
                    candidates.Add(sample);
                }
            }
        }

        private void AddZoneSamples(FacilityZone zone)
        {
            var backend = Backend;
            if (backend == null)
            {
                return;
            }

            foreach (var room in backend.GetNavigableRooms())
            {
                if (room != null && room.Zone == zone)
                {
                    AddFarSamples(backend.GetRoomSamples(room));
                }
            }
        }

        private void AddAllSamples()
        {
            var backend = Backend;
            if (backend == null)
            {
                return;
            }

            foreach (var room in backend.GetNavigableRooms())
            {
                AddFarSamples(backend.GetRoomSamples(room));
            }
        }

        private FacilityZone? GetNearestKnownZone()
        {
            var backend = Backend;
            if (backend == null)
            {
                return null;
            }

            FacilityZone? nearest = null;
            var nearestDistance = float.PositiveInfinity;
            var position = botPlayer.PlayerPosition;
            foreach (var room in backend.GetNavigableRooms())
            {
                foreach (var sample in backend.GetRoomSamples(room))
                {
                    var distance = Vector3.SqrMagnitude(sample - position);
                    if (distance < nearestDistance)
                    {
                        nearestDistance = distance;
                        nearest = room.Zone;
                    }
                }
            }

            return nearest;
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
