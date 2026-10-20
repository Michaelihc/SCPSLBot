using Interactables.Interobjects.DoorUtils;
using LightContainmentZoneDecontamination;
using MapGeneration;
using PlayerRoles;
using SCPSLBot.Infrastructure;
using SCPSLBot.Navigation;
using System.Collections.Generic;
using UnityEngine;

namespace SCPSLBot.AI.FirstPersonControl.Brain
{
    /// <summary>
    /// Facility-wide wandering. The bot picks a destination room (never an entry point: entries
    /// sit just past a doorway, so "arrived" used to trigger while still in the hallway and the
    /// bot turned around to the hallway's other end), walks to an interior point of that room and
    /// commits until it is really inside. Rooms are scored by novelty (per-bot visit memory),
    /// distance and a role policy for which zones are in scope; rooms that turned out unreachable
    /// are skipped for a while. Cross-zone trips work because locomotion crosses the elevators and
    /// checkpoints the planned path runs through.
    /// </summary>
    internal sealed class ExploreBehavior : IBotBehavior
    {
        private const float ArrivalHorizontalMeters = 1.5f;
        private const float ArrivalVerticalMeters = 1.5f;
        private const float DwellArrivalSeconds = 4f;
        private const float LingerMinSeconds = 1f;
        private const float LingerMaxSeconds = 3f;
        private const float VisitNoveltySeconds = 180f;
        private const float UnreachableSeconds = 30f;
        private const float PickRetrySeconds = 1.5f;
        private const int MaxPathChecksPerPick = 2;
        private const int RankedCandidates = 4;
        private const float DoorClearanceMeters = 3f;
        private const int MaxEvaluatedSamples = 400;
        private const int InteriorChoices = 3;
        private const float BaseTimeoutSeconds = 20f;
        private const float TimeoutMetersPerSecond = 1.5f;
        private const float ElevatorAllowanceSeconds = 60f;
        private const float NoPathTimeoutSeconds = 4f;
        private const float VisitRecordIntervalSeconds = 1f;
        private const float OtherZoneDistancePenalty = 40f;
        private const float DistanceScaleMeters = 120f;

        private readonly FpcBotPlayer botPlayer;
        private readonly System.Random random = new();
        private readonly Dictionary<RoomIdentifier, float> lastVisitAt = new(ManagedReferenceComparer<RoomIdentifier>.Instance);
        private readonly Dictionary<RoomIdentifier, float> unreachableUntil = new(ManagedReferenceComparer<RoomIdentifier>.Instance);
        private readonly List<(RoomIdentifier Room, float Score)> ranked = new(RankedCandidates + 1);
        private readonly List<(Vector3 Sample, float Score)> interior = new(InteriorChoices + 1);
        private readonly List<Vector3> doorways = new();

        private bool hasTarget;
        private RoomIdentifier targetRoom;
        private Vector3 destination;
        private float committedAt;
        private float deadline;
        private bool deadlineMeasured;
        private float enteredTargetRoomAt = -1f;
        private float lingerUntil;
        private float nextPickAt;

        private RoomIdentifier currentRoom;
        private FacilityZone currentZone = FacilityZone.None;
        private float nextVisitRecordAt;

        private RoleTypeId memoryRole = RoleTypeId.None;
        private int memoryGeneration = -1;

        public ExploreBehavior(FpcBotPlayer botPlayer)
        {
            this.botPlayer = botPlayer;
        }

        public string Name => "explore";

        /// <summary>The destination room, for diagnostics.</summary>
        public string Detail => hasTarget && targetRoom != null ? targetRoom.Name.ToString() : null;

        private static INavigationBackend Backend => NavigationSystem.Instance.Backend;

        public float Evaluate()
        {
            if (botPlayer.FpcRole == null || Backend == null)
            {
                return 0f;
            }

            var role = botPlayer.BotHub.PlayerHub.roleManager.CurrentRole.RoleTypeId;
            var generation = NavigationSystem.Instance.MapGeneration;
            if (role != memoryRole || generation != memoryGeneration)
            {
                // Reachability depends on role permissions and keycards; rooms die with the map.
                memoryRole = role;
                memoryGeneration = generation;
                ResetMemory();
            }

            return BehaviorPriority.Idle;
        }

