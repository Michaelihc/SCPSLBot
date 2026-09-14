using Interactables.Interobjects;
using Interactables.Interobjects.DoorUtils;
using LabApi.Events.Arguments.ServerEvents;
using LabApi.Events.Handlers;
using MapGeneration;
using MEC;
using SCPSLBot.Navigation.Mesh;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace SCPSLBot.Navigation
{
    internal class NavigationSystem
    {
        private const int MaxMeshFileBytes = 16 * 1024 * 1024;

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

        private CoroutineHandle mapLoadHandle;

        public void Init()
        {
            if (Initialized)
            {
                return;
            }

            Initialized = true;
            MapGeneration = unchecked(MapGeneration + 1);
            ReadyGeneration = -1;
            EnsureDefaultMeshFile();

            ServerEvents.MapGenerated += OnMapGenerated;
            ServerEvents.RoundRestarted += OnRoundRestarted;

            if (SeedSynchronizer.MapGenerated)
            {
                TryLoadConnectMeshes(MapGeneration);
            }
        }

        public void Terminate()
        {
            if (!Initialized)
            {
                return;
            }

            Initialized = false;
            MapGeneration = unchecked(MapGeneration + 1);
            ReadyGeneration = -1;
            if (mapLoadHandle.IsRunning)
            {
                Timing.KillCoroutines(mapLoadHandle);
            }

            ServerEvents.MapGenerated -= OnMapGenerated;
            ServerEvents.RoundRestarted -= OnRoundRestarted;

            NavigationMesh.ResetMeshes();
        }

        private void OnMapGenerated(MapGeneratedEventArgs args)
        {
            BeginMapLoad();
        }

        private void OnRoundRestarted()
        {
            MapGeneration = unchecked(MapGeneration + 1);
            ReadyGeneration = -1;
            if (mapLoadHandle.IsRunning)
            {
                Timing.KillCoroutines(mapLoadHandle);
            }

            NavigationMesh.ResetMeshes();
        }

        private void BeginMapLoad()
        {
            MapGeneration = unchecked(MapGeneration + 1);
            ReadyGeneration = -1;
            LastLoadError = string.Empty;
            if (mapLoadHandle.IsRunning)
            {
                Timing.KillCoroutines(mapLoadHandle);
            }

            mapLoadHandle = Timing.RunCoroutine(LoadConnectMeshesAsync(MapGeneration));
        }

        private IEnumerator<float> LoadConnectMeshesAsync(int loadGeneration)
        {
            yield return Timing.WaitUntilTrue(() => SeedSynchronizer.MapGenerated);

            if (!Initialized || loadGeneration != MapGeneration)
            {
                yield break;
            }

            TryLoadConnectMeshes(loadGeneration);
        }

        private void TryLoadConnectMeshes(int loadGeneration)
        {
            try
            {
                LoadConnectMeshes();
                if (Initialized && loadGeneration == MapGeneration)
                {
                    ReadyGeneration = loadGeneration;
                    LastLoadError = string.Empty;
                }
            }
            catch (Exception exception)
            {
                ReadyGeneration = -1;
                LastLoadError = $"{exception.GetType().Name}: {exception.Message}";
                Debug.LogError($"Navigation load failed for generation {loadGeneration}: {LastLoadError}");
                Debug.LogException(exception);
            }
        }

        public void LoadConnectMeshes()
        {
            Debug.Log($"Loading meshes.");
            LoadMeshes(MeshFileName);

            Debug.Log($"Connecting cells between rooms.");
            foreach (var door in DoorVariant.AllDoors)
            {
                if (door.Rooms.Length == 2)
                {
                    var doorCenterPosition = door.transform.position + Vector3.up;  // assuming pivot point is located at the bottom of all doors
                    LinkRoomCellsAtPoint(doorCenterPosition, door.Rooms[0], door.Rooms[1]);
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
        private static void ConnectDoorlessConnectors()
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
                var forward = transform.forward;

                if (!TryGetConnectorSideRoom(center, forward, out var roomA)
                    || !TryGetConnectorSideRoom(center, -forward, out var roomB)
                    || roomA == roomB)
                {
                    continue;
                }

                LinkRoomCellsAtPoint(center, roomA, roomB);
            }
        }

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
        private static void LinkRoomCellsAtPoint(Vector3 point, RoomIdentifier roomA, RoomIdentifier roomB)
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
        { }
        #endregion
    }
}
