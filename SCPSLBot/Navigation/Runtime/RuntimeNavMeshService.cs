using AdminToys;
using Interactables.Interobjects;
using Interactables.Interobjects.DoorUtils;
using MapGeneration;
using MapGeneration.RoomConnectors;
using MEC;
using SCPSLBot.Navigation.Policy;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.AI;
using LabLogger = LabApi.Features.Console.Logger;

namespace SCPSLBot.Navigation.Runtime
{
    /// <summary>
    /// Owns the navmesh baked on the dedicated server for the current map: initial asynchronous
    /// bake from the live collision geometry, elevator links, keycard door areas, periodic and
    /// event-driven reconciliation (only tiles whose sources changed are rebuilt, off the main
    /// thread), carving obstacles for blocked crossings, and the per-room index consumed by
    /// behaviors. One instance per plugin; one navmesh per map generation.
    /// </summary>
    internal sealed class RuntimeNavMeshService
    {
        private const float BakeTimeoutSeconds = 90f;
        private const float EventReconcileDelaySeconds = 0.75f;
        private const float RoomBoundsPadding = 6f;
        private const int TileSizeVoxels = 128;
        private const float AgentRadiusClearance = 0.05f;
        private const float AgentHeightTolerance = 0.25f;

        private readonly List<NavMeshBuildSource> sources = new();
        private readonly NavMeshSourceCollector collector = new();
        private readonly NavMeshLinkRegistry links = new();
        private readonly RoomNavigationIndex rooms = new();
        private readonly BlockedCrossingObstacles obstacles = new();

        private NavigationConfig config = new();
        private NavMeshData data;
        private NavMeshDataInstance instance;
        private NavMeshBuildSettings settings;
        private Bounds bounds;
        private Bounds facilityBounds;
        private Bounds? customBounds;
        private ulong sourceHash;
        private AsyncOperation pendingUpdate;
        private bool eventsSubscribed;
        private float reconcileRequestedAt = float.NegativeInfinity;
        private bool reconcileRequested;
        private CoroutineHandle reconcileHandle;
        private int owningGeneration = -1;

        public DoorAreaRegistry Areas => collector.Areas;
        public NavMeshLinkRegistry Links => links;
        public RoomNavigationIndex Rooms => rooms;
        public BlockedCrossingObstacles Obstacles => obstacles;
        public NavigationConfig Config => config;

        /// <summary>True once the navmesh for the owning map generation is published and indexed.</summary>
        public bool IsBuilt { get; private set; }

        /// <summary>Increments whenever the walkable surface changed (bake, fallback pass, reconcile).</summary>
        public int SurfaceGeneration { get; private set; }

        public int AgentTypeId => settings.agentTypeID;
        public long LastBakeMs { get; private set; }
        public long LastReconcileMs { get; private set; }
        public int ReconcileCount { get; private set; }
        public int ReconcileRebuilds { get; private set; }
        public int SourceCount { get; private set; }
        public int Triangles { get; private set; }
        public int Vertices { get; private set; }
        public int UnreadableMeshes => collector.UnusableMeshSources;
        public int FallbackFloorRooms => collector.FallbackFloorRooms;
        public int ModifierBoxes => collector.ModifierBoxes;
        public string LastError { get; private set; } = string.Empty;
        public bool IsReconciling => pendingUpdate != null && !pendingUpdate.isDone;

