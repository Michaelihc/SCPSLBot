using Interactables.Interobjects;
using Interactables.Interobjects.DoorUtils;
using LabApi.Events.Arguments.ServerEvents;
using LabApi.Events.Handlers;
using MapGeneration;
using MEC;
using SCPSLBot.Navigation.Authored;
using SCPSLBot.Navigation.Mesh;
using SCPSLBot.Navigation.Policy;
using SCPSLBot.Navigation.Runtime;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;
using LabLogger = LabApi.Features.Console.Logger;

namespace SCPSLBot.Navigation
{
    internal class NavigationSystem
    {
        private const int MaxMeshFileBytes = 16 * 1024 * 1024;
        private const float MaxLoadRetrySeconds = 15f;
        private const float SealedConnectorPenaltyMeters = 80f;
        private const float PermanentPenaltySeconds = 1e8f;
        private const int RuntimeBakeFailuresBeforeFallback = 2;

        public static NavigationSystem Instance { get; } = new NavigationSystem();

        public string BaseDir { get; set; }
        public string MeshFileName { get; } = "navmesh.slnmf";

        public bool Initialized { get; private set; } = false;

        public int MapGeneration { get; private set; }
        public int ReadyGeneration { get; private set; } = -1;
        public bool IsReadyForCurrentMap => Initialized
                                            && SeedSynchronizer.MapGenerated
                                            && ReadyGeneration == MapGeneration;
        public string LastLoadError { get; private set; } = string.Empty;

        /// <summary>Room forms whose cells were generated from live collision probes this map (authored backend).</summary>
        public int GeneratedRoomForms { get; private set; }

        /// <summary>Door-less connectors probed as impassable; they keep only a heavily penalized last-resort center link (authored backend).</summary>
        public int SealedConnectors { get; private set; }

        /// <summary>Door-less connectors linked through a probed passage band (authored backend).</summary>
        public int ProbedConnectors { get; private set; }

        /// <summary>Configured backend; the active one can differ after a runtime bake fell back.</summary>
        public NavigationConfig Config { get; private set; } = new NavigationConfig();

        /// <summary>The backend serving the current map. Never null once initialized.</summary>
        public INavigationBackend Backend { get; private set; }

        /// <summary>The runtime navmesh service (present even when the authored backend is active, for diagnostics).</summary>
        public RuntimeNavMeshService Runtime { get; } = new RuntimeNavMeshService();

        /// <summary>Consecutive runtime bake failures for the current map (resets per map).</summary>
        public int RuntimeBakeFailures { get; private set; }

        /// <summary>Optional additional geometry region, retained only for this native map.</summary>
        public Bounds? CustomBounds { get; private set; }

        private readonly AuthoredNavigationBackend authoredBackend = new();
        private readonly RuntimeNavigationBackend runtimeBackend;
        private CoroutineHandle mapLoadHandle;

        public void Init(NavigationConfig config = null)
        {
            if (Initialized)
            {
                return;
            }

            Config = config ?? new NavigationConfig();
            Backend = ConfiguredBackend;
            Initialized = true;
            MapGeneration = unchecked(MapGeneration + 1);
            ReadyGeneration = -1;
            ServerEvents.MapGenerated += OnMapGenerated;
            ServerEvents.RoundRestarted += OnRoundRestarted;

            if (SeedSynchronizer.MapGenerated)
            {
                BeginMapLoad();
            }
        }

        public void Terminate()
        {
            if (!Initialized)
            {
                return;
            }

            Initialized = false;
            CustomBounds = null;
            MapGeneration = unchecked(MapGeneration + 1);
            ReadyGeneration = -1;
            if (mapLoadHandle.IsRunning)
            {
                Timing.KillCoroutines(mapLoadHandle);
            }

            ServerEvents.MapGenerated -= OnMapGenerated;
            ServerEvents.RoundRestarted -= OnRoundRestarted;

            Runtime.Clear();
            NavigationMesh.ResetMeshes();
        }

        public IBotNavigator CreateNavigator(AI.FirstPersonControl.FpcBotPlayer botPlayer)
        {
            return (Backend ?? authoredBackend).CreateNavigator(botPlayer);
        }

