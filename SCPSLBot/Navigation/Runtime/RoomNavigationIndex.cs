using Interactables.Interobjects;
using Interactables.Interobjects.DoorUtils;
using MapGeneration;
using MapGeneration.RoomConnectors;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace SCPSLBot.Navigation.Runtime
{
    /// <summary>
    /// Per-room view of the baked navmesh: a lattice of reachable samples inside every room (roam
    /// targets, order goals, survey anchors) and one entry point per neighbouring room, sampled
    /// just past the shared doorway, connector or elevator. Rebuilt after every bake that changed
    /// the surface; queries are dictionary lookups.
    /// </summary>
    internal sealed class RoomNavigationIndex
    {
        private const float SampleSpacing = 2f;
        private const float SampleVerticalStep = 3f;
        private const float SampleRadius = 1.6f;
        private const int MaxSamplesPerRoom = 4000;
        private const float EntryDepth = 1.5f;
        private const float EntrySampleRadius = 2.5f;

        private static readonly IReadOnlyList<Vector3> NoSamples = new List<Vector3>(0);

        private readonly Dictionary<RoomIdentifier, List<Vector3>> samplesByRoom = new();
        private readonly Dictionary<RoomIdentifier, List<RoomEntry>> entriesByRoom = new();
        private readonly List<RoomIdentifier> navigableRooms = new();
        private readonly List<string> uncoveredRooms = new();

        public IReadOnlyList<RoomIdentifier> NavigableRooms => navigableRooms;

        /// <summary>Rooms (form names) without any sample this map.</summary>
        public IReadOnlyList<string> UncoveredRooms => uncoveredRooms;

        public int SampleCount { get; private set; }

        public int EntryCount { get; private set; }

        /// <summary>Elevated samples discarded because no path connects them to the room floor (crate tops, ledges).</summary>
        public int IslandSamples { get; private set; }

        public long LastRebuildMs { get; private set; }

        public void Clear()
        {
            samplesByRoom.Clear();
            entriesByRoom.Clear();
            navigableRooms.Clear();
            uncoveredRooms.Clear();
            SampleCount = 0;
            EntryCount = 0;
            IslandSamples = 0;
        }

        public void Rebuild(NavMeshLinkRegistry links)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            Clear();
            foreach (var room in RoomIdentifier.AllRoomIdentifiers)
            {
                if (room == null || room.Name == RoomName.Pocket)
                {
                    continue;
                }

                var samples = SampleRoom(room);
                IslandSamples += RemoveIslands(room, samples);
                if (samples.Count == 0)
                {
                    uncoveredRooms.Add(room.gameObject.name);
                    continue;
                }

                samplesByRoom[room] = samples;
                navigableRooms.Add(room);
                SampleCount += samples.Count;
            }

            BuildEntries(links);
            stopwatch.Stop();
            LastRebuildMs = stopwatch.ElapsedMilliseconds;
        }

        /// <summary>
        /// The room sample nearest <paramref name="probe"/> (same floor) from which the room's floor
        /// hub is reachable; skips floor-level pockets between bars or inside cages.
        /// </summary>
        /// <summary>Areas a bot without any keycard may use: walkable, jump links and elevator links.</summary>
        public static int PlainAreaMask => (1 << Policy.DoorAreaRegistry.WalkableArea) | (1 << Policy.DoorAreaRegistry.JumpArea) | (1 << Policy.DoorAreaRegistry.ElevatorArea);

        public bool TryGetConnectedSample(RoomIdentifier room, Vector3 probe, float maxHorizontalDistance, float maxHeightDifference, out Vector3 sample)
            => TryGetConnectedSample(room, probe, maxHorizontalDistance, maxHeightDifference, PlainAreaMask, out sample);

        public bool TryGetConnectedSample(RoomIdentifier room, Vector3 probe, float maxHorizontalDistance, float maxHeightDifference, int areaMask, out Vector3 sample)
        {
            sample = default;
            var samples = GetSamples(room);
            if (samples.Count == 0)
            {
                return false;
            }

            var ordered = new List<Vector3>();
            foreach (var candidate in samples)
            {
                if (Mathf.Abs(candidate.y - probe.y) <= maxHeightDifference && HorizontalDistance(candidate, probe) <= maxHorizontalDistance)
                {
                    ordered.Add(candidate);
                }
            }

            if (ordered.Count == 0)
            {
                return false;
            }

            ordered.Sort((a, b) => HorizontalDistance(probe, a).CompareTo(HorizontalDistance(probe, b)));
            var hub = FindHub(room, samples);
            var path = new NavMeshPath();
            var plainMask = (1 << Policy.DoorAreaRegistry.WalkableArea) | (1 << Policy.DoorAreaRegistry.JumpArea);
            var attempts = ordered.Count;
            var fallback = false;
            var fallbackSample = ordered[0];
            for (var index = 0; index < attempts; index++)
            {
                var candidate = ordered[index];
                if (!NavMesh.SamplePosition(candidate, out var candidateHit, 0.3f, NavMesh.AllAreas)
                    || !NavMesh.CalculatePath(candidate, hub, areaMask | candidateHit.mask, path)
                    || path.status != NavMeshPathStatus.PathComplete)
                {
                    continue;
                }

                // A sample inside a keycard doorway is a poor anchor/goal for a bot without the card.
                if ((candidateHit.mask & ~plainMask) != 0)
                {
                    if (!fallback)
                    {
                        fallback = true;
                        fallbackSample = candidate;
                    }

                    continue;
                }

                sample = candidate;
                return true;
            }

            sample = fallbackSample;
            return true;
        }

        /// <summary>
        /// The room sample nearest <paramref name="probe"/> (same floor, within range) that a complete
        /// path from <paramref name="origin"/> reaches with <paramref name="areaMask"/>; false when none.
        /// </summary>
        public bool TryGetReachableSample(RoomIdentifier room, Vector3 origin, Vector3 probe, float maxHorizontalDistance, float maxHeightDifference, int areaMask, out Vector3 goal)
        {
            goal = default;
            var candidates = new List<Vector3>();
            foreach (var candidate in GetSamples(room))
            {
                if (Mathf.Abs(candidate.y - probe.y) <= maxHeightDifference && HorizontalDistance(candidate, probe) <= maxHorizontalDistance)
                {
                    candidates.Add(candidate);
                }
            }

            if (candidates.Count == 0 || !NavMesh.SamplePosition(origin, out var startHit, 6f, NavMesh.AllAreas))
            {
                return false;
            }

            candidates.Sort((a, b) => HorizontalDistance(probe, a).CompareTo(HorizontalDistance(probe, b)));
            var path = new NavMeshPath();
            foreach (var candidate in candidates)
            {
                if (NavMesh.CalculatePath(startHit.position, candidate, areaMask | startHit.mask, path) && path.status == NavMeshPathStatus.PathComplete)
                {
                    goal = candidate;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// The room sample nearest <paramref name="origin"/> that a complete path reaches. Samples
        /// are tried nearest first; false when none of the nearest few is reachable.
        /// </summary>
        public bool TryGetReachableSample(RoomIdentifier room, Vector3 origin, int areaMask, out Vector3 goal)
        {
            goal = default;
            var samples = GetSamples(room);
            if (samples.Count == 0)
            {
                return false;
            }

            var ordered = new List<Vector3>(samples);
            ordered.Sort((a, b) => HorizontalDistance(origin, a).CompareTo(HorizontalDistance(origin, b)));
            if (!NavMesh.SamplePosition(origin, out var startHit, 6f, NavMesh.AllAreas))
            {
                goal = ordered[0];
                return true;
            }

            var path = new NavMeshPath();
            var attempts = Mathf.Min(8, ordered.Count);
            for (var index = 0; index < attempts; index++)
            {
                if (NavMesh.CalculatePath(startHit.position, ordered[index], areaMask | startHit.mask, path) && path.status == NavMeshPathStatus.PathComplete)
                {
                    goal = ordered[index];
                    return true;
                }
            }

            return false;
        }

        // Elevated samples (crate tops, table tops, ledges) are polygons of their own that the
        // builder keeps once they exceed the minimum region area. Only keep the ones a complete
        // path connects to the room's main floor; floor-level samples are trusted as they are.
        private static int RemoveIslands(RoomIdentifier room, List<Vector3> samples)
        {
            if (samples.Count < 2)
            {
                return 0;
            }

            var hub = FindHub(room, samples);
            var path = new NavMeshPath();
            var removed = 0;
            for (var index = samples.Count - 1; index >= 0; index--)
            {
                var sample = samples[index];
                if (Mathf.Abs(sample.y - hub.y) <= 0.4f)
                {
                    continue;
                }

                // Searching from the sample keeps a failing query cheap: an island is exhausted in
                // a handful of polygons, whereas a search from the floor would flood the whole map.
                if (NavMesh.CalculatePath(sample, hub, NavMesh.AllAreas, path) && path.status == NavMeshPathStatus.PathComplete)
                {
                    continue;
                }

                samples.RemoveAt(index);
                removed++;
            }

            return removed;
        }

        // The hub is the floor-level sample nearest the pivot; a pipe top or ledge nearest the
        // pivot would otherwise disqualify the whole floor.
        private static Vector3 FindHub(RoomIdentifier room, IReadOnlyList<Vector3> samples)
        {
            var pivot = room.transform.position;
            var hub = samples[0];
            var hubDistance = float.PositiveInfinity;
            var hubFound = false;
            foreach (var sample in samples)
            {
                if (Mathf.Abs(sample.y - pivot.y) > 0.6f)
                {
                    continue;
                }

                var distance = Vector3.Distance(sample, pivot);
                if (distance < hubDistance)
                {
                    hubDistance = distance;
                    hub = sample;
                    hubFound = true;
                }
            }

            if (!hubFound)
            {
                // Multi-level rooms whose pivot is not on a floor: take the lowest sample nearest the pivot.
                var lowest = float.PositiveInfinity;
                foreach (var sample in samples)
                {
                    lowest = Mathf.Min(lowest, sample.y);
                }

                foreach (var sample in samples)
                {
                    if (sample.y > lowest + 0.6f)
                    {
                        continue;
                    }

                    var distance = Vector3.Distance(sample, pivot);
                    if (distance < hubDistance)
                    {
                        hubDistance = distance;
                        hub = sample;
                    }
                }
            }

            return hub;
        }

        private static float HorizontalDistance(Vector3 a, Vector3 b)
            => Vector3.Distance(Vector3.ProjectOnPlane(a, Vector3.up), Vector3.ProjectOnPlane(b, Vector3.up));

        public bool HasNavigation(RoomIdentifier room) => room != null && samplesByRoom.ContainsKey(room);

        public IReadOnlyList<Vector3> GetSamples(RoomIdentifier room)
        {
            return room != null && samplesByRoom.TryGetValue(room, out var samples) ? samples : NoSamples;
        }

        public void GetEntries(RoomIdentifier room, List<RoomEntry> results)
        {
            results.Clear();
            if (room != null && entriesByRoom.TryGetValue(room, out var entries))
            {
                results.AddRange(entries);
            }
        }

        private static List<Vector3> SampleRoom(RoomIdentifier room)
        {
            var samples = new List<Vector3>();
            var bounds = room.WorldspaceBounds;
            if (bounds.size.sqrMagnitude < 1f)
            {
                bounds = new Bounds(room.transform.position, new Vector3(15f, 6f, 15f));
            }

            bounds.Expand(1f);
            var area = Mathf.Max(1f, bounds.size.x * bounds.size.z);
            var spacing = Mathf.Max(SampleSpacing, Mathf.Sqrt(area / MaxSamplesPerRoom));
            var columns = Mathf.Max(1, Mathf.CeilToInt(bounds.size.x / spacing));
            var rows = Mathf.Max(1, Mathf.CeilToInt(bounds.size.z / spacing));
            var layers = Mathf.Max(1, Mathf.CeilToInt(bounds.size.y / SampleVerticalStep));
            var seen = new HashSet<(int, int, int)>();
            for (var i = 0; i <= columns; i++)
            {
                for (var j = 0; j <= rows; j++)
                {
                    for (var k = 0; k <= layers; k++)
                    {
                        var probe = new Vector3(
                            bounds.min.x + i * spacing,
                            bounds.min.y + k * SampleVerticalStep,
                            bounds.min.z + j * spacing);
                        if (!NavMesh.SamplePosition(probe, out var hit, SampleRadius, NavMesh.AllAreas))
                        {
                            continue;
                        }

                        if (!RoomUtils.TryGetRoom(hit.position + Vector3.up * 0.5f, out var hitRoom) || hitRoom != room)
                        {
                            continue;
                        }

                        var key = (Mathf.RoundToInt(hit.position.x), Mathf.RoundToInt(hit.position.y * 2f), Mathf.RoundToInt(hit.position.z));
                        if (seen.Add(key))
                        {
                            samples.Add(hit.position);
                        }
                    }
                }
            }

            return samples;
        }

        private void BuildEntries(NavMeshLinkRegistry links)
        {
            foreach (var door in DoorVariant.AllDoors)
            {
                if (door == null || door is ElevatorDoor)
                {
                    continue;
                }

                AddPortal(door.transform);
            }

            foreach (var connector in Object.FindObjectsByType<SpawnableRoomConnector>(FindObjectsSortMode.None))
            {
                if (connector == null || connector.GetComponentInChildren<DoorVariant>() != null)
                {
                    continue;
                }

                AddPortal(connector.transform);
            }

            foreach (var link in links.Links)
            {
                AddEntry(RoomAt(link.LandingA), RoomAt(link.LandingB), link.LandingB);
                AddEntry(RoomAt(link.LandingB), RoomAt(link.LandingA), link.LandingA);
            }
        }

        private void AddPortal(Transform portal)
        {
            var center = portal.position + Vector3.up * 0.5f;
            var forward = Vector3.ProjectOnPlane(portal.forward, Vector3.up).normalized;
            if (forward.sqrMagnitude < 0.5f)
            {
                return;
            }

            var front = RoomBeyond(center, forward);
            var back = RoomBeyond(center, -forward);
            if (front == null || back == null || front == back)
            {
                return;
            }

            if (TrySampleEntry(center + forward * EntryDepth, front, out var frontEntry))
            {
                AddEntry(back, front, frontEntry);
            }

            if (TrySampleEntry(center - forward * EntryDepth, back, out var backEntry))
            {
                AddEntry(front, back, backEntry);
            }
        }

        private static RoomIdentifier RoomBeyond(Vector3 center, Vector3 direction)
        {
            for (var distance = 1.5f; distance <= 4.5f; distance += 1.5f)
            {
                if (RoomUtils.TryGetRoom(center + direction * distance, out var room) && room != null)
                {
                    return room;
                }
            }

            return null;
        }

        private static RoomIdentifier RoomAt(Vector3 position)
        {
            return RoomUtils.TryGetRoom(position + Vector3.up * 0.5f, out var room) ? room : null;
        }

        private static bool TrySampleEntry(Vector3 probe, RoomIdentifier room, out Vector3 entry)
        {
            entry = default;
            if (!NavMesh.SamplePosition(probe, out var hit, EntrySampleRadius, NavMesh.AllAreas))
            {
                return false;
            }

            if (!RoomUtils.TryGetRoom(hit.position + Vector3.up * 0.5f, out var hitRoom) || hitRoom != room)
            {
                return false;
            }

            entry = hit.position;
            return true;
        }

        private void AddEntry(RoomIdentifier from, RoomIdentifier to, Vector3 position)
        {
            if (from == null || to == null || from == to)
            {
                return;
            }

            if (!entriesByRoom.TryGetValue(from, out var entries))
            {
                entries = new List<RoomEntry>();
                entriesByRoom[from] = entries;
            }

            foreach (var existing in entries)
            {
                if (existing.Room == to && Vector3.Distance(existing.Position, position + Vector3.up * NavigationAgentProfile.RootHeight) < 1f)
                {
                    return;
                }
            }

            // Entries are walk-to points; like the authored cell centers they sit at capsule height.
            entries.Add(new RoomEntry(to, position + Vector3.up * NavigationAgentProfile.RootHeight));
            EntryCount++;
        }
    }
}
