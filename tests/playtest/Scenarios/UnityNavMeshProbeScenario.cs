using LabLogger = LabApi.Features.Console.Logger;
using MapGeneration;
using MapGeneration.RoomConnectors;
using PlaytestHarness.Core;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using UnityEngine;
using UnityEngine.AI;

namespace SCPSLBot.PlaytestScenarios.Scenarios;

/// <summary>
/// Feasibility probe, test-only: builds a Unity (Recast) navmesh at runtime from the dedicated
/// server's live physics colliders and reports what the builder could actually see, how long it
/// took, how many polygons came out, and whether paths exist across door-less clutter connectors
/// and into rooms that have no authored cells. Touches no SCPSLBot code; removes its navmesh data.
/// </summary>
public sealed class UnityNavMeshProbeScenario : Scenario
{
    public override string Name => "scpslbot-unity-navmesh-probe";
    public override string[] Suites => ["scpslbot-navigation-research"];
    public override string Description => "RESEARCH: runtime Unity navmesh build from server colliders; logs sources, build time, polygons and path checks.";
    public override FidelityRange Supported => FidelityRange.Only(Fidelity.Standard);
    public override bool IncludeInRunAll => false;
    public override float TimeoutSeconds => 300f;

    private static readonly int ExcludedLayers = LayerMask.GetMask("Player", "Hitbox", "Door", "InteractableNoPlayerCollision", "Ragdoll", "Grenade", "Viewmodel", "UI", "TransparentFX", "Ignore Raycast");

