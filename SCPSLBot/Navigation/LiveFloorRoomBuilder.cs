using Interactables.Interobjects;
using Interactables.Interobjects.DoorUtils;
using MapGeneration;
using PlayerRoles.FirstPersonControl;
using SCPSLBot.Navigation.Mesh;
using System;
using System.Collections.Generic;
using UnityEngine;
using LabLogger = LabApi.Features.Console.Logger;

namespace SCPSLBot.Navigation
{
    /// <summary>
    /// Replaces a room form's cells with a grid walked over the live floor. Each step is probed from
    /// just above the floor it left, so ceilings and upper levels never read as floor, and stacked
    /// levels, stairs and raised landings all come out connected the way a capsule can walk them.
    /// Cells are one grid square around each walkable point, in room space, so the result holds for
    /// any placement of the room.
    /// </summary>
    internal static class LiveFloorRoomBuilder
    {
        private const float GridStep = 1f;
        private const float MinWalkableNormalY = 0.7f;
        private const float ProbeStart = 1.4f;
        private const float ProbeDepth = 2.8f;
        // One grid square of a steep stair flight; the midpoint capsule rejects real ledges.
        private const float MaxStepHeight = 1.1f;
        private const float SameFloorTolerance = 0.3f;
        private const int SeedSearchRadius = 1;
        private const int MaxNodes = 6000;
        private const float ShaftHalfWidth = 2.2f;
        private const float ShaftDepth = 5f;
        private const float ShaftFrontMargin = 0.3f;
        private const float ShaftVerticalReach = 3f;

        private static readonly (int I, int J)[] Neighbours = { (1, 0), (-1, 0), (0, 1), (0, -1) };
        private static readonly Collider[] Overlaps = new Collider[32];

        // Doors open at runtime, so a closed leaf must not cut the walk off.
        public static int ProbeMask => FpcStateProcessor.Mask & ~LayerMask.GetMask("Player", "Hitbox", "Door");

        private sealed class Node
        {
            public int I;
            public int J;
            public int Index;
            /// <summary>Floor height in room space.</summary>
            public float Y;
        }

        private sealed class Walk
        {
            public RoomIdentifier Room;
            public int Mask;
            public List<ElevatorDoor> ElevatorDoors = new();
            public HashSet<Collider> DoorLeaves = new();
            public Dictionary<(int, int), List<Node>> Columns = new();
            public List<Node> Nodes = new();
            public Queue<Node> Open = new();
            public List<(Node From, Node To)> Links = new();
        }

        /// <summary>
        /// Rebuilds <paramref name="mesh"/> from <paramref name="landings"/> and every existing cell
        /// that still has floor under it. Returns false, leaving the form untouched, when no floor is found.
        /// </summary>
        public static bool Rebuild(RoomIdentifier room, NavigationMesh mesh, IReadOnlyList<Vector3> landings)
        {
            var walk = new Walk { Room = room, Mask = ProbeMask };
            foreach (ElevatorGroup group in Enum.GetValues(typeof(ElevatorGroup)))
            {
                walk.ElevatorDoors.AddRange(ElevatorDoor.GetDoorsForGroup(group));
            }

            foreach (var door in DoorVariant.AllDoors)
            {
                if (door != null && door is not ElevatorDoor && door.AllColliders != null)
                {
                    walk.DoorLeaves.UnionWith(door.AllColliders);
                }
            }

            foreach (var landing in landings)
            {
                TrySeed(walk, room.transform.InverseTransformPoint(landing));
            }

            foreach (var cell in mesh.Cells)
            {
                TrySeed(walk, cell.CenterPosition - Vector3.up * NavigationAgentProfile.RootHeight);
            }

            while (walk.Open.Count > 0 && walk.Nodes.Count < MaxNodes)
            {
                var node = walk.Open.Dequeue();
                foreach (var (stepI, stepJ) in Neighbours)
                {
                    var toI = node.I + stepI;
                    var toJ = node.J + stepJ;
                    if (!TryProbeFloor(walk, toI, toJ, node.Y, out var floorY) || !CanStep(walk, node, toI, toJ, floorY))
                    {
                        continue;
                    }

                    var next = GetOrAddNode(walk, toI, toJ, floorY);
                    walk.Links.Add(stepI + stepJ > 0 ? (node, next) : (next, node));
                }
            }

            var form = NavigationMesh.GetForm(room.gameObject);
            if (walk.Nodes.Count == 0)
            {
                LabLogger.Warn($"[SCPSLBot] NAV_ROOM_UNFILLED form={form} name={room.Name}: no live floor at its elevator landings");
                return false;
            }

            mesh.ClearCells();
            BuildCells(mesh, walk);
            mesh.IsGenerated = true;
            LabLogger.Info($"[SCPSLBot] NAV_ROOM_REBUILT form={form} name={room.Name} cells={walk.Nodes.Count} islands={DescribeIslands(walk)}");
            return true;
        }

