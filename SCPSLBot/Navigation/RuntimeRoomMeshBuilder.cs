using MapGeneration;
using PlayerRoles.FirstPersonControl;
using SCPSLBot.Navigation.Mesh;
using System.Collections.Generic;
using UnityEngine;
using LabLogger = LabApi.Features.Console.Logger;

namespace SCPSLBot.Navigation
{
    /// <summary>
    /// Fills navigation gaps for room prefabs that have no authored cells by probing the live floor
    /// and collision geometry into a coarse grid of convex cells. Generated cells share vertices so
    /// native adjacency works, are marked generated so they are never persisted, and respect the
    /// current clutter because they are derived from the colliders that actually exist this round.
    /// </summary>
    internal static class RuntimeRoomMeshBuilder
    {
        private const float GridStep = 1f;
        private const float MaxCellHeightDelta = 0.75f;
        private const float MinWalkableNormalY = 0.7f;
        // Probes start just below a normal room ceiling (rooms sit at floor level) so ceilings and
        // overhead props are never mistaken for floors; depth covers sunken floors and ramps.
        private const float ProbeStartLocalY = 2.6f;
        private const float ProbeDepth = 12f;
        private const float BoundsPadding = 0.5f;

        // Doors open at runtime, so a closed leaf must not carve the doorway cells out of the grid.
        private static int ProbeMask => FpcStateProcessor.Mask & ~LayerMask.GetMask("Player", "Hitbox", "Door");

        /// <summary>Generates cells for every room whose form currently has none. Returns the number of forms filled.</summary>
        public static int FillRoomsWithoutCells()
        {
            var filledForms = 0;
            var visitedForms = new HashSet<string>();
            foreach (var room in RoomIdentifier.AllRoomIdentifiers)
            {
                if (room == null || room.Name == RoomName.Pocket)
                {
                    continue;
                }

                var form = NavigationMesh.GetForm(room.gameObject);
                if (!visitedForms.Add(form))
                {
                    continue;
                }

                if (!NavigationMesh.LocalMeshesByRoom.TryGetValue(room.gameObject, out var mesh) || mesh == null)
                {
                    continue;
                }

                if (mesh.Cells.Count > 0)
                {
                    continue;
                }

                var cellCount = Generate(room, mesh);
                mesh.IsGenerated = cellCount > 0;
                if (cellCount > 0)
                {
                    filledForms++;
                    LabLogger.Info($"[SCPSLBot] NAV_ROOM_GENERATED form={form} name={room.Name} cells={cellCount}");
                }
                else
                {
                    LabLogger.Warn($"[SCPSLBot] NAV_ROOM_UNFILLED form={form} name={room.Name}: no walkable grid cells were found");
                }
            }

            return filledForms;
        }