        public void OnEnter()
        {
            botPlayer.Locomotion.DiscardAbandonRequest();
            lingerUntil = 0f;
            if (hasTarget)
            {
                // Resuming after combat or an objective: give the trip a fresh time budget.
                committedAt = Time.time;
                deadline = Time.time + BaseTimeoutSeconds + HorizontalDistance(botPlayer.PlayerPosition, destination) / TimeoutMetersPerSecond;
                deadlineMeasured = false;
                enteredTargetRoomAt = -1f;
            }
        }

        public void OnExit()
        {
        }

        public bool Tick()
        {
            var now = Time.time;
            RecordVisit(now);

            if (botPlayer.Locomotion.ConsumeAbandonRequest() && hasTarget)
            {
                MarkUnreachable(targetRoom, now);
                ClearTarget();
            }

            if (now < lingerUntil)
            {
                botPlayer.Locomotion.Stop();
                return true;
            }

            if (!hasTarget)
            {
                if (now < nextPickAt || !TryPick(now))
                {
                    if (now >= nextPickAt)
                    {
                        nextPickAt = now + PickRetrySeconds;
                    }

                    botPlayer.Locomotion.Stop();
                    return true;
                }
            }

            botPlayer.MoveToPosition(destination);
            MeasureDeadline(now);

            if (HasArrived(now))
            {
                lastVisitAt[targetRoom] = now;
                ClearTarget();
                lingerUntil = now + Mathf.Lerp(LingerMinSeconds, LingerMaxSeconds, (float)random.NextDouble());
                return true;
            }

            if (now > deadline)
            {
                MarkUnreachable(targetRoom, now);
                ClearTarget();
            }

            return true;
        }

        private bool HasArrived(float now)
        {
            if (targetRoom == null)
            {
                return true;
            }

            if (enteredTargetRoomAt >= 0f && now - enteredTargetRoomAt >= DwellArrivalSeconds)
            {
                return true;
            }

            var position = botPlayer.PlayerPosition;
            var feetY = position.y - NavigationAgentProfile.RootHeight;
            return HorizontalDistance(position, destination) <= ArrivalHorizontalMeters
                   && Mathf.Abs(feetY - destination.y) <= ArrivalVerticalMeters
                   && RoomUtils.TryGetRoom(position, out var room)
                   && room == targetRoom;
        }

        // The first plan toward the destination tells how long the trip really is (doors and
        // elevators included); until then a straight-line budget applies.
        private void MeasureDeadline(float now)
        {
            if (deadlineMeasured)
            {
                return;
            }

            var navigator = botPlayer.Navigator;
            if (navigator.HasPath && (navigator.GoalPosition - destination).sqrMagnitude < 4f)
            {
                var points = navigator.PointsPath;
                var length = 0f;
                for (var index = 1; index < points.Count; index++)
                {
                    length += HorizontalDistance(points[index - 1], points[index]);
                }

                var budget = BaseTimeoutSeconds + length / TimeoutMetersPerSecond;
                if (navigator.TryGetElevatorLink(out _))
                {
                    budget += ElevatorAllowanceSeconds;
                }

                deadline = now + budget;
                deadlineMeasured = true;
                return;
            }

            if (!navigator.HasPath && !navigator.HasPartialPath && now - committedAt > NoPathTimeoutSeconds)
            {
                deadline = now;
            }
        }

        private void RecordVisit(float now)
        {
            if (now < nextVisitRecordAt)
            {
                return;
            }

            nextVisitRecordAt = now + VisitRecordIntervalSeconds;
            if (!RoomUtils.TryGetRoom(botPlayer.PlayerPosition, out var room) || room == null)
            {
                currentRoom = null;
                return;
            }

            currentRoom = room;
            currentZone = room.Zone;
            lastVisitAt[room] = now;
            if (hasTarget && room == targetRoom)
            {
                if (enteredTargetRoomAt < 0f)
                {
                    enteredTargetRoomAt = now;
                }
            }
            else
            {
                enteredTargetRoomAt = -1f;
            }
        }