        /// <summary>Re-runs the load/bake for the current map (RA nav rebuild).</summary>
        public void Rebuild()
        {
            if (!Initialized)
            {
                return;
            }

            Backend = ConfiguredBackend;
            BeginMapLoad();
        }

        public void Rebuild(Bounds? customBounds)
        {
            if (!Initialized) return;
            CustomBounds = customBounds;
            Rebuild();
        }

        private INavigationBackend ConfiguredBackend => Config.Backend == NavigationBackend.Runtime ? runtimeBackend : authoredBackend;

        private void OnMapGenerated(MapGeneratedEventArgs args)
        {
            CustomBounds = null;
            Backend = ConfiguredBackend;
            BeginMapLoad();
        }

        private void OnRoundRestarted()
        {
            CustomBounds = null;
            MapGeneration = unchecked(MapGeneration + 1);
            ReadyGeneration = -1;
            if (mapLoadHandle.IsRunning)
            {
                Timing.KillCoroutines(mapLoadHandle);
            }

            Runtime.Clear();
            NavigationMesh.ResetMeshes();
        }

        private void BeginMapLoad()
        {
            MapGeneration = unchecked(MapGeneration + 1);
            ReadyGeneration = -1;
            LastLoadError = string.Empty;
            RuntimeBakeFailures = 0;
            if (mapLoadHandle.IsRunning)
            {
                Timing.KillCoroutines(mapLoadHandle);
            }

            Runtime.Clear();
            NavigationMesh.ResetMeshes();
            mapLoadHandle = Timing.RunCoroutine(LoadConnectMeshesAsync(MapGeneration));
        }

        private IEnumerator<float> LoadConnectMeshesAsync(int loadGeneration)
        {
            float retrySeconds = 1f;
            int failures = 0;
            var settledAfterGeneration = false;
            while (Initialized && loadGeneration == MapGeneration)
            {
                // Recheck ownership and native readiness after every wait. Restart/disable also
                // cancel this single worker, so an old map can never publish readiness later.
                if (!SeedSynchronizer.MapGenerated)
                {
                    yield return Timing.WaitForSeconds(0.25f);
                    continue;
                }

                // Spawned connectors register their rooms and destroy clutter that collides with
                // native blockers in their Start(); probe/bake only after those ran.
                if (!settledAfterGeneration)
                {
                    settledAfterGeneration = true;
                    yield return Timing.WaitForOneFrame;
                    yield return Timing.WaitForOneFrame;
                    if (!Initialized || loadGeneration != MapGeneration)
                    {
                        yield break;
                    }
                }

                bool loaded;
                if (ReferenceEquals(Backend, runtimeBackend))
                {
                    var bake = Timing.RunCoroutine(Runtime.BuildAsync(Config, loadGeneration, CustomBounds));
                    yield return Timing.WaitUntilDone(bake);
                    if (!Initialized || loadGeneration != MapGeneration)
                    {
                        yield break;
                    }

                    loaded = Runtime.IsBuilt;
                    if (loaded)
                    {
                        ReadyGeneration = loadGeneration;
                        LastLoadError = string.Empty;
                    }
                    else
                    {
                        RuntimeBakeFailures++;
                        LastLoadError = $"runtime bake: {Runtime.LastError}";
                        if (RuntimeBakeFailures >= RuntimeBakeFailuresBeforeFallback)
                        {
                            // The builder failed twice on this map; serve the authored mesh instead
                            // of leaving the map without navigation.
                            LabLogger.Error($"[SCPSLBot] NAV_BAKE_FALLBACK generation={loadGeneration} failures={RuntimeBakeFailures} backend=authored reason={Runtime.LastError}");
                            Runtime.Clear();
                            Backend = authoredBackend;
                        }
                    }
                }
                else
                {
                    loaded = TryLoadConnectMeshes(loadGeneration, logException: failures == 0);
                }

                if (loaded)
                {
                    if (failures > 0)
                    {
                        LabLogger.Info($"[SCPSLBot] NAV_LOAD_RECOVERED generation={loadGeneration} failures={failures} backend={Backend.Name}");
                    }
                    yield break;
                }

                failures = Math.Min(failures + 1, int.MaxValue - 1);
                LabLogger.Warn($"[SCPSLBot] NAV_LOAD_RETRY generation={loadGeneration} failures={failures} retry_seconds={retrySeconds} backend={Backend.Name}: {LastLoadError}");
                yield return Timing.WaitForSeconds(retrySeconds);
                retrySeconds = Mathf.Min(MaxLoadRetrySeconds, retrySeconds * 2f);
            }
        }

