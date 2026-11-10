using Interactables.Interobjects;
using Interactables.Interobjects.DoorUtils;
using LightContainmentZoneDecontamination;
using MapGeneration;
using PlayerRoles;
using SCPSLBot.Infrastructure;
using SCPSLBot.Navigation;
using SCPSLBot.Navigation.Policy;
using System;
using System.Collections.Generic;
using UnityEngine;
using LabLogger = LabApi.Features.Console.Logger;

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
    /// <para>
    /// It never leaves the bot idle: off the navmesh (odd spawns, ledges) it walks to the nearest
    /// navmesh point first, and when no room passes the reachability check it still commits to
    /// the nearest room and lets partial paths, the stuck ladder and the deadline sort it out.
    /// </para>
    /// </summary>
    internal sealed class ExploreBehavior : IBotBehavior, IDescribedBehavior
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
        private const int FallbackAfterFailedPicks = 2;
        private const float CrossingDeadlineGraceSeconds = 15f;
        private const int BlockedTripsBeforePacing = 3;
        private const float PacingSeconds = 30f;
        private const float PaceMinimumMeters = 5f;
        private const int PaceSampleTries = 8;
        private const float RecoveryArrivalMeters = 0.8f;
        private const float RecoveryTimeoutSeconds = 10f;
        private const float RecoveryNavigatorRangeMeters = 5f;
        private static readonly float[] RecoverySearchRadii = { 4f, 8f, 16f, 32f };
        private const float LogIntervalSeconds = 2f;

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

        private int failedPicks;
        private int blockedTrips;
        private float pacingUntil;
        private bool recovering;
        private Vector3 recoveryPoint;
        private float recoveryStartedAt;
        private float nextLogAt;

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

            if (hasTarget && botPlayer.Locomotion.IsCrossingElevator)
            {
                // Waiting for and riding an elevator is bounded by locomotion; path checks from
                // inside a moving cabin fail and must not condemn rooms or expire the trip.
                botPlayer.MoveToPosition(destination);
                deadline = Mathf.Max(deadline, now + CrossingDeadlineGraceSeconds);
                return true;
            }

            if (botPlayer.Locomotion.ConsumeAbandonRequest() && hasTarget)
            {
                Log("abandon", "locomotion gave up the route");
                MarkUnreachable(targetRoom, now);
                if (botPlayer.Locomotion.AbandonedAtDeniedDoor)
                {
                    NoteBlockedTrip(now);
                }

                ClearTarget();
            }

            if (now < lingerUntil)
            {
                botPlayer.Locomotion.Stop();
                return true;
            }

            if (recovering)
            {
                TickRecovery(now);
                return true;
            }

            if (!hasTarget)
            {
                if (now < nextPickAt)
                {
                    botPlayer.Locomotion.Stop();
                    return true;
                }

                if (!botPlayer.Navigator.IsOnMesh() && TryStartRecovery(now))
                {
                    TickRecovery(now);
                    return true;
                }

                if (!TryPick(now))
                {
                    failedPicks++;
                    if (failedPicks < FallbackAfterFailedPicks || now < pacingUntil || !TryPickFallback(now))
                    {
                        nextPickAt = now + PickRetrySeconds;
                        botPlayer.Locomotion.Stop();
                        return true;
                    }
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

            if (now >= deadline)
            {
                Log("timeout", deadlineMeasured ? "trip took too long" : "no path");
                MarkUnreachable(targetRoom, now);
                if (!deadlineMeasured)
                {
                    NoteBlockedTrip(now);
                }

                ClearTarget();
            }

            return true;
        }

        // Trips that keep dying without the bot ever changing room mean it is shut in.
        private void NoteBlockedTrip(float now)
        {
            if (++blockedTrips < BlockedTripsBeforePacing)
            {
                return;
            }

            blockedTrips = 0;
            var backend = Backend;
            if (backend != null && botPlayer.Combat.Scp106.TryAtlasToNearestRoom(backend.GetNavigableRooms(), currentRoom, botPlayer.PlayerPosition))
            {
                Log("atlas", "stranded, using Hunter's Atlas to reach a room");
                return;
            }

            pacingUntil = now + PacingSeconds;
            Log("pacing", "no way out of this room for now");
        }

        // Every exit is shut by a door this role cannot open (SCP-939 trapped in SCP-914's room), and
        // only someone opening up can change that. Walk the room rather than stand against the door,
        // and try the door again once pacing ends.
        private bool TryPickPacing(INavigationBackend backend, float now)
        {
            var samples = currentRoom != null ? backend.GetRoomSamples(currentRoom) : null;
            if (samples == null || samples.Count == 0)
            {
                return false;
            }

            var position = botPlayer.PlayerPosition;
            var pathChecks = 0;
            for (var attempt = 0; attempt < PaceSampleTries && pathChecks < MaxPathChecksPerPick; attempt++)
            {
                var sample = samples[random.Next(samples.Count)];
                if (HorizontalDistance(position, sample) < PaceMinimumMeters)
                {
                    continue;
                }

                pathChecks++;
                if (botPlayer.Navigator.CanReach(sample))
                {
                    Commit(currentRoom, sample, now);
                    return true;
                }
            }

            return false;
        }

        private bool TryStartRecovery(float now)
        {
            var backend = Backend;
            if (backend == null)
            {
                return false;
            }

            var position = botPlayer.PlayerPosition;
            foreach (var radius in RecoverySearchRadii)
            {
                if (backend.TryGetNearestPoint(position, radius, out var nearest, out _))
                {
                    recovering = true;
                    recoveryPoint = nearest;
                    recoveryStartedAt = now;
                    Log("recover", $"off the navmesh, walking to {Format(nearest)}");
                    return true;
                }
            }

            Log("recover", "off the navmesh and no navmesh within 32 m");
            return false;
        }

        // The navigator samples starts up to a few meters away; beyond that walk straight at the
        // nearest navmesh point (jumping off a ledge is fine, native FPC owns the fall).
        private void TickRecovery(float now)
        {
            var position = botPlayer.PlayerPosition;
            var feetY = position.y - NavigationAgentProfile.RootHeight;
            var arrived = HorizontalDistance(position, recoveryPoint) <= RecoveryArrivalMeters && Mathf.Abs(feetY - recoveryPoint.y) <= ArrivalVerticalMeters;
            var timedOut = now - recoveryStartedAt > RecoveryTimeoutSeconds;
            if (arrived || botPlayer.Navigator.IsOnMesh() || timedOut)
            {
                recovering = false;
                nextPickAt = now;
                botPlayer.Locomotion.Stop();
                if (timedOut)
                {
                    NoteBlockedTrip(now);
                }

                return;
            }

            if (HorizontalDistance(position, recoveryPoint) <= RecoveryNavigatorRangeMeters)
            {
                botPlayer.MoveToPosition(recoveryPoint);
            }
            else
            {
                botPlayer.Locomotion.SteerDirect(recoveryPoint);
            }
        }

        // Every ranked room failed the path check (an island start, a bot whose plans keep
        // coming back partial): commit to the nearest room anyway so the bot keeps moving; the
        // partial path, stuck ladder and deadline decide what happens next.
        private bool TryPickFallback(float now)
        {
            var backend = Backend;
            if (backend == null)
            {
                return false;
            }

            var position = botPlayer.PlayerPosition;
            RoomIdentifier nearest = null;
            var nearestDistance = float.PositiveInfinity;
            foreach (var room in backend.GetNavigableRooms())
            {
                if (room == null || room == currentRoom)
                {
                    continue;
                }

                var distance = Vector3.Distance(position, room.transform.position);
                if (distance < nearestDistance)
                {
                    nearestDistance = distance;
                    nearest = room;
                }
            }

            nearest ??= currentRoom;
            if (nearest == null || !TryGetInteriorPoint(backend, nearest, out var point))
            {
                return false;
            }

            Log("fallback", $"no reachable room after {failedPicks} picks, heading to {nearest.Name}");
            Commit(nearest, point, now);
            return true;
        }

        private void Log(string kind, string reason)
        {
            var now = Time.time;
            if (now < nextLogAt)
            {
                return;
            }

            nextLogAt = now + LogIntervalSeconds;
            var name = botPlayer.BotHub?.PlayerHub?.nicknameSync?.MyNick ?? "?";
            var room = targetRoom != null ? targetRoom.Name.ToString() : "none";
            LabLogger.Info($"[BotNav] EXPLORE bot={name} event={kind} room={room} reason={reason} pos={Format(botPlayer.PlayerPosition)}");
        }

        private static string Format(Vector3 value) => $"({value.x:F1},{value.y:F1},{value.z:F1})";

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

            if (room != currentRoom)
            {
                blockedTrips = 0;
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

            if (now < pacingUntil)
            {
                return TryPickPacing(backend, now);
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
            failedPicks = 0;
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
            failedPicks = 0;
            blockedTrips = 0;
            pacingUntil = 0f;
            recovering = false;
        }

        /// <summary>
        /// Zones a role wanders through: SCPs the whole facility, humans their zone and the zones next
        /// to it. Path checks keep a human without the right keycard on this side of a border.
        /// LCZ is dropped once decontamination runs and everything but Surface after detonation.
        /// Zones behind locked elevators are dropped too (warmup locks the Gate elevators): path
        /// checks ignore elevator locks, so the bot would otherwise keep walking to a dead lift.
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
            else
            {
                zones = ZoneBit(current) | AdjacentZones(current);
            }

            if (DecontaminationController.Singleton != null && DecontaminationController.Singleton.IsDecontaminating)
            {
                zones &= ~ZoneBit(FacilityZone.LightContainment);
            }

            if (AlphaWarheadController.Detonated)
            {
                zones &= ZoneBit(FacilityZone.Surface);
            }

            if (current is not (FacilityZone.None or FacilityZone.Other))
            {
                zones &= ZonesReachableByElevator(current);
            }

            return zones;
        }

        private static readonly List<(int A, int B)> ZoneLinks = new();
        private static readonly ElevatorGroup[] ElevatorGroups = (ElevatorGroup[])Enum.GetValues(typeof(ElevatorGroup));

        /// <summary>
        /// Zones reachable from <paramref name="current"/> when every locked elevator is cut. The
        /// Heavy/Entrance checkpoint is a door, which path checks already judge. A zone pair with
        /// no elevator found at all stays linked, so a missing lift never strands the bot.
        /// </summary>
        private static int ZonesReachableByElevator(FacilityZone current)
        {
            var lczHcz = ZoneBit(FacilityZone.LightContainment) | ZoneBit(FacilityZone.HeavyContainment);
            var ezSurface = ZoneBit(FacilityZone.Entrance) | ZoneBit(FacilityZone.Surface);
            int seenPairs = 0, openPairs = 0;
            foreach (var group in ElevatorGroups)
            {
                var doors = ElevatorDoor.GetDoorsForGroup(group);
                if (doors.Count != 2 || !TryGetDoorZone(doors[0], out var zoneA) || !TryGetDoorZone(doors[1], out var zoneB) || zoneA == zoneB)
                {
                    continue;
                }

                var pair = ZoneBit(zoneA) | ZoneBit(zoneB);
                seenPairs |= pair == lczHcz ? 1 : pair == ezSurface ? 2 : 0;
                if (doors[0].ActiveLocks == 0 && doors[1].ActiveLocks == 0)
                {
                    openPairs |= pair == lczHcz ? 1 : pair == ezSurface ? 2 : 0;
                }
            }

            ZoneLinks.Clear();
            ZoneLinks.Add((ZoneBit(FacilityZone.HeavyContainment), ZoneBit(FacilityZone.Entrance)));
            if ((openPairs & 1) != 0 || (seenPairs & 1) == 0)
            {
                ZoneLinks.Add((ZoneBit(FacilityZone.LightContainment), ZoneBit(FacilityZone.HeavyContainment)));
            }

            if ((openPairs & 2) != 0 || (seenPairs & 2) == 0)
            {
                ZoneLinks.Add((ZoneBit(FacilityZone.Entrance), ZoneBit(FacilityZone.Surface)));
            }

            return ZoneReachPolicy.Reachable(ZoneBit(current), ZoneLinks);
        }

        private static bool TryGetDoorZone(ElevatorDoor door, out FacilityZone zone)
        {
            zone = FacilityZone.None;
            if (door == null || door.Rooms == null || door.Rooms.Length == 0 || door.Rooms[0] == null)
            {
                return false;
            }

            zone = door.Rooms[0].Zone;
            return zone is FacilityZone.LightContainment or FacilityZone.HeavyContainment or FacilityZone.Entrance or FacilityZone.Surface;
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