        private static int Generate(RoomIdentifier room, NavigationMesh mesh)
        {
            var transform = room.transform;
            var bounds = room.WorldspaceBounds;
            if (bounds.size.sqrMagnitude < 1f)
            {
                bounds = new Bounds(transform.position, new Vector3(15f, 6f, 15f));
            }

            // Local-space extents of the world bounds (rooms are axis-aligned in 90 degree steps).
            var min = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
            var max = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
            for (var corner = 0; corner < 8; corner++)
            {
                var world = new Vector3(
                    (corner & 1) == 0 ? bounds.min.x : bounds.max.x,
                    (corner & 2) == 0 ? bounds.min.y : bounds.max.y,
                    (corner & 4) == 0 ? bounds.min.z : bounds.max.z);
                var local = transform.InverseTransformPoint(world);
                min = Vector3.Min(min, local);
                max = Vector3.Max(max, local);
            }

            var startX = Mathf.Floor((min.x - BoundsPadding) / GridStep) * GridStep;
            var startZ = Mathf.Floor((min.z - BoundsPadding) / GridStep) * GridStep;
            var columns = Mathf.Clamp(Mathf.CeilToInt((max.x + BoundsPadding - startX) / GridStep), 1, 64);
            var rows = Mathf.Clamp(Mathf.CeilToInt((max.z + BoundsPadding - startZ) / GridStep), 1, 64);
            var probeTopLocalY = ProbeStartLocalY;

            var radius = NavigationAgentProfile.Radius;
            var rootHeight = NavigationAgentProfile.RootHeight;
            var bottomOffset = NavigationAgentProfile.StepClearance + radius;
            var topOffset = Mathf.Max(bottomOffset + 0.05f, NavigationAgentProfile.Height - radius - 0.05f);
            var mask = ProbeMask;

            // Floor height per grid vertex (local), NaN when there is no walkable floor of this room.
            var floorY = new float[columns + 1, rows + 1];
            for (var i = 0; i <= columns; i++)
            {
                for (var j = 0; j <= rows; j++)
                {
                    floorY[i, j] = ProbeFloor(room, transform.TransformPoint(new Vector3(startX + i * GridStep, probeTopLocalY, startZ + j * GridStep)), mask);
                }
            }

            var vertices = new Dictionary<(int, int), Vertex>();
            var cellCount = 0;
            for (var i = 0; i < columns; i++)
            {
                for (var j = 0; j < rows; j++)
                {
                    var y00 = floorY[i, j];
                    var y01 = floorY[i, j + 1];
                    var y11 = floorY[i + 1, j + 1];
                    var y10 = floorY[i + 1, j];
                    if (float.IsNaN(y00) || float.IsNaN(y01) || float.IsNaN(y11) || float.IsNaN(y10))
                    {
                        continue;
                    }

                    var lowest = Mathf.Min(Mathf.Min(y00, y01), Mathf.Min(y11, y10));
                    var highest = Mathf.Max(Mathf.Max(y00, y01), Mathf.Max(y11, y10));
                    if (highest - lowest > MaxCellHeightDelta)
                    {
                        continue;
                    }

                    var centerLocal = new Vector3(startX + (i + 0.5f) * GridStep, (y00 + y01 + y11 + y10) * 0.25f, startZ + (j + 0.5f) * GridStep);
                    var centerFloorWorld = transform.TransformPoint(centerLocal);
                    if (Physics.CheckCapsule(centerFloorWorld + Vector3.up * bottomOffset, centerFloorWorld + Vector3.up * topOffset, radius, mask, QueryTriggerInteraction.Ignore))
                    {
                        continue;
                    }

                    // Clockwise from above so edge planes face inward like the authored meshes.
                    var cellVertices = new[]
                    {
                        GetVertex(vertices, i, j, startX, startZ, y00 + rootHeight),
                        GetVertex(vertices, i, j + 1, startX, startZ, y01 + rootHeight),
                        GetVertex(vertices, i + 1, j + 1, startX, startZ, y11 + rootHeight),
                        GetVertex(vertices, i + 1, j, startX, startZ, y10 + rootHeight),
                    };
                    mesh.MakeCell(cellVertices);
                    cellCount++;
                }
            }

            return cellCount;
        }

        private static Vertex GetVertex(Dictionary<(int, int), Vertex> vertices, int i, int j, float startX, float startZ, float y)
        {
            if (!vertices.TryGetValue((i, j), out var vertex))
            {
                vertex = new Vertex(new Vector3(startX + i * GridStep, y, startZ + j * GridStep));
                vertices[(i, j)] = vertex;
            }

            return vertex;
        }

        private static float ProbeFloor(RoomIdentifier room, Vector3 worldTop, int mask)
        {
            if (!Physics.Raycast(worldTop, Vector3.down, out var hit, ProbeDepth, mask, QueryTriggerInteraction.Ignore))
            {
                return float.NaN;
            }

            if (hit.normal.y < MinWalkableNormalY)
            {
                return float.NaN;
            }

            if (!RoomUtils.TryGetRoom(hit.point + Vector3.up * 0.5f, out var hitRoom) || hitRoom != room)
            {
                return float.NaN;
            }

            return room.transform.InverseTransformPoint(hit.point).y;
        }
    }
}