        private static string DescribeIslands(Walk walk)
        {
            var parents = new int[walk.Nodes.Count];
            for (var index = 0; index < parents.Length; index++)
            {
                parents[index] = index;
            }

            foreach (var (from, to) in walk.Links)
            {
                Union(parents, from.Index, to.Index);
            }

            var islands = new Dictionary<int, (int Count, float MinY, float MaxY, Vector3 Sample)>();
            foreach (var node in walk.Nodes)
            {
                var root = Find(parents, node.Index);
                if (!islands.TryGetValue(root, out var island))
                {
                    island = (0, node.Y, node.Y, walk.Room.transform.TransformPoint(new Vector3(node.I * GridStep, node.Y, node.J * GridStep)));
                }

                islands[root] = (island.Count + 1, Mathf.Min(island.MinY, node.Y), Mathf.Max(island.MaxY, node.Y), island.Sample);
            }

            var parts = new List<string>();
            foreach (var island in islands.Values)
            {
                parts.Add($"{island.Count}@({island.Sample.x:F1},{island.Sample.y:F1},{island.Sample.z:F1})y{island.MinY:F1}..{island.MaxY:F1}");
            }

            return string.Join(" ", parts);
        }

        private static void TrySeed(Walk walk, Vector3 local)
        {
            var seedI = Mathf.RoundToInt(local.x / GridStep);
            var seedJ = Mathf.RoundToInt(local.z / GridStep);
            for (var i = seedI - SeedSearchRadius; i <= seedI + SeedSearchRadius; i++)
            {
                for (var j = seedJ - SeedSearchRadius; j <= seedJ + SeedSearchRadius; j++)
                {
                    if (TryProbeFloor(walk, i, j, local.y, out var floorY))
                    {
                        GetOrAddNode(walk, i, j, floorY);
                        return;
                    }
                }
            }
        }

        private static Node GetOrAddNode(Walk walk, int i, int j, float floorY)
        {
            if (!walk.Columns.TryGetValue((i, j), out var column))
            {
                walk.Columns[(i, j)] = column = new List<Node>();
            }

            foreach (var existing in column)
            {
                if (Mathf.Abs(existing.Y - floorY) <= SameFloorTolerance)
                {
                    return existing;
                }
            }

            var node = new Node { I = i, J = j, Y = floorY, Index = walk.Nodes.Count };
            column.Add(node);
            walk.Nodes.Add(node);
            walk.Open.Enqueue(node);
            return node;
        }

        // The floor under grid point (i, j) within one step of fromY, where a standing capsule fits
        // and the bot's own room lookup finds this room.
        private static bool TryProbeFloor(Walk walk, int i, int j, float fromY, out float floorY)
        {
            floorY = 0f;
            var transform = walk.Room.transform;
            var top = transform.TransformPoint(new Vector3(i * GridStep, fromY + ProbeStart, j * GridStep));
            if (!Physics.Raycast(top, Vector3.down, out var hit, ProbeDepth, walk.Mask, QueryTriggerInteraction.Ignore)
                || hit.normal.y < MinWalkableNormalY)
            {
                return false;
            }

            floorY = transform.InverseTransformPoint(hit.point).y;
            return Mathf.Abs(floorY - fromY) <= MaxStepHeight
                   && !IsInElevatorShaft(hit.point, walk.ElevatorDoors)
                   && !IsCapsuleBlocked(walk, hit.point)
                   && RoomUtils.TryGetRoom(hit.point + Vector3.up * NavigationAgentProfile.RootHeight, out var hitRoom)
                   && hitRoom == walk.Room;
        }