        /// <summary>
        /// Bakes the navmesh for <paramref name="generation"/>. Yields until the asynchronous build
        /// completed (or failed); check <see cref="IsBuilt"/> and <see cref="LastError"/> afterwards.
        /// The iterator never throws: every engine step is wrapped so the caller's retry ladder
        /// stays in charge.
        /// </summary>
        public IEnumerator<float> BuildAsync(NavigationConfig navigationConfig, int generation, Bounds? additionalBounds = null)
        {
            Clear();
            customBounds = additionalBounds;
            config = navigationConfig ?? new NavigationConfig();
            owningGeneration = generation;
            LastError = string.Empty;
            var stopwatch = Stopwatch.StartNew();

            if (!TryPrepare())
            {
                yield break;
            }

            var operation = TryStartUpdate();
            if (operation == null)
            {
                yield break;
            }

            var deadline = Time.realtimeSinceStartup + BakeTimeoutSeconds;
            while (!operation.isDone)
            {
                if (Time.realtimeSinceStartup > deadline)
                {
                    Fail("bake timed out");
                    yield break;
                }

                yield return Timing.WaitForOneFrame;
            }

            if (!TryPublish())
            {
                yield break;
            }

            // Rooms whose collider meshes the builder could not read get a probed floor and one
            // more (incremental) build pass.
            if (TryBuildFallbackFloors(out var fallbackRooms) && fallbackRooms > 0)
            {
                var fallbackOperation = TryStartUpdate();
                if (fallbackOperation == null)
                {
                    yield break;
                }

                deadline = Time.realtimeSinceStartup + BakeTimeoutSeconds;
                while (!fallbackOperation.isDone)
                {
                    if (Time.realtimeSinceStartup > deadline)
                    {
                        Fail("fallback bake timed out");
                        yield break;
                    }

                    yield return Timing.WaitForOneFrame;
                }

                if (!TryIndex())
                {
                    yield break;
                }
            }

            stopwatch.Stop();
            LastBakeMs = stopwatch.ElapsedMilliseconds;
            IsBuilt = true;
            Subscribe();
            reconcileHandle = Timing.RunCoroutine(ReconcileLoop(generation));
            LabLogger.Info($"[SCPSLBot] NAV_BAKED generation={generation} bakeMs={LastBakeMs} sources={SourceCount} triangles={Triangles} vertices={Vertices} links={links.Count} passageLinks={links.PassageLinks} sealedConnectors={links.SealedConnectors} doorClasses={Areas.ClassCount} modifierBoxes={ModifierBoxes} unreadableMeshes={UnreadableMeshes} fallbackRooms={FallbackFloorRooms} rooms={rooms.NavigableRooms.Count} samples={rooms.SampleCount} islands={rooms.IslandSamples} entries={rooms.EntryCount} indexMs={rooms.LastRebuildMs} uncovered=[{string.Join(",", rooms.UncoveredRooms)}]");
        }

        public void Clear()
        {
            Unsubscribe();
            if (reconcileHandle.IsRunning)
            {
                Timing.KillCoroutines(reconcileHandle);
            }

            pendingUpdate = null;
            IsBuilt = false;
            owningGeneration = -1;
            obstacles.Clear();
            links.Clear();
            rooms.Clear();
            if (instance.valid)
            {
                instance.Remove();
            }

            instance = default;
            if (data != null)
            {
                UnityEngine.Object.Destroy(data);
                data = null;
            }

            collector.Clear();
            sources.Clear();
            sourceHash = 0;
            SourceCount = 0;
            Triangles = 0;
            Vertices = 0;
            reconcileRequested = false;
            customBounds = null;
        }

        /// <summary>Asks for a reconciliation soon (geometry event); coalesced with a short delay.</summary>
        public void RequestReconcile(string reason)
        {
            if (!IsBuilt)
            {
                return;
            }

            if (!reconcileRequested)
            {
                reconcileRequestedAt = Time.time;
            }

            reconcileRequested = true;
        }

        /// <summary>Carves the navmesh at <paramref name="position"/> for the configured time and bumps the surface generation.</summary>
        public void BlockCrossing(Vector3 position)
        {
            if (!IsBuilt)
            {
                return;
            }

            obstacles.Add(position, config.BlockedCrossingSeconds);
            SurfaceGeneration = unchecked(SurfaceGeneration + 1);
        }

        public string Describe()
        {
            return $"backend=runtime built={IsBuilt} generation={owningGeneration} surface_generation={SurfaceGeneration} bake_ms={LastBakeMs} sources={SourceCount} triangles={Triangles} vertices={Vertices} "
                   + $"links={links.Count} passage_links={links.PassageLinks} sealed_connectors={links.SealedConnectors} door_classes={Areas.ClassCount} modifier_boxes={ModifierBoxes} unreadable_meshes={UnreadableMeshes} fallback_rooms={FallbackFloorRooms} "
                   + $"rooms={rooms.NavigableRooms.Count} samples={rooms.SampleCount} islands={rooms.IslandSamples} entries={rooms.EntryCount} index_ms={rooms.LastRebuildMs} uncovered_rooms={rooms.UncoveredRooms.Count} uncovered=[{string.Join(",", rooms.UncoveredRooms)}] "
                   + $"reconciles={ReconcileCount} reconcile_rebuilds={ReconcileRebuilds} last_reconcile_ms={LastReconcileMs} reconciling={IsReconciling} obstacles={obstacles.Count} "
                   + $"custom_region={(customBounds.HasValue ? customBounds.Value.ToString() : "none")} custom_sources={collector.CustomSources} error={(string.IsNullOrEmpty(LastError) ? "none" : LastError)}";
        }