        private bool TryLoadConnectMeshes(int loadGeneration, bool logException)
        {
            try
            {
                // Installation can fail transiently too (for example, a locked file). Keep it
                // inside the same retry boundary as parsing, publication and room linking.
                EnsureDefaultMeshFile();
                LoadConnectMeshes();
                if (Initialized && loadGeneration == MapGeneration)
                {
                    ReadyGeneration = loadGeneration;
                    LastLoadError = string.Empty;
                    return true;
                }
            }
            catch (Exception exception)
            {
                ReadyGeneration = -1;
                LastLoadError = $"{exception.GetType().Name}: {exception.Message}";
                // A linking failure may have published only part of the graph. Invalidate its
                // topology before retrying, rather than exposing half-connected navigation.
                NavigationMesh.ResetMeshes();
                if (logException)
                {
                    LabLogger.Error($"[SCPSLBot] Navigation load failed for generation {loadGeneration}: {exception}");
                }
            }

            return false;
        }

        public void LoadConnectMeshes()
        {
            Debug.Log($"Loading meshes.");
            LoadMeshes(MeshFileName);
            GeneratedRoomForms = 0;
            SealedConnectors = 0;
            ProbedConnectors = 0;
            NavigationAgentProfile.Invalidate();

            Debug.Log($"Filling rooms without authored cells from live collision probes.");
            GeneratedRoomForms = RuntimeRoomMeshBuilder.FillRoomsWithoutCells();

            Debug.Log($"Connecting cells between rooms.");
            foreach (var door in DoorVariant.AllDoors)
            {
                if (door == null)
                {
                    continue;
                }

                var doorCenterPosition = door.transform.position + Vector3.up;  // assuming pivot point is located at the bottom of all doors
                if (door.Rooms != null && door.Rooms.Length == 2)
                {
                    LinkRoomCellsAtPoint(doorCenterPosition, door.Rooms[0], door.Rooms[1]);
                    continue;
                }

                // Native room registration samples one meter around the pivot and can list a single
                // room for doors that sit exactly on a room boundary (bulk doors, side-room doors).
                // Resolve both sides by position, like door-less connectors, instead of leaving the
                // rooms unlinked.
                var doorForward = Vector3.ProjectOnPlane(door.transform.forward, Vector3.up).normalized;
                if (door is not ElevatorDoor
                    && doorForward.sqrMagnitude > 0.5f
                    && TryGetConnectorSideRoom(doorCenterPosition, doorForward, out var doorRoomA)
                    && TryGetConnectorSideRoom(doorCenterPosition, -doorForward, out var doorRoomB)
                    && doorRoomA != doorRoomB)
                {
                    LinkRoomCellsAtPoint(doorCenterPosition, doorRoomA, doorRoomB);
                }
            }

            Debug.Log($"Connecting cells across door-less connectors (open hallways / clutter).");
            ConnectDoorlessConnectors();

            Debug.Log($"Connecting cells between elevator destinations.");
            var elevatorGroups = Enum.GetValues(typeof(ElevatorGroup));
            foreach (ElevatorGroup group in elevatorGroups)
            {
                var elevatorDoors = ElevatorDoor.GetDoorsForGroup(group);
                if (elevatorDoors.Count != 2)
                {
                    Debug.LogWarning($"Irregular elevator level count ({elevatorDoors.Count}) of group {group}");
                    continue;
                }

                var cellAt0InShaft = ResolveElevatorShaftCell(elevatorDoors[0]);
                var cellAt1InShaft = ResolveElevatorShaftCell(elevatorDoors[1]);

                if (cellAt0InShaft != null && cellAt1InShaft != null)
                {
                    // Connect
                    ConnectForeignCells(cellAt0InShaft.Value, cellAt1InShaft.Value);
                    ConnectForeignCells(cellAt1InShaft.Value, cellAt0InShaft.Value);
                }
            }
            Debug.Log($"Connecting cells finished.");
        }

