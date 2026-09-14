using MapGeneration;
using SCPSLBot.AI.FirstPersonControl;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace SCPSLBot.Navigation.Runtime
{
    /// <summary>The runtime navmesh exposed through the shared backend surface.</summary>
    internal sealed class RuntimeNavigationBackend : INavigationBackend
    {
        private readonly RuntimeNavMeshService service;

        public RuntimeNavigationBackend(RuntimeNavMeshService service)
        {
            this.service = service;
        }

        public RuntimeNavMeshService Service => service;

        public string Name => "runtime";

        public int TopologyVersion => service.SurfaceGeneration;

        public IBotNavigator CreateNavigator(FpcBotPlayer botPlayer) => new RuntimeBotNavigator(botPlayer, service);

        public bool RoomHasNavigation(RoomIdentifier room) => service.IsBuilt && service.Rooms.HasNavigation(room);

        public IReadOnlyList<Vector3> GetRoomSamples(RoomIdentifier room) => service.Rooms.GetSamples(room);

        public IEnumerable<RoomIdentifier> GetNavigableRooms() => service.Rooms.NavigableRooms;

        public void GetForeignRoomEntries(RoomIdentifier room, List<RoomEntry> results) => service.Rooms.GetEntries(room, results);

        public bool TryGetNearestRoomSample(RoomIdentifier room, Vector3 probe, float maxHorizontalDistance, float maxHeightDifference, out Vector3 sample)
        {
            sample = default;
            var best = float.PositiveInfinity;
            foreach (var candidate in service.Rooms.GetSamples(room))
            {
                if (Mathf.Abs(candidate.y - probe.y) > maxHeightDifference)
                {
                    continue;
                }

                var distance = Vector3.Distance(Vector3.ProjectOnPlane(candidate, Vector3.up), Vector3.ProjectOnPlane(probe, Vector3.up));
                if (distance < best)
                {
                    best = distance;
                    sample = candidate;
                }
            }

            // The lattice is 2 m coarse; snap the probe itself when it lies on the mesh so survey
            // anchors and room orders land where asked instead of a lattice point away.
            if (best <= maxHorizontalDistance
                && NavMesh.SamplePosition(probe, out var hit, 1f, NavMesh.AllAreas)
                && Mathf.Abs(hit.position.y - probe.y) <= maxHeightDifference
                && RoomUtils.TryGetRoom(hit.position + Vector3.up * 0.5f, out var hitRoom)
                && hitRoom == room)
            {
                sample = hit.position;
                return true;
            }

            return best <= maxHorizontalDistance;
        }

        public bool TryGetConnectedRoomSample(RoomIdentifier room, Vector3 probe, float maxHorizontalDistance, float maxHeightDifference, out Vector3 sample)
        {
            sample = default;
            return service.IsBuilt && service.Rooms.TryGetConnectedSample(room, probe, maxHorizontalDistance, maxHeightDifference, out sample);
        }

        public bool TryGetReachableRoomSample(RoomIdentifier room, Vector3 origin, int areaMask, out Vector3 goal)
        {
            goal = default;
            return service.IsBuilt && service.Rooms.TryGetReachableSample(room, origin, areaMask, out goal);
        }

        public bool TryGetReachableRoomSample(RoomIdentifier room, Vector3 origin, Vector3 probe, float maxHorizontalDistance, float maxHeightDifference, int areaMask, out Vector3 goal)
        {
            goal = default;
            return service.IsBuilt && service.Rooms.TryGetReachableSample(room, origin, probe, maxHorizontalDistance, maxHeightDifference, areaMask, out goal);
        }

        public bool IsOnMesh(Vector3 position, float maxDistance)
        {
            return service.IsBuilt && NavMesh.SamplePosition(position, out _, Mathf.Max(0.5f, maxDistance), NavMesh.AllAreas);
        }

        public bool TryGetNearestPoint(Vector3 position, float maxDistance, out Vector3 nearest, out float distance)
        {
            if (service.IsBuilt && NavMesh.SamplePosition(position, out var hit, maxDistance, NavMesh.AllAreas))
            {
                nearest = hit.position;
                distance = hit.distance;
                return true;
            }

            nearest = default;
            distance = float.PositiveInfinity;
            return false;
        }

        public string Diagnostics
        {
            get
            {
                return $"nav_triangles={service.Triangles}; nav_unreadable_meshes={service.UnreadableMeshes}; nav_links={service.Links.Count}; nav_passage_links={service.Links.PassageLinks}; nav_sealed_connectors={service.Links.SealedConnectors}; nav_last_bake_ms={service.LastBakeMs}; "
                       + $"nav_door_classes={service.Areas.ClassCount}; nav_fallback_rooms={service.FallbackFloorRooms}; nav_uncovered_rooms={service.Rooms.UncoveredRooms.Count}; nav_island_samples={service.Rooms.IslandSamples}; "
                       + $"nav_reconciles={service.ReconcileCount}; nav_reconcile_rebuilds={service.ReconcileRebuilds}; nav_obstacles={service.Obstacles.Count}";
            }
        }
    }
}
