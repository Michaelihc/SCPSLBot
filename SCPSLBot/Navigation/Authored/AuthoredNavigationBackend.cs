using MapGeneration;
using SCPSLBot.AI.FirstPersonControl;
using SCPSLBot.Navigation.Mesh;
using System.Collections.Generic;
using UnityEngine;

namespace SCPSLBot.Navigation.Authored
{
    /// <summary>
    /// The hand-authored cell mesh (navmesh.slnmf plus runtime room fill and connector probes)
    /// exposed through the shared backend surface. Thin adapters over the NavigationMesh statics.
    /// </summary>
    internal sealed class AuthoredNavigationBackend : INavigationBackend
    {
        private readonly Dictionary<GameObject, List<Vector3>> roomSamples = new();
        private int roomSamplesVersion = -1;

        public string Name => "authored";

        public int TopologyVersion => NavigationMesh.TopologyVersion;

        public IBotNavigator CreateNavigator(FpcBotPlayer botPlayer) => new AuthoredBotNavigator(botPlayer);

        public bool RoomHasNavigation(RoomIdentifier room)
        {
            return room != null
                   && NavigationMesh.LocalMeshesByRoom.TryGetValue(room.gameObject, out var mesh)
                   && mesh.Cells.Count > 0;
        }

        public IReadOnlyList<Vector3> GetRoomSamples(RoomIdentifier room)
        {
            if (room == null || !NavigationMesh.LocalMeshesByRoom.TryGetValue(room.gameObject, out var mesh))
            {
                return System.Array.Empty<Vector3>();
            }

            if (roomSamplesVersion != NavigationMesh.TopologyVersion)
            {
                roomSamples.Clear();
                roomSamplesVersion = NavigationMesh.TopologyVersion;
            }

            if (!roomSamples.TryGetValue(room.gameObject, out var samples))
            {
                samples = new List<Vector3>(mesh.Cells.Count);
                foreach (var cell in mesh.Cells)
                {
                    samples.Add(room.transform.TransformPoint(cell.CenterPosition));
                }

                roomSamples[room.gameObject] = samples;
            }

            return samples;
        }

        public IEnumerable<RoomIdentifier> GetNavigableRooms()
        {
            foreach (var pair in NavigationMesh.LocalMeshesByRoom)
            {
                if (pair.Value.Cells.Count == 0 || pair.Key == null)
                {
                    continue;
                }

                var room = pair.Key.GetComponent<RoomIdentifier>();
                if (room != null)
                {
                    yield return room;
                }
            }
        }

        public void GetForeignRoomEntries(RoomIdentifier room, List<RoomEntry> results)
        {
            results.Clear();
            if (room == null || !NavigationMesh.LocalMeshesByRoom.TryGetValue(room.gameObject, out var roomMesh))
            {
                return;
            }

            foreach (var localCell in roomMesh.Cells)
            {
                var transformCell = new TransformCell(localCell, room.transform);
                foreach (var foreignCell in NavigationMesh.GetForeignConnectedCells(transformCell))
                {
                    var foreignRoom = foreignCell.Transform != null ? foreignCell.Transform.GetComponent<RoomIdentifier>() : null;
                    if (foreignRoom == null || foreignRoom == room)
                    {
                        continue;
                    }

                    // The foreign portal cell itself is a boundary cell; its first adjacent local
                    // cell lies further inside the room and is the point worth walking to.
                    var target = foreignCell.Local?.AdjacentCells != null && foreignCell.Local.AdjacentCells.Count > 0
                        ? new TransformCell(foreignCell.Local.AdjacentCells[0], foreignCell.Transform).CenterPosition
                        : foreignCell.CenterPosition;
                    results.Add(new RoomEntry(foreignRoom, target));
                }
            }
        }

        public bool TryGetNearestRoomSample(RoomIdentifier room, Vector3 probe, float maxHorizontalDistance, float maxHeightDifference, out Vector3 sample)
        {
            sample = default;
            var best = float.PositiveInfinity;
            foreach (var candidate in GetRoomSamples(room))
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

            return best <= maxHorizontalDistance;
        }

        public bool TryGetConnectedRoomSample(RoomIdentifier room, Vector3 probe, float maxHorizontalDistance, float maxHeightDifference, out Vector3 sample)
            => TryGetNearestRoomSample(room, probe, maxHorizontalDistance, maxHeightDifference, out sample);

        public bool TryGetReachableRoomSample(RoomIdentifier room, Vector3 origin, int areaMask, out Vector3 goal)
        {
            goal = default;
            var best = float.PositiveInfinity;
            foreach (var sample in GetRoomSamples(room))
            {
                var distance = Vector3.Distance(Vector3.ProjectOnPlane(sample, Vector3.up), Vector3.ProjectOnPlane(origin, Vector3.up));
                if (distance < best)
                {
                    best = distance;
                    goal = sample;
                }
            }

            return !float.IsPositiveInfinity(best);
        }

        public bool TryGetReachableRoomSample(RoomIdentifier room, Vector3 origin, Vector3 probe, float maxHorizontalDistance, float maxHeightDifference, int areaMask, out Vector3 goal)
            => TryGetNearestRoomSample(room, probe, maxHorizontalDistance, maxHeightDifference, out goal);

        public bool IsOnMesh(Vector3 position, float maxDistance)
        {
            if (NavigationMesh.GetCellWithin(position) != null)
            {
                return true;
            }

            return maxDistance > 0f
                   && RoomUtils.TryGetRoom(position, out var room)
                   && room != null
                   && NavigationMesh.TryGetNearestCell(position, room, maxDistance, out _);
        }

        public bool TryGetNearestPoint(Vector3 position, float maxDistance, out Vector3 nearest, out float distance)
        {
            nearest = Vector3.zero;
            var bestSqr = maxDistance * maxDistance;
            var found = false;
            foreach (var pair in NavigationMesh.LocalMeshesByRoom)
            {
                var transform = pair.Key.transform;
                foreach (var cell in pair.Value.Cells)
                {
                    var center = transform.TransformPoint(cell.CenterPosition);
                    var centerSqr = (center - position).sqrMagnitude;
                    if (centerSqr < bestSqr)
                    {
                        bestSqr = centerSqr;
                        nearest = center;
                        found = true;
                    }

                    foreach (var vertex in cell.Vertices)
                    {
                        var point = transform.TransformPoint(vertex.Position);
                        var pointSqr = (point - position).sqrMagnitude;
                        if (pointSqr < bestSqr)
                        {
                            bestSqr = pointSqr;
                            nearest = point;
                            found = true;
                        }
                    }
                }
            }

            distance = found ? Mathf.Sqrt(bestSqr) : float.PositiveInfinity;
            return found;
        }

        public string Diagnostics
        {
            get
            {
                var navigation = NavigationSystem.Instance;
                return $"nav_generated_rooms={navigation.GeneratedRoomForms}; nav_probed_connectors={navigation.ProbedConnectors}; nav_sealed_connectors={navigation.SealedConnectors}; nav_penalized_links={NavigationMesh.PenalizedLinkCount}";
            }
        }
    }
}