        // Links navmesh cells across room connectors that are NOT doors (open hallways, bulk-door
        // openings, clutter passages). Doors are already handled via DoorVariant.AllDoors; elevators
        // separately. This is what lets bots traverse the native map when the connector->standard-door
        // rewrite is disabled. Each connector sits on the boundary between two rooms; we resolve the
        // room on each side from its position and link the nearest boundary cells (same scheme as
        // doors). Runs once per map load. When ForceStandardDoorConnectors is enabled every connector
        // is a door, so this finds nothing to link and is a no-op.
        private void ConnectDoorlessConnectors()
        {
            var connectors = UnityEngine.Object.FindObjectsByType<global::MapGeneration.RoomConnectors.SpawnableRoomConnector>(FindObjectsSortMode.None);
            foreach (var connector in connectors)
            {
                if (!connector)
                {
                    continue;
                }

                // Doors / elevator doors are connected through their own passes.
                if (connector.GetComponentInChildren<DoorVariant>() != null)
                {
                    continue;
                }

                var transform = connector.transform;
                var center = transform.position + Vector3.up;
                var forward = Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized;
                var right = Vector3.Cross(Vector3.up, forward).normalized;
                var connectorType = connector.SpawnData.ConnectorType;

                if (!TryGetConnectorSideRoom(center, forward, out var roomA)
                    || !TryGetConnectorSideRoom(center, -forward, out var roomB)
                    || roomA == roomB)
                {
                    continue;
                }

                // Clutter connectors leave an off-center gap and wallable connectors may be sealed;
                // the live colliders decide where (and whether) bots can cross this round.
                var passage = ConnectorPassageProbe.Probe(center, forward, right);
                if (passage.Gap is not { } gap)
                {
                    // Nothing the capsule fits through at any tier. Keep the pre-probe whole-edge
                    // link as a heavily penalized last resort: A* only routes through it when no
                    // other way exists, and the stuck ladder then jumps, penalizes and abandons
                    // instead of the bot having no path at all.
                    SealedConnectors++;
                    LabLogger.Warn($"[SCPSLBot] NAV_CONNECTOR_SEALED type={connectorType} pos={Format(transform.position)} rooms={NavigationMesh.GetForm(roomA.gameObject)}|{NavigationMesh.GetForm(roomB.gameObject)} portalGaps={passage.GapCount} floor={passage.FloorFound} fallback=penalized-center-link");
                    LinkRoomCellsAtPoint(center, roomA, roomB, SealedConnectorPenaltyMeters);
                    continue;
                }

                // The probed gap is already the clear band for the capsule center.
                ProbedConnectors++;
                LabLogger.Info($"[SCPSLBot] NAV_CONNECTOR_PASSAGE type={connectorType} pos={Format(transform.position)} rooms={NavigationMesh.GetForm(roomA.gameObject)}|{NavigationMesh.GetForm(roomB.gameObject)} tier={passage.Tier} band={gap} portalGaps={passage.GapCount}");
                LinkRoomCellsAtPassage(center, right, gap, roomA, roomB);
            }
        }

        // Links two rooms across a probed passage band: the foreign portal edge each side crosses
        // is narrowed to the band so the funnel aims through the actual gap instead of the middle
        // of the doorway or a corner buried in clutter.
        private static void LinkRoomCellsAtPassage(Vector3 point, Vector3 right, LateralGap band, RoomIdentifier roomA, RoomIdentifier roomB)
        {
            if (roomA == null || roomB == null || roomA == roomB)
            {
                return;
            }

            if (!NavigationMesh.LocalMeshesByRoom.TryGetValue(roomA.gameObject, out var meshA)
                || !NavigationMesh.LocalMeshesByRoom.TryGetValue(roomB.gameObject, out var meshB))
            {
                return;
            }

            var edgeA = NavigationMesh.GetNearestEdge(point, roomA);
            var edgeB = NavigationMesh.GetNearestEdge(point, roomB);
            if (!edgeA.HasValue || !edgeB.HasValue)
            {
                return;
            }

            var cellA = FindCellWithEdge(meshA, edgeA.Value.Local, roomA.transform);
            var cellB = FindCellWithEdge(meshB, edgeB.Value.Local, roomB.transform);
            if (!cellA.HasValue || !cellB.HasValue)
            {
                return;
            }

            var passageEdgeA = MakePassageEdge(edgeA.Value, point, right, band, roomA.transform);
            var passageEdgeB = MakePassageEdge(edgeB.Value, point, right, band, roomB.transform);

            ConnectForeignCells(cellA.Value, cellB.Value);
            ConnectForeignCellEdge(cellA.Value, cellB.Value, passageEdgeB);

            ConnectForeignCells(cellB.Value, cellA.Value);
            ConnectForeignCellEdge(cellB.Value, cellA.Value, passageEdgeA);
        }