        private bool TryPick(float now)
        {
            var backend = Backend;
            if (backend == null)
            {
                return false;
            }

            var hub = botPlayer.BotHub.PlayerHub;
            var allowedZones = GetAllowedZones(hub.roleManager.CurrentRole, currentZone);
            var position = botPlayer.PlayerPosition;
            ranked.Clear();
            foreach (var room in backend.GetNavigableRooms())
            {
                if (room == null || room == currentRoom || (allowedZones & ZoneBit(room.Zone)) == 0)
                {
                    continue;
                }

                if (unreachableUntil.TryGetValue(room, out var until) && now < until)
                {
                    continue;
                }

                var novelty = lastVisitAt.TryGetValue(room, out var visitedAt)
                    ? Mathf.Clamp01((now - visitedAt) / VisitNoveltySeconds)
                    : 1.25f;
                var distance = HorizontalDistance(position, room.transform.position)
                               + (room.Zone != currentZone ? OtherZoneDistancePenalty : 0f);
                var score = novelty * 1.5f - distance / DistanceScaleMeters * 0.6f + (float)random.NextDouble() * 0.35f;
                InsertRanked(room, score);
            }

            var pathChecks = 0;
            foreach (var (room, _) in ranked)
            {
                if (!TryGetInteriorPoint(backend, room, out var point))
                {
                    MarkUnreachable(room, now);
                    continue;
                }

                pathChecks++;
                if (botPlayer.Navigator.CanReach(point))
                {
                    Commit(room, point, now);
                    return true;
                }

                MarkUnreachable(room, now);
                if (pathChecks >= MaxPathChecksPerPick)
                {
                    break;
                }
            }

            return false;
        }

        private void InsertRanked(RoomIdentifier room, float score)
        {
            var index = ranked.Count;
            while (index > 0 && ranked[index - 1].Score < score)
            {
                index--;
            }

            if (index >= RankedCandidates)
            {
                return;
            }

            ranked.Insert(index, (room, score));
            if (ranked.Count > RankedCandidates)
            {
                ranked.RemoveAt(ranked.Count - 1);
            }
        }

        // A navmesh sample near the room's centre and clear of its doorways, so arrival happens
        // well inside the room. One of the few best is drawn at random so bots do not converge.
        private bool TryGetInteriorPoint(INavigationBackend backend, RoomIdentifier room, out Vector3 point)
        {
            point = default;
            var samples = backend.GetRoomSamples(room);
            if (samples == null || samples.Count == 0)
            {
                return false;
            }

            doorways.Clear();
            if (DoorVariant.DoorsByRoom.TryGetValue(room, out var doors))
            {
                foreach (var door in doors)
                {
                    if (door != null)
                    {
                        doorways.Add(door.transform.position);
                    }
                }
            }

            var pivot = room.transform.position;
            var stride = Mathf.Max(1, samples.Count / MaxEvaluatedSamples);
            var clearanceSqr = DoorClearanceMeters * DoorClearanceMeters;
            interior.Clear();
            var fallback = samples[0];
            var fallbackClearance = -1f;
            for (var index = 0; index < samples.Count; index += stride)
            {
                var sample = samples[index];
                var nearestDoorway = float.PositiveInfinity;
                foreach (var doorway in doorways)
                {
                    nearestDoorway = Mathf.Min(nearestDoorway, HorizontalSqrDistance(sample, doorway));
                }

                if (nearestDoorway > fallbackClearance)
                {
                    fallbackClearance = nearestDoorway;
                    fallback = sample;
                }

                if (nearestDoorway < clearanceSqr)
                {
                    continue;
                }

                // Prefer the floor the room is built on: vertical offset counts double.
                var centrality = HorizontalDistance(sample, pivot) + Mathf.Abs(sample.y - pivot.y) * 2f;
                InsertInterior(sample, centrality);
            }

            point = interior.Count > 0 ? interior[random.Next(interior.Count)].Sample : fallback;
            return true;
        }