        private bool TryPrepare()
        {
            try
            {
                NavigationAgentProfile.Invalidate();
                settings = NavMesh.GetSettingsByID(0);
                // A little more than the capsule radius so string-pulled corners clear the
                // controller's skin instead of scraping walls along long straight segments.
                settings.agentRadius = NavigationAgentProfile.Radius + AgentRadiusClearance;
                settings.agentHeight = NavigationAgentProfile.Height - AgentHeightTolerance;
                settings.agentClimb = Mathf.Max(0.3f, NavigationAgentProfile.StepOffset + 0.08f);
                settings.agentSlope = 45f;
                settings.overrideVoxelSize = true;
                settings.voxelSize = config.VoxelSize;
                settings.overrideTileSize = true;
                settings.tileSize = TileSizeVoxels;
                settings.minRegionArea = 1f;
                facilityBounds = ComputeFacilityBounds();
                bounds = facilityBounds;
                if (customBounds.HasValue) bounds.Encapsulate(customBounds.Value);

                var issues = settings.ValidationReport(bounds);
                if (issues != null && issues.Length > 0)
                {
                    LabLogger.Warn($"[SCPSLBot] NAV_SETTINGS_REPORT {string.Join(" | ", issues)}");
                }

                data = new NavMeshData(settings.agentTypeID)
                {
                    name = "SCPSLBot.RuntimeNavMesh",
                    position = Vector3.zero,
                    rotation = Quaternion.identity,
                };
                return true;
            }
            catch (Exception exception)
            {
                Fail($"prepare failed: {exception.GetType().Name}: {exception.Message}");
                return false;
            }
        }

        private AsyncOperation TryStartUpdate()
        {
            try
            {
                sourceHash = CollectSources(out var collectMs);
                SourceCount = sources.Count;
                if (sources.Count == 0)
                {
                    Fail("no navmesh sources were collected");
                    return null;
                }

                pendingUpdate = NavMeshBuilder.UpdateNavMeshDataAsync(data, settings, sources, bounds);
                LabLogger.Info($"[SCPSLBot] NAV_BAKE_START generation={owningGeneration} sources={sources.Count} collectMs={collectMs} unreadableMeshes={UnreadableMeshes} modifierBoxes={ModifierBoxes} triggersDropped={collector.TriggerSources} ignoredRoots={collector.IgnoredRoots} bounds={bounds.size}");
                return pendingUpdate;
            }
            catch (Exception exception)
            {
                Fail($"build start failed: {exception.GetType().Name}: {exception.Message}");
                return null;
            }
        }

        private bool TryPublish()
        {
            try
            {
                pendingUpdate = null;
                if (!instance.valid)
                {
                    instance = NavMesh.AddNavMeshData(data);
                    if (!instance.valid)
                    {
                        Fail("AddNavMeshData returned an invalid instance");
                        return false;
                    }
                }

                return TryIndex();
            }
            catch (Exception exception)
            {
                Fail($"publish failed: {exception.GetType().Name}: {exception.Message}");
                return false;
            }
        }

        private bool TryIndex()
        {
            try
            {
                pendingUpdate = null;
                var triangulation = NavMesh.CalculateTriangulation();
                Triangles = triangulation.indices.Length / 3;
                Vertices = triangulation.vertices.Length;
                if (Triangles == 0)
                {
                    Fail("the bake produced no walkable polygons");
                    return false;
                }

                links.Rebuild(settings.agentTypeID);
                links.RebuildPassages(settings.agentTypeID);
                rooms.Rebuild(links);
                SurfaceGeneration = unchecked(SurfaceGeneration + 1);
                return true;
            }
            catch (Exception exception)
            {
                Fail($"index failed: {exception.GetType().Name}: {exception.Message}");
                return false;
            }
        }