        private static TransformCell? FindCellWithEdge(NavigationMesh mesh, Edge edge, Transform transform)
        {
            return mesh.Cells
                .Where(lc => lc.Edges.Any(e => e == edge))
                .Select(lc => (TransformCell?)new TransformCell(lc, transform))
                .FirstOrDefault();
        }

        // Builds a detached edge spanning the passage band at the authored edge's height, oriented
        // like the authored edge so the positive-side crossing test keeps its meaning.
        private static TransformEdge MakePassageEdge(TransformEdge original, Vector3 point, Vector3 right, LateralGap band, Transform transform)
        {
            var authoredFrom = original.From.Position;
            var authoredTo = original.To.Position;
            var height = Mathf.Lerp(authoredFrom.y, authoredTo.y, 0.5f);

            var fromWorld = new Vector3(point.x, height, point.z) + right * band.Start;
            var toWorld = new Vector3(point.x, height, point.z) + right * band.End;
            if (Vector3.Dot(authoredTo - authoredFrom, right) < 0f)
            {
                (fromWorld, toWorld) = (toWorld, fromWorld);
            }

            var localEdge = new Edge(
                new Vertex(transform.InverseTransformPoint(fromWorld)),
                new Vertex(transform.InverseTransformPoint(toWorld)));
            return new TransformEdge(localEdge, transform);
        }

        private static string Format(Vector3 value) => $"({value.x:F1},{value.y:F1},{value.z:F1})";

        private static bool TryGetConnectorSideRoom(Vector3 center, Vector3 direction, out RoomIdentifier room)
        {
            for (var distance = 1.5f; distance <= 4.5f; distance += 1.5f)
            {
                if (RoomUtils.TryGetRoom(center + direction * distance, out room)
                    && room != null
                    && NavigationMesh.LocalMeshesByRoom.ContainsKey(room.gameObject))
                {
                    return true;
                }
            }

            room = null;
            return false;
        }

        // Connects the nearest boundary cells of two rooms at a shared passage point, in both
        // directions. Safe against missing meshes / no matching cell / duplicate links.
        private static void LinkRoomCellsAtPoint(Vector3 point, RoomIdentifier roomA, RoomIdentifier roomB, float penaltyMeters = 0f)
        {
            if (roomA == null || roomB == null || roomA == roomB)
            {
                return;
            }

            if (!NavigationMesh.LocalMeshesByRoom.TryGetValue(roomA.gameObject, out var meshA)
                || !NavigationMesh.LocalMeshesByRoom.TryGetValue(roomB.gameObject, out var meshB))
            {
                return;
            }

            var edgeA = NavigationMesh.GetNearestEdge(point, roomA);
            var edgeB = NavigationMesh.GetNearestEdge(point, roomB);
            if (!edgeA.HasValue || !edgeB.HasValue)
            {
                return;
            }

            var cellA = meshA.Cells
                .Where(lc => lc.Edges.Any(e => e == edgeA.Value.Local))
                .Select(lc => (TransformCell?)new TransformCell(lc, roomA.transform))
                .FirstOrDefault();
            var cellB = meshB.Cells
                .Where(lc => lc.Edges.Any(e => e == edgeB.Value.Local))
                .Select(lc => (TransformCell?)new TransformCell(lc, roomB.transform))
                .FirstOrDefault();
            if (!cellA.HasValue || !cellB.HasValue)
            {
                return;
            }

            ConnectForeignCells(cellA.Value, cellB.Value);
            ConnectForeignCellEdge(cellA.Value, cellB.Value, edgeB.Value);

            ConnectForeignCells(cellB.Value, cellA.Value);
            ConnectForeignCellEdge(cellB.Value, cellA.Value, edgeA.Value);

            if (penaltyMeters > 0f)
            {
                NavigationMesh.PenalizeLink(cellA.Value, cellB.Value, penaltyMeters, PermanentPenaltySeconds);
                NavigationMesh.PenalizeLink(cellB.Value, cellA.Value, penaltyMeters, PermanentPenaltySeconds);
            }
        }