        private void InsertInterior(Vector3 sample, float centrality)
        {
            var index = interior.Count;
            while (index > 0 && interior[index - 1].Score > centrality)
            {
                index--;
            }

            if (index >= InteriorChoices)
            {
                return;
            }

            interior.Insert(index, (sample, centrality));
            if (interior.Count > InteriorChoices)
            {
                interior.RemoveAt(interior.Count - 1);
            }
        }

        private void Commit(RoomIdentifier room, Vector3 point, float now)
        {
            hasTarget = true;
            targetRoom = room;
            destination = point;
            committedAt = now;
            deadline = now + BaseTimeoutSeconds + HorizontalDistance(botPlayer.PlayerPosition, point) / TimeoutMetersPerSecond;
            deadlineMeasured = false;
            enteredTargetRoomAt = -1f;
        }

        private void ClearTarget()
        {
            hasTarget = false;
            targetRoom = null;
            enteredTargetRoomAt = -1f;
        }

        private void MarkUnreachable(RoomIdentifier room, float now)
        {
            if (room != null)
            {
                unreachableUntil[room] = now + UnreachableSeconds;
            }
        }

        private void ResetMemory()
        {
            lastVisitAt.Clear();
            unreachableUntil.Clear();
            ClearTarget();
            currentRoom = null;
            currentZone = FacilityZone.None;
            nextPickAt = 0f;
            lingerUntil = 0f;
        }

        /// <summary>
        /// Zones a role wanders through: guards patrol their zone, NTF and Chaos also the zones next
        /// to it, SCPs the whole facility; the objective roles (and anything else) stay in their zone.
        /// LCZ is dropped once decontamination runs and everything but Surface after detonation.
        /// </summary>
        private static int GetAllowedZones(PlayerRoleBase role, FacilityZone current)
        {
            const int facility = (1 << (int)FacilityZone.LightContainment) | (1 << (int)FacilityZone.HeavyContainment)
                                 | (1 << (int)FacilityZone.Entrance) | (1 << (int)FacilityZone.Surface);
            int zones;
            if (current is FacilityZone.None or FacilityZone.Other)
            {
                zones = facility;
            }
            else if (role.Team == Team.SCPs)
            {
                zones = facility;
            }
            else if (role.RoleTypeId is RoleTypeId.NtfPrivate or RoleTypeId.NtfSergeant or RoleTypeId.NtfSpecialist or RoleTypeId.NtfCaptain
                     || role.Team == Team.ChaosInsurgency)
            {
                zones = ZoneBit(current) | AdjacentZones(current);
            }
            else
            {
                zones = ZoneBit(current);
            }

            if (DecontaminationController.Singleton != null && DecontaminationController.Singleton.IsDecontaminating)
            {
                zones &= ~ZoneBit(FacilityZone.LightContainment);
            }

            if (AlphaWarheadController.Detonated)
            {
                zones &= ZoneBit(FacilityZone.Surface);
            }

            return zones;
        }

        private static int AdjacentZones(FacilityZone zone)
        {
            return zone switch
            {
                FacilityZone.LightContainment => ZoneBit(FacilityZone.HeavyContainment),
                FacilityZone.HeavyContainment => ZoneBit(FacilityZone.LightContainment) | ZoneBit(FacilityZone.Entrance),
                FacilityZone.Entrance => ZoneBit(FacilityZone.HeavyContainment) | ZoneBit(FacilityZone.Surface),
                FacilityZone.Surface => ZoneBit(FacilityZone.Entrance),
                _ => 0,
            };
        }

        private static int ZoneBit(FacilityZone zone) => 1 << (int)zone;

        private static float HorizontalDistance(Vector3 a, Vector3 b) => Mathf.Sqrt(HorizontalSqrDistance(a, b));

        private static float HorizontalSqrDistance(Vector3 a, Vector3 b)
        {
            var dx = a.x - b.x;
            var dz = a.z - b.z;
            return dx * dx + dz * dz;
        }
    }
}