        // Floor behind an elevator door is the shaft or a docked cabin, never a route.
        private static bool IsInElevatorShaft(Vector3 point, List<ElevatorDoor> doors)
        {
            foreach (var door in doors)
            {
                if (door == null)
                {
                    continue;
                }

                var transform = door.transform;
                var offset = point - transform.position;
                var along = Vector3.Dot(offset, transform.forward);
                if (Mathf.Abs(offset.y) <= ShaftVerticalReach
                    && along < ShaftFrontMargin
                    && along > -ShaftDepth
                    && Mathf.Abs(Vector3.Dot(offset, transform.right)) <= ShaftHalfWidth)
                {
                    return true;
                }
            }

            return false;
        }

        // Both ends passed the capsule test; the midpoint closes the gap a thin wall could hide in.
        private static bool CanStep(Walk walk, Node from, int toI, int toJ, float toY)
        {
            var middle = walk.Room.transform.TransformPoint(new Vector3(
                (from.I + toI) * 0.5f * GridStep,
                (from.Y + toY) * 0.5f,
                (from.J + toJ) * 0.5f * GridStep));
            return !IsCapsuleBlocked(walk, middle);
        }

        // Door leaves move, and bots open them, so a closed one does not block the walk whatever
        // layer it is on.
        private static bool IsCapsuleBlocked(Walk walk, Vector3 floor)
        {
            var radius = NavigationAgentProfile.Radius;
            var bottomOffset = NavigationAgentProfile.StepClearance + radius;
            var topOffset = Mathf.Max(bottomOffset + 0.05f, NavigationAgentProfile.Height - radius - 0.05f);
            var count = Physics.OverlapCapsuleNonAlloc(floor + Vector3.up * bottomOffset, floor + Vector3.up * topOffset, radius, Overlaps, walk.Mask, QueryTriggerInteraction.Ignore);
            for (var index = 0; index < count; index++)
            {
                if (!walk.DoorLeaves.Contains(Overlaps[index]))
                {
                    return true;
                }
            }

            return false;
        }

        // One square per node, corners clockwise from above: (-,-), (-,+), (+,+), (+,-). Corners of
        // walkable neighbours are welded, so shared edges give the adjacency authored meshes have.
        private static void BuildCells(NavigationMesh mesh, Walk walk)
        {
            const int corners = 4;
            var parents = new int[walk.Nodes.Count * corners];
            for (var index = 0; index < parents.Length; index++)
            {
                parents[index] = index;
            }

            foreach (var (from, to) in walk.Links)
            {
                var alongI = to.I != from.I;
                Union(parents, from.Index * corners + (alongI ? 2 : 1), to.Index * corners + (alongI ? 1 : 0));
                Union(parents, from.Index * corners + (alongI ? 3 : 2), to.Index * corners + (alongI ? 0 : 3));
            }

            var heights = new Dictionary<int, (float Sum, int Count)>();
            for (var corner = 0; corner < parents.Length; corner++)
            {
                var root = Find(parents, corner);
                heights.TryGetValue(root, out var height);
                heights[root] = (height.Sum + walk.Nodes[corner / corners].Y, height.Count + 1);
            }

            var vertices = new Dictionary<int, Vertex>();
            var rootHeight = NavigationAgentProfile.RootHeight;
            foreach (var node in walk.Nodes)
            {
                var cellVertices = new Vertex[corners];
                for (var corner = 0; corner < corners; corner++)
                {
                    var root = Find(parents, node.Index * corners + corner);
                    if (!vertices.TryGetValue(root, out var vertex))
                    {
                        var (sum, count) = heights[root];
                        var x = (node.I + (corner >= 2 ? 0.5f : -0.5f)) * GridStep;
                        var z = (node.J + (corner is 1 or 2 ? 0.5f : -0.5f)) * GridStep;
                        vertex = mesh.AddVertex(new Vector3(x, sum / count + rootHeight, z));
                        vertices[root] = vertex;
                    }

                    cellVertices[corner] = vertex;
                }

                mesh.MakeCell(cellVertices);
            }
        }

        private static int Find(int[] parents, int index)
        {
            while (parents[index] != index)
            {
                parents[index] = parents[parents[index]];
                index = parents[index];
            }

            return index;
        }

        private static void Union(int[] parents, int a, int b)
        {
            parents[Find(parents, a)] = Find(parents, b);
        }
    }
}