        private static void ConnectForeignCellEdge(TransformCell from, TransformCell to, TransformEdge edge)
        {
            if (!NavigationMesh.ForeignConnectedCellEdges.TryGetValue(from, out var edges))
            {
                edges = new Dictionary<TransformCell, TransformEdge>();
                NavigationMesh.ForeignConnectedCellEdges[from] = edges;
            }

            edges[to] = edge;
            NavigationMesh.MarkTopologyChanged();
        }

        // Resolves the navmesh cell at an elevator landing WITHOUT mutating native
        // RoomIdentifier.RoomsByCoords. The previous probe-loop registered a fake coord->room
        // entry there, which threw on a second nav load (duplicate key) and left a destroyed-room
        // reference in native state across rounds. This is behavior-equivalent: if the shaft-side
        // position maps to a room, resolve normally; otherwise resolve the landing cell against the
        // door's far-side room mesh directly.
        private static TransformCell? ResolveElevatorShaftCell(ElevatorDoor door)
        {
            if (door == null)
            {
                return null;
            }

            var doorTransform = door.transform;
            var doorPosition = doorTransform.position + Vector3.up;
            var doorForward = doorTransform.forward;
            var probePosition = doorPosition - doorForward;

            if (RoomUtils.TryGetRoom(probePosition, out _))
            {
                return NavigationMesh.GetCellWithin(probePosition);
            }

            if (!RoomUtils.TryGetRoom(doorPosition + doorForward, out var fallbackRoom) || fallbackRoom == null)
            {
                return null;
            }

            return NavigationMesh.GetRoomCellWithin(probePosition, fallbackRoom);
        }

        private static void ConnectForeignCells(TransformCell from, TransformCell to)
        {
            if (!NavigationMesh.ForeignConnectedCells.TryGetValue(from, out var connected))
            {
                connected = new List<TransformCell>();
                NavigationMesh.ForeignConnectedCells[from] = connected;
            }

            if (!connected.Contains(to))
            {
                connected.Add(to);
                NavigationMesh.MarkTopologyChanged();
            }
        }

        public void LoadMeshes(string fileName)
        {
            var path = Path.Combine(BaseDir, fileName);
            var document = LoadValidatedDocument(path);

            // Parsing and validation happen before touching the published graph. Publishing uses a
            // fresh graph, and any unexpected apply fault leaves a clean empty current-map graph.
            NavigationMesh.ResetMeshes();
            NavigationMesh.InitMeshes();
            try
            {
                document.Publish();
            }
            catch
            {
                NavigationMesh.ResetMeshes();
                NavigationMesh.InitMeshes();
                throw;
            }
        }

        public void SaveMeshes(string fileName)
        {
            var path = Path.Combine(BaseDir, fileName);
            byte[] bytes;
            using (var memoryStream = new MemoryStream())
            {
                using (var binaryWriter = new BinaryWriter(memoryStream, System.Text.Encoding.UTF8, leaveOpen: true))
                {
                    NavigationMesh.WriteMeshes(binaryWriter);
                    binaryWriter.Flush();
                }

                bytes = memoryStream.ToArray();
            }

            // Prove that what we are about to publish can be read before replacing the live file.
            NavigationMeshDocument.Parse(bytes);
            WriteBytesAtomic(path, bytes, keepBackup: true);
        }

