using MapGeneration;
using PlayerRoles.FirstPersonControl;
using System.Collections.Generic;
using UnityEngine;

namespace SCPSLBot.Navigation.Runtime
{
    /// <summary>
    /// Last-resort walkable surface for a room whose collider meshes the navmesh builder cannot
    /// read: the live floor is probed on a fine grid with the human capsule (every level, ramps
    /// included), the walkable set is dilated by the agent radius, and the result becomes one
    /// generated mesh the builder erodes back by that same radius. Never persisted.
    /// </summary>
    internal static class FloorProbeSourceBuilder
    {
        private const float GridStep = 0.25f;
        private const float MaxLevelDelta = 0.35f;
        private const float MinWalkableNormalY = 0.7f;
        private const float MinHeadroom = 1.75f;
        private const float BoundsPadding = 0.75f;
        private const int MaxColumns = 160;

        private static int ProbeMask => FpcStateProcessor.Mask & ~LayerMask.GetMask("Player", "Hitbox", "Door");

        private static readonly RaycastHit[] hitBuffer = new RaycastHit[16];

        /// <summary>Builds the probed floor mesh for <paramref name="room"/>; null when nothing walkable was found.</summary>
        public static UnityEngine.Mesh Build(RoomIdentifier room, out int cellCount)
        {
            cellCount = 0;
            var bounds = room.WorldspaceBounds;
            if (bounds.size.sqrMagnitude < 1f)
            {
                bounds = new Bounds(room.transform.position, new Vector3(15f, 6f, 15f));
            }

            bounds.Expand(BoundsPadding * 2f);
            var columns = Mathf.Clamp(Mathf.CeilToInt(bounds.size.x / GridStep), 1, MaxColumns);
            var rows = Mathf.Clamp(Mathf.CeilToInt(bounds.size.z / GridStep), 1, MaxColumns);
            var stepX = bounds.size.x / columns;
            var stepZ = bounds.size.z / rows;
            var top = bounds.max.y + 0.5f;
            var depth = bounds.size.y + 1f;
            var mask = ProbeMask;
            var radius = NavigationAgentProfile.Radius;
            var bottomOffset = NavigationAgentProfile.StepClearance + radius;
            var topOffset = Mathf.Max(bottomOffset + 0.05f, NavigationAgentProfile.Height - radius - 0.05f);

            // Every floor level under each column, then the capsule test at each.
            var levels = new List<float>[columns, rows];
            for (var i = 0; i < columns; i++)
            {
                for (var j = 0; j < rows; j++)
                {
                    var x = bounds.min.x + (i + 0.5f) * stepX;
                    var z = bounds.min.z + (j + 0.5f) * stepZ;
                    var origin = new Vector3(x, top, z);
                    var hits = Physics.RaycastNonAlloc(origin, Vector3.down, hitBuffer, depth, mask, QueryTriggerInteraction.Ignore);
                    List<float> walkable = null;
                    for (var h = 0; h < hits; h++)
                    {
                        var hit = hitBuffer[h];
                        if (hit.normal.y < MinWalkableNormalY)
                        {
                            continue;
                        }

                        if (!RoomUtils.TryGetRoom(hit.point + Vector3.up * 0.5f, out var hitRoom) || hitRoom != room)
                        {
                            continue;
                        }

                        var foot = hit.point;
                        if (Physics.CheckCapsule(foot + Vector3.up * bottomOffset, foot + Vector3.up * topOffset, radius, mask, QueryTriggerInteraction.Ignore))
                        {
                            continue;
                        }

                        if (Physics.Raycast(foot + Vector3.up * 0.1f, Vector3.up, MinHeadroom, mask, QueryTriggerInteraction.Ignore))
                        {
                            continue;
                        }

                        walkable ??= new List<float>(2);
                        var duplicate = false;
                        foreach (var level in walkable)
                        {
                            if (Mathf.Abs(level - foot.y) < MaxLevelDelta)
                            {
                                duplicate = true;
                                break;
                            }
                        }

                        if (!duplicate)
                        {
                            walkable.Add(foot.y);
                        }
                    }

                    levels[i, j] = walkable;
                }
            }

            // Dilate by the agent radius (in cells) so the builder's erosion lands back on the probe.
            var dilation = Mathf.CeilToInt(radius / Mathf.Min(stepX, stepZ));
            var dilated = new List<float>[columns, rows];
            for (var i = 0; i < columns; i++)
            {
                for (var j = 0; j < rows; j++)
                {
                    List<float> result = null;
                    for (var di = -dilation; di <= dilation; di++)
                    {
                        for (var dj = -dilation; dj <= dilation; dj++)
                        {
                            var ni = i + di;
                            var nj = j + dj;
                            if (ni < 0 || nj < 0 || ni >= columns || nj >= rows || levels[ni, nj] == null)
                            {
                                continue;
                            }

                            if (di * di * stepX * stepX + dj * dj * stepZ * stepZ > radius * radius + 1e-3f)
                            {
                                continue;
                            }

                            foreach (var level in levels[ni, nj])
                            {
                                result ??= new List<float>(2);
                                var duplicate = false;
                                foreach (var existing in result)
                                {
                                    if (Mathf.Abs(existing - level) < MaxLevelDelta)
                                    {
                                        duplicate = true;
                                        break;
                                    }
                                }

                                if (!duplicate)
                                {
                                    result.Add(level);
                                }
                            }
                        }
                    }

                    dilated[i, j] = result;
                }
            }

            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            for (var i = 0; i < columns; i++)
            {
                for (var j = 0; j < rows; j++)
                {
                    if (dilated[i, j] == null)
                    {
                        continue;
                    }

                    var x0 = bounds.min.x + i * stepX;
                    var z0 = bounds.min.z + j * stepZ;
                    foreach (var level in dilated[i, j])
                    {
                        // Corner heights follow neighbouring levels so ramps stay smooth.
                        var y00 = CornerHeight(dilated, i, j, level, -1, -1, columns, rows);
                        var y01 = CornerHeight(dilated, i, j, level, -1, 1, columns, rows);
                        var y11 = CornerHeight(dilated, i, j, level, 1, 1, columns, rows);
                        var y10 = CornerHeight(dilated, i, j, level, 1, -1, columns, rows);
                        var baseIndex = vertices.Count;
                        vertices.Add(new Vector3(x0, y00, z0));
                        vertices.Add(new Vector3(x0, y01, z0 + stepZ));
                        vertices.Add(new Vector3(x0 + stepX, y11, z0 + stepZ));
                        vertices.Add(new Vector3(x0 + stepX, y10, z0));
                        // Counter-clockwise seen from above so the normal faces up.
                        triangles.Add(baseIndex);
                        triangles.Add(baseIndex + 1);
                        triangles.Add(baseIndex + 2);
                        triangles.Add(baseIndex);
                        triangles.Add(baseIndex + 2);
                        triangles.Add(baseIndex + 3);
                        cellCount++;
                    }
                }
            }

            if (cellCount == 0)
            {
                return null;
            }

            var mesh = new UnityEngine.Mesh
            {
                name = $"SCPSLBot.FloorProbe.{room.gameObject.name}",
                indexFormat = vertices.Count > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16,
            };
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        private static float CornerHeight(List<float>[,] levels, int i, int j, float level, int di, int dj, int columns, int rows)
        {
            var sum = level;
            var count = 1;
            Accumulate(levels, i + di, j, level, columns, rows, ref sum, ref count);
            Accumulate(levels, i, j + dj, level, columns, rows, ref sum, ref count);
            Accumulate(levels, i + di, j + dj, level, columns, rows, ref sum, ref count);
            return sum / count;
        }

        private static void Accumulate(List<float>[,] levels, int i, int j, float level, int columns, int rows, ref float sum, ref int count)
        {
            if (i < 0 || j < 0 || i >= columns || j >= rows || levels[i, j] == null)
            {
                return;
            }

            foreach (var candidate in levels[i, j])
            {
                if (Mathf.Abs(candidate - level) < MaxLevelDelta)
                {
                    sum += candidate;
                    count++;
                    return;
                }
            }
        }
    }
}