        private bool TryBuildFallbackFloors(out int count)
        {
            count = 0;
            try
            {
                foreach (var room in collector.RoomsWithUnusableMeshes)
                {
                    if (room == null || rooms.HasNavigation(room))
                    {
                        continue;
                    }

                    var floor = FloorProbeSourceBuilder.Build(room, out var cells);
                    if (floor == null)
                    {
                        LabLogger.Warn($"[SCPSLBot] NAV_ROOM_UNFILLED form={room.gameObject.name} name={room.Name}: no probed floor");
                        continue;
                    }

                    collector.SetFallbackFloor(room, floor);
                    count++;
                    LabLogger.Info($"[SCPSLBot] NAV_ROOM_PROBED form={room.gameObject.name} name={room.Name} cells={cells}");
                }

                return true;
            }
            catch (Exception exception)
            {
                Fail($"fallback floors failed: {exception.GetType().Name}: {exception.Message}");
                return false;
            }
        }

        private IEnumerator<float> ReconcileLoop(int generation)
        {
            while (IsBuilt && owningGeneration == generation)
            {
                var interval = Mathf.Max(1f, config.ReconcileIntervalSeconds);
                var waited = 0f;
                while (waited < interval && !(reconcileRequested && Time.time - reconcileRequestedAt >= EventReconcileDelaySeconds))
                {
                    yield return Timing.WaitForSeconds(0.25f);
                    waited += 0.25f;
                    if (!IsBuilt || owningGeneration != generation)
                    {
                        yield break;
                    }
                }

                reconcileRequested = false;
                obstacles.Prune(Time.time);

                var operation = TryStartReconcile(out var changed);
                if (operation == null)
                {
                    continue;
                }

                var started = Time.realtimeSinceStartup;
                while (!operation.isDone)
                {
                    if (Time.realtimeSinceStartup - started > BakeTimeoutSeconds)
                    {
                        LabLogger.Warn($"[SCPSLBot] NAV_RECONCILE_TIMEOUT generation={generation}");
                        break;
                    }

                    yield return Timing.WaitForOneFrame;
                }

                if (!IsBuilt || owningGeneration != generation)
                {
                    yield break;
                }

                FinishReconcile(started, changed);
            }
        }

        private AsyncOperation TryStartReconcile(out bool changed)
        {
            changed = false;
            var observation = RuntimeNavigationTiming.Begin("ReconcileStart", Time.frameCount);
            try
            {
                ReconcileCount++;
                var hash = CollectSources(out var collectMs);
                LastReconcileMs = collectMs;
                if (hash == sourceHash)
                {
                    return null;
                }

                sourceHash = hash;
                SourceCount = sources.Count;
                changed = true;
                ReconcileRebuilds++;
                LabLogger.Info($"[SCPSLBot] NAV_RECONCILE_START sources={SourceCount} change={collector.LastChangeSummary}");
                pendingUpdate = NavMeshBuilder.UpdateNavMeshDataAsync(data, settings, sources, bounds);
                return pendingUpdate;
            }
            catch (Exception exception)
            {
                LabLogger.Warn($"[SCPSLBot] NAV_RECONCILE_FAILED {exception.GetType().Name}: {exception.Message}");
                return null;
            }
            finally { observation.Complete(sources.Count, changed); }
        }

        private ulong CollectSources(out long elapsedMilliseconds)
        {
            var stopwatch = Stopwatch.StartNew();
            var observation = RuntimeNavigationTiming.Begin("SourceCollection", Time.frameCount);
            ulong hash = sourceHash;
            try
            {
                hash = collector.Collect(facilityBounds, config.KeycardAreaRouting, sources, customBounds);
                return hash;
            }
            finally
            {
                stopwatch.Stop();
                elapsedMilliseconds = stopwatch.ElapsedMilliseconds;
                observation.Complete(sources.Count, hash != sourceHash);
            }
        }