        private NavigationMeshDocument LoadValidatedDocument(string path)
        {
            Exception primaryError = null;
            if (File.Exists(path))
            {
                try
                {
                    return NavigationMeshDocument.Parse(ReadBoundedFile(path));
                }
                catch (Exception exception)
                {
                    primaryError = exception;
                    QuarantineCorruptFile(path, exception);
                }
            }

            var backupPath = path + ".bak";
            if (File.Exists(backupPath))
            {
                try
                {
                    var backupBytes = ReadBoundedFile(backupPath);
                    var backupDocument = NavigationMeshDocument.Parse(backupBytes);
                    WriteBytesAtomic(path, backupBytes, keepBackup: false);
                    Debug.LogWarning($"Recovered navigation mesh from backup {backupPath}.");
                    return backupDocument;
                }
                catch (Exception backupError)
                {
                    Debug.LogWarning($"Navigation mesh backup is unusable: {backupError.Message}");
                }
            }

            var embeddedBytes = ReadEmbeddedDefaultMesh();
            if (embeddedBytes != null)
            {
                var embeddedDocument = NavigationMeshDocument.Parse(embeddedBytes);
                WriteBytesAtomic(path, embeddedBytes, keepBackup: false);
                Debug.LogWarning($"Recovered navigation mesh from the embedded default after primary failure: {primaryError?.Message ?? "file missing"}");
                return embeddedDocument;
            }

            throw new InvalidDataException(
                $"No valid navigation mesh was available at {path}, its backup, or the embedded default.",
                primaryError);
        }

        private static byte[] ReadBoundedFile(string path)
        {
            var info = new FileInfo(path);
            if (info.Length <= 0 || info.Length > MaxMeshFileBytes)
            {
                throw new InvalidDataException($"Navigation mesh size {info.Length} is outside 1..{MaxMeshFileBytes} bytes.");
            }

            return File.ReadAllBytes(path);
        }

        private static void QuarantineCorruptFile(string path, Exception exception)
        {
            try
            {
                var quarantinePath = path + $".corrupt-{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}";
                File.Move(path, quarantinePath);
                Debug.LogWarning($"Quarantined corrupt navigation mesh to {quarantinePath}: {exception.Message}");
            }
            catch (Exception quarantineError)
            {
                Debug.LogWarning($"Failed to quarantine corrupt navigation mesh {path}: {quarantineError.Message}");
            }
        }

        private static byte[] ReadEmbeddedDefaultMesh()
        {
            var assembly = Assembly.GetExecutingAssembly();
            using var resourceStream = assembly.GetManifestResourceStream("SCPSLBot.Assets.navmesh.slnmf");
            if (resourceStream == null)
            {
                return null;
            }

            using var memoryStream = new MemoryStream();
            resourceStream.CopyTo(memoryStream);
            if (memoryStream.Length <= 0 || memoryStream.Length > MaxMeshFileBytes)
            {
                throw new InvalidDataException($"Embedded navigation mesh size {memoryStream.Length} is invalid.");
            }

            return memoryStream.ToArray();
        }

        private static void WriteBytesAtomic(string path, byte[] bytes, bool keepBackup)
        {
            var directory = Path.GetDirectoryName(path);
            Directory.CreateDirectory(directory);
            var tempPath = path + $".tmp-{Guid.NewGuid():N}";
            try
            {
                using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(flushToDisk: true);
                }

                if (File.Exists(path))
                {
                    File.Replace(tempPath, path, keepBackup ? path + ".bak" : null, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(tempPath, path);
                }
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
        }

        private void EnsureDefaultMeshFile()
        {
            if (string.IsNullOrWhiteSpace(BaseDir))
            {
                return;
            }

            var path = Path.Combine(BaseDir, MeshFileName);
            if (File.Exists(path))
            {
                return;
            }

            Directory.CreateDirectory(BaseDir);

            var embeddedBytes = ReadEmbeddedDefaultMesh();
            if (embeddedBytes == null)
            {
                Debug.LogWarning("Embedded default navigation mesh was not found.");
                return;
            }

            NavigationMeshDocument.Parse(embeddedBytes);
            WriteBytesAtomic(path, embeddedBytes, keepBackup: false);
            Debug.Log($"Installed default navigation mesh to {path}.");
        }

        #region Private constructor
        private NavigationSystem()
        {
            runtimeBackend = new RuntimeNavigationBackend(Runtime);
            Backend = authoredBackend;
        }
        #endregion
    }
}