    public override IEnumerator<float> Run(ScenarioContext ctx)
    {
        NavMeshDataInstance instance = default;
        try
        {
            // Agent profile matching the human capsule (radius 0.36, height 1.8, step 0.22).
            NavMeshBuildSettings settings = NavMesh.GetSettingsByID(0);
            settings.agentRadius = 0.36f;
            settings.agentHeight = 1.8f;
            settings.agentClimb = 0.3f;
            settings.agentSlope = 45f;
            settings.voxelSize = 0.09f;
            settings.overrideVoxelSize = true;
            settings.tileSize = 128;
            settings.overrideTileSize = true;
            settings.minRegionArea = 1f;

            Bounds facility = new(Vector3.zero, Vector3.zero);
            bool first = true;
            foreach (RoomIdentifier room in RoomIdentifier.AllRoomIdentifiers)
            {
                if (room == null || room.Name == RoomName.Pocket)
                {
                    continue;
                }

                Bounds b = new(room.transform.position, new Vector3(40f, 30f, 40f));
                if (first)
                {
                    facility = b;
                    first = false;
                }
                else
                {
                    facility.Encapsulate(b);
                }
            }

            int mask = ~ExcludedLayers;
            List<NavMeshBuildSource> sources = new();
            Stopwatch collect = Stopwatch.StartNew();
            NavMeshBuilder.CollectSources(facility, mask, NavMeshCollectGeometry.PhysicsColliders, 0, new List<NavMeshBuildMarkup>(), sources);
            collect.Stop();

            Dictionary<NavMeshBuildSourceShape, int> byShape = new();
            int unreadable = 0;
            int meshTriangles = 0;
            foreach (NavMeshBuildSource source in sources)
            {
                byShape[source.shape] = byShape.TryGetValue(source.shape, out int count) ? count + 1 : 1;
                if (source.shape == NavMeshBuildSourceShape.Mesh && source.sourceObject is Mesh mesh)
                {
                    if (!mesh.isReadable)
                    {
                        unreadable++;
                    }
                    else
                    {
                        meshTriangles += mesh.triangles.Length / 3;
                    }
                }
            }

            ctx.Info($"[NavProbe] SOURCES total={sources.Count} shapes={string.Join(",", byShape.Select(p => $"{p.Key}:{p.Value}"))} unreadableMeshes={unreadable} readableTriangles={meshTriangles} collectMs={collect.ElapsedMilliseconds} bounds={facility.size}");
            LabLogger.Info($"[NavProbe] SOURCES total={sources.Count} shapes={string.Join(",", byShape.Select(p => $"{p.Key}:{p.Value}"))} unreadableMeshes={unreadable} readableTriangles={meshTriangles} collectMs={collect.ElapsedMilliseconds} bounds={facility.size}");
            ctx.Require(sources.Count > 0, "runtime navmesh builder collected at least one physics collider source");

            // Which mesh colliders are unreadable, and are render meshes any better on the server?
            Dictionary<string, int> unreadableNames = new();
            Dictionary<string, (int readable, int unreadable)> perRoom = new();
            foreach (NavMeshBuildSource source in sources)
            {
                if (source.shape != NavMeshBuildSourceShape.Mesh || source.sourceObject is not Mesh mesh || source.component == null)
                {
                    continue;
                }

                RoomIdentifier? owner = source.component.GetComponentInParent<RoomIdentifier>();
                string roomName = owner != null ? owner.gameObject.name : "(no room)";
                (int readable, int unreadable) counts = perRoom.TryGetValue(roomName, out (int readable, int unreadable) existing) ? existing : (0, 0);
                if (mesh.isReadable)
                {
                    counts.readable++;
                }
                else
                {
                    counts.unreadable++;
                    unreadableNames[mesh.name] = unreadableNames.TryGetValue(mesh.name, out int n) ? n + 1 : 1;
                }

                perRoom[roomName] = counts;
            }

            LabLogger.Info($"[NavProbe] UNREADABLE top=[{string.Join(",", unreadableNames.OrderByDescending(p => p.Value).Take(25).Select(p => $"{p.Key}:{p.Value}"))}]");
            LabLogger.Info($"[NavProbe] PERROOM sample=[{string.Join(",", perRoom.OrderBy(p => p.Value.readable).Take(30).Select(p => $"{p.Key}:{p.Value.readable}r/{p.Value.unreadable}u"))}]");

            List<NavMeshBuildSource> renderSources = new();
            NavMeshBuilder.CollectSources(facility, mask, NavMeshCollectGeometry.RenderMeshes, 0, new List<NavMeshBuildMarkup>(), renderSources);
            int renderReadable = 0;
            int renderUnreadable = 0;
            int renderTriangles = 0;
            foreach (NavMeshBuildSource source in renderSources)
            {
                if (source.shape == NavMeshBuildSourceShape.Mesh && source.sourceObject is Mesh mesh)
                {
                    if (mesh.isReadable)
                    {
                        renderReadable++;
                        renderTriangles += mesh.triangles.Length / 3;
                    }
                    else
                    {
                        renderUnreadable++;
                    }
                }
            }

            LabLogger.Info($"[NavProbe] RENDERSOURCES total={renderSources.Count} readableMeshes={renderReadable} unreadableMeshes={renderUnreadable} readableTriangles={renderTriangles}");
            ctx.Info($"[NavProbe] RENDERSOURCES total={renderSources.Count} readableMeshes={renderReadable} unreadableMeshes={renderUnreadable} readableTriangles={renderTriangles}");

            Stopwatch build = Stopwatch.StartNew();
            NavMeshData data = NavMeshBuilder.BuildNavMeshData(settings, sources, facility, Vector3.zero, Quaternion.identity);
            build.Stop();
            ctx.Require(data != null, "BuildNavMeshData produced navmesh data");
            instance = NavMesh.AddNavMeshData(data!);

            NavMeshTriangulation triangulation = NavMesh.CalculateTriangulation();
            int polygons = triangulation.indices.Length / 3;
            ctx.Info($"[NavProbe] BUILD buildMs={build.ElapsedMilliseconds} vertices={triangulation.vertices.Length} triangles={polygons}");
            LabLogger.Info($"[NavProbe] BUILD buildMs={build.ElapsedMilliseconds} vertices={triangulation.vertices.Length} triangles={polygons}");
            ctx.Require(polygons > 0, "runtime navmesh has walkable polygons");

            // Coverage: sample every room center at floor level.
            int rooms = 0;
            int covered = 0;
            List<string> uncovered = new();
            foreach (RoomIdentifier room in RoomIdentifier.AllRoomIdentifiers)
            {
                if (room == null || room.Name == RoomName.Pocket)
                {
                    continue;
                }

                rooms++;
                Vector3 probe = room.transform.position + Vector3.up * 0.5f;
                if (NavMesh.SamplePosition(probe, out _, 3f, NavMesh.AllAreas))
                {
                    covered++;
                }
                else
                {
                    uncovered.Add(room.gameObject.name);
                }
            }

            ctx.Info($"[NavProbe] COVERAGE rooms={rooms} covered={covered} uncovered=[{string.Join(",", uncovered.Take(12))}]");
            LabLogger.Info($"[NavProbe] COVERAGE rooms={rooms} covered={covered} uncovered=[{string.Join(",", uncovered.Take(12))}]");

            // Why is a room uncovered? Log what the physics floor under its centre is made of and
            // whether any navmesh polygon exists anywhere inside its bounds.
            HashSet<string> detailedForms = new();
            foreach (RoomIdentifier room in RoomIdentifier.AllRoomIdentifiers)
            {
                if (room == null || room.Name == RoomName.Pocket || !detailedForms.Add(room.gameObject.name))
                {
                    continue;
                }

                Vector3 centre = room.transform.position + Vector3.up * 0.5f;
                bool centreCovered = NavMesh.SamplePosition(centre, out _, 3f, NavMesh.AllAreas);
                bool anyWithin10 = NavMesh.SamplePosition(centre, out NavMeshHit farHit, 10f, NavMesh.AllAreas);
                string floor = "none";
                if (Physics.Raycast(centre + Vector3.up * 2f, Vector3.down, out RaycastHit hit, 12f, ~ExcludedLayers, QueryTriggerInteraction.Ignore))
                {
                    string meshInfo = "n/a";
                    if (hit.collider is MeshCollider meshCollider && meshCollider.sharedMesh != null)
                    {
                        Mesh m = meshCollider.sharedMesh;
                        meshInfo = $"mesh={m.name} readable={m.isReadable} verts={m.vertexCount} tris={(m.isReadable ? m.triangles.Length / 3 : -1)} convex={meshCollider.convex}";
                    }

                    floor = $"{hit.collider.GetType().Name}:{hit.collider.name} layer={LayerMask.LayerToName(hit.collider.gameObject.layer)} normalY={hit.normal.y:F2} scale={hit.collider.transform.lossyScale} dist={hit.distance:F2} {meshInfo}";
                }

                int sourcesInRoom = 0;
                int meshSourcesInRoom = 0;
                foreach (NavMeshBuildSource source in sources)
                {
                    if (source.component != null && source.component.GetComponentInParent<RoomIdentifier>() == room)
                    {
                        sourcesInRoom++;
                        if (source.shape == NavMeshBuildSourceShape.Mesh)
                        {
                            meshSourcesInRoom++;
                        }
                    }
                }

                LabLogger.Info($"[NavProbe] ROOMDETAIL form={room.gameObject.name} name={room.Name} zone={room.Zone} pos={Format(room.transform.position)} covered={centreCovered} within10m={anyWithin10}{(anyWithin10 ? "@" + Format(farHit.position) : "")} sources={sourcesInRoom} meshSources={meshSourcesInRoom} floor=[{floor}]");
            }

            // Paths across every door-less connector (clutter / open hallway), both directions.
            int connectorCases = 0;
            int connectorPaths = 0;
            List<string> connectorFailures = new();
            foreach (SpawnableRoomConnector connector in UnityEngine.Object.FindObjectsByType<SpawnableRoomConnector>(FindObjectsSortMode.None))
            {
                if (connector == null || connector.GetComponentInChildren<Interactables.Interobjects.DoorUtils.DoorVariant>() != null)
                {
                    continue;
                }

                Vector3 center = connector.transform.position + Vector3.up * 0.5f;
                Vector3 forward = Vector3.ProjectOnPlane(connector.transform.forward, Vector3.up).normalized;
                Vector3 a = center + forward * 3.5f;
                Vector3 b = center - forward * 3.5f;
                connectorCases++;
                if (TryPath(a, b, out float length, out int corners))
                {
                    connectorPaths++;
                    LabLogger.Info($"[NavProbe] CONNECTOR type={connector.SpawnData.ConnectorType} pos={Format(connector.transform.position)} path=OK length={length:F1} corners={corners}");
                }
                else
                {
                    connectorFailures.Add($"{connector.SpawnData.ConnectorType}@{Format(connector.transform.position)}");
                    LabLogger.Warn($"[NavProbe] CONNECTOR type={connector.SpawnData.ConnectorType} pos={Format(connector.transform.position)} path=NONE");
                }
            }

            ctx.Info($"[NavProbe] CONNECTORS cases={connectorCases} paths={connectorPaths} failures=[{string.Join(",", connectorFailures)}]");
            LabLogger.Info($"[NavProbe] CONNECTORS cases={connectorCases} paths={connectorPaths} failures=[{string.Join(",", connectorFailures)}]");

            // Long path: LCZ Class-D spawn to the HCZ warhead room through checkpoints/doors (doors excluded from geometry).
            RoomIdentifier? classD = RoomIdentifier.AllRoomIdentifiers.FirstOrDefault(r => r != null && r.Name == RoomName.LczClassDSpawn);
            RoomIdentifier? nuke = RoomIdentifier.AllRoomIdentifiers.FirstOrDefault(r => r != null && r.Name == RoomName.HczWarhead);
            RoomIdentifier? wayside = RoomIdentifier.AllRoomIdentifiers.FirstOrDefault(r => r != null && r.Name == RoomName.HczWaysideIncinerator);
            if (classD != null && nuke != null)
            {
                bool ok = TryPath(classD.transform.position + Vector3.up * 0.5f, nuke.transform.position + Vector3.up * 0.5f, out float length, out int corners);
                LabLogger.Info($"[NavProbe] LONGPATH from=LczClassDSpawn to=HczWarhead path={(ok ? "OK" : "NONE")} length={length:F1} corners={corners}");
                ctx.Info($"[NavProbe] LONGPATH from=LczClassDSpawn to=HczWarhead path={(ok ? "OK" : "NONE")} length={length:F1} corners={corners}");
            }

            if (wayside != null)
            {
                RoomIdentifier? neighbour = wayside.ConnectedRooms.FirstOrDefault();
                if (neighbour != null)
                {
                    bool ok = TryPath(neighbour.transform.position + Vector3.up * 0.5f, wayside.transform.position + Vector3.up * 0.5f, out float length, out int corners);
                    LabLogger.Info($"[NavProbe] WAYSIDE from={neighbour.gameObject.name} path={(ok ? "OK" : "NONE")} length={length:F1} corners={corners}");
                    ctx.Info($"[NavProbe] WAYSIDE from={neighbour.gameObject.name} path={(ok ? "OK" : "NONE")} length={length:F1} corners={corners}");
                }
            }

            // Query cost: 200 random room-to-room paths.
            Stopwatch query = Stopwatch.StartNew();
            RoomIdentifier[] all = RoomIdentifier.AllRoomIdentifiers.Where(r => r != null && r.Name != RoomName.Pocket).ToArray();
            System.Random random = new(1234);
            int found = 0;
            for (int i = 0; i < 200; i++)
            {
                RoomIdentifier from = all[random.Next(all.Length)];
                RoomIdentifier to = all[random.Next(all.Length)];
                if (TryPath(from.transform.position + Vector3.up * 0.5f, to.transform.position + Vector3.up * 0.5f, out _, out _))
                {
                    found++;
                }
            }

            query.Stop();
            LabLogger.Info($"[NavProbe] QUERIES count=200 found={found} totalMs={query.ElapsedMilliseconds} avgMs={query.ElapsedMilliseconds / 200f:F2}");
            ctx.Info($"[NavProbe] QUERIES count=200 found={found} totalMs={query.ElapsedMilliseconds} avgMs={query.ElapsedMilliseconds / 200f:F2}");
            yield return ctx.Wait(0.5f);
        }
        finally
        {
            if (instance.valid)
            {
                NavMesh.RemoveNavMeshData(instance);
            }
        }
    }

    private static bool TryPath(Vector3 from, Vector3 to, out float length, out int corners)
    {
        length = 0f;
        corners = 0;
        if (!NavMesh.SamplePosition(from, out NavMeshHit fromHit, 4f, NavMesh.AllAreas)
            || !NavMesh.SamplePosition(to, out NavMeshHit toHit, 4f, NavMesh.AllAreas))
        {
            return false;
        }

        NavMeshPath path = new();
        if (!NavMesh.CalculatePath(fromHit.position, toHit.position, NavMesh.AllAreas, path) || path.status != NavMeshPathStatus.PathComplete)
        {
            return false;
        }

        corners = path.corners.Length;
        for (int i = 1; i < path.corners.Length; i++)
        {
            length += Vector3.Distance(path.corners[i - 1], path.corners[i]);
        }

        return true;
    }

    private static string Format(Vector3 v) => $"({v.x:F1},{v.y:F1},{v.z:F1})";
}