        private void FinishReconcile(float started, bool changed)
        {
            pendingUpdate = null;
            if (!changed)
            {
                return;
            }

            var observation = RuntimeNavigationTiming.Begin("ReconcileFinish", Time.frameCount);
            try
            {
                links.Rebuild(settings.agentTypeID);
                links.RebuildPassages(settings.agentTypeID);
                rooms.Rebuild(links);
                SurfaceGeneration = unchecked(SurfaceGeneration + 1);
                LabLogger.Info($"[SCPSLBot] NAV_RECONCILED sources={SourceCount} elapsedMs={(Time.realtimeSinceStartup - started) * 1000f:F0} indexMs={rooms.LastRebuildMs} links={links.Count} rooms={rooms.NavigableRooms.Count} surfaceGeneration={SurfaceGeneration}");
            }
            catch (Exception exception)
            {
                LabLogger.Warn($"[SCPSLBot] NAV_RECONCILE_FAILED index {exception.GetType().Name}: {exception.Message}");
            }
            finally { observation.Complete(sources.Count, changed); }
        }

        private void Subscribe()
        {
            if (eventsSubscribed)
            {
                return;
            }

            eventsSubscribed = true;
            AdminToyBase.OnAdded += OnToyChanged;
            AdminToyBase.OnRemoved += OnToyChanged;
            SpawnableRoomConnector.OnAdded += OnConnectorChanged;
            SpawnableRoomConnector.OnRemoved += OnConnectorChanged;
            DoorVariant.OnInstanceCreated += OnDoorCreated;
            DoorVariant.OnInstanceRemoved += OnDoorRemoved;
            foreach (var door in DoorVariant.AllDoors)
            {
                HookDoor(door);
            }
        }

        private void Unsubscribe()
        {
            if (!eventsSubscribed)
            {
                return;
            }

            eventsSubscribed = false;
            AdminToyBase.OnAdded -= OnToyChanged;
            AdminToyBase.OnRemoved -= OnToyChanged;
            SpawnableRoomConnector.OnAdded -= OnConnectorChanged;
            SpawnableRoomConnector.OnRemoved -= OnConnectorChanged;
            DoorVariant.OnInstanceCreated -= OnDoorCreated;
            DoorVariant.OnInstanceRemoved -= OnDoorRemoved;
            foreach (var door in DoorVariant.AllDoors)
            {
                UnhookDoor(door);
            }
        }

        private void HookDoor(DoorVariant door)
        {
            if (door is BreakableDoor breakable)
            {
                breakable.OnDestroyedChanged -= OnDoorDestroyedChanged;
                breakable.OnDestroyedChanged += OnDoorDestroyedChanged;
            }
        }

        private void UnhookDoor(DoorVariant door)
        {
            if (door is BreakableDoor breakable)
            {
                breakable.OnDestroyedChanged -= OnDoorDestroyedChanged;
            }
        }

        private void OnToyChanged(AdminToyBase toy)
        {
            if (NavMeshSourceCollector.CanChangeNavigation(toy))
            {
                RequestReconcile("admin-toy");
            }
        }

        private void OnConnectorChanged(SpawnableRoomConnector connector) => RequestReconcile("room-connector");
        private void OnDoorDestroyedChanged() => RequestReconcile("door-destroyed");

        private void OnDoorCreated(DoorVariant door)
        {
            HookDoor(door);
            RequestReconcile("door-created");
        }

        private void OnDoorRemoved(DoorVariant door)
        {
            UnhookDoor(door);
            RequestReconcile("door-removed");
        }

        private void Fail(string message)
        {
            LastError = message;
            IsBuilt = false;
            pendingUpdate = null;
            LabLogger.Error($"[SCPSLBot] NAV_BAKE_FAILED generation={owningGeneration} reason={message}");
        }

        private static Bounds ComputeFacilityBounds()
        {
            var result = new Bounds(Vector3.zero, Vector3.zero);
            var first = true;
            foreach (var room in RoomIdentifier.AllRoomIdentifiers)
            {
                if (room == null || room.Name == RoomName.Pocket)
                {
                    continue;
                }

                var roomBounds = room.WorldspaceBounds;
                if (roomBounds.size.sqrMagnitude < 1f)
                {
                    roomBounds = new Bounds(room.transform.position, new Vector3(40f, 30f, 40f));
                }
                else
                {
                    roomBounds.Expand(RoomBoundsPadding * 2f);
                    roomBounds.Encapsulate(new Bounds(room.transform.position, new Vector3(40f, 30f, 40f)));
                }

                if (first)
                {
                    result = roomBounds;
                    first = false;
                }
                else
                {
                    result.Encapsulate(roomBounds);
                }
            }

            return result;
        }
    }
}
