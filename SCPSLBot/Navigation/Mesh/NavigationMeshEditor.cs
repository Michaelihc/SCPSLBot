using Interactables.Interobjects;
using Interactables.Interobjects.DoorUtils;
using MapGeneration;
using MEC;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace SCPSLBot.Navigation.Mesh
{
    internal class NavigationMeshEditor
    {
        public static NavigationMeshEditor Instance { get; } = new();

        public LabApi.Features.Wrappers.Player PlayerEditing { get; set; }

        private NavigationMeshVisuals Visuals { get; } = new();

        private LabApi.Features.Wrappers.Player LastPlayerEditing { get; set; }

        private List<TransformVertex> SelectedVertices { get; } = new();
        private bool AutoSelectModeEnabled = false;

        private CoroutineHandle[] handles;

        public void Init()
        {
            Visuals.SelectedVertices = SelectedVertices;
            Visuals.Init();

            handles = new [] {
                Timing.RunCoroutine(RunEachFrame(UpdateEditing)),
                Timing.RunCoroutine(RunEachFrame(UpdateMeshEventLogging)),

                Timing.RunCoroutine(RunEachFrame(UpdateNearestVertex)),
                Timing.RunCoroutine(RunEachFrame(UpdateFacingVertex)),
                Timing.RunCoroutine(RunEachFrame(UpdateVertexAutoSelect)),

                Timing.RunCoroutine(RunEachFrame(UpdateNearestCell)),
                Timing.RunCoroutine(RunEachFrame(UpdateCachedCell)),
                Timing.RunCoroutine(RunEachFrame(UpdateFacingCell)),

                Timing.RunCoroutine(RunEachFrame(Visuals.UpdateBroadcastMessage)),

                Timing.RunCoroutine(RunEachFrame(Visuals.UpdateVertexVisuals)),
                Timing.RunCoroutine(RunEachFrame(Visuals.UpdateCellVisuals)),
                Timing.RunCoroutine(RunEachFrame(Visuals.UpdateEdgeVisuals)),
                Timing.RunCoroutine(RunEachFrame(Visuals.UpdateConnectionVisuals)),
            };
        }

        public void Terminate()
        {
            Visuals.Terminate();

            Timing.KillCoroutines(handles);
            handles = null;

            Instance.PlayerEditing = null;

            UpdateEditing();
            UpdateMeshEventLogging();

            UpdateNearestVertex();
            UpdateFacingVertex();
            UpdateVertexAutoSelect();

            UpdateNearestCell();
            UpdateCachedCell();
            UpdateFacingCell();

            Visuals.UpdateBroadcastMessage();

            Visuals.UpdateVertexVisuals();
            Visuals.UpdateCellVisuals();
            Visuals.UpdateEdgeVisuals();
            Visuals.UpdateConnectionVisuals();
        }

        public Vertex FindClosestVertexFacingAt(GameObject room, Vector3 localPosition, Vector3 localDirection)
        {
            var vertices = NavigationMesh.LocalMeshesByRoom[room].Vertices;

            var targetVertex = vertices
                .Select(v => (n: v, d: Vector3.SqrMagnitude(v.Position - localPosition)))
                .Where(t => t.d < 50f && t.d > 1f)
                .OrderBy(t => t.d)
                .Select(t => t.n)
                .FirstOrDefault(a => Vector3.Dot(Vector3.Normalize(a.Position - localPosition), localDirection) > 0.999848f);

            return targetVertex;
        }

        public Cell FindClosestRoomCellByCenter(Vector3 position, float radius = 1f)
        {
            if (!RoomUtils.TryGetRoom(position, out var room))
            {
                return null;
            }

            var roomForm = NavigationMesh.GetForm(room.gameObject);
            if (!NavigationMesh.MeshesByRoomForm.TryGetValue(roomForm, out var mesh))
            {
                return null;
            }

            var radiusSqr = Mathf.Pow(radius, 2);
            var localPosition = room.transform.InverseTransformPoint(position);

            var cellsWithinRadius = mesh.Cells.Select(cell => (cell, distSqr: Vector3.SqrMagnitude(cell.CenterPosition - localPosition)))
                .Where(t => t.distSqr < radiusSqr);

            if (!cellsWithinRadius.Any())
            {
                return null;
            }

            return cellsWithinRadius
                .Aggregate((a, c) => c.distSqr < a.distSqr ? c : a)
                .cell;
        }

        public Cell FindClosestCellFacingAt(GameObject room, Vector3 localPosition, Vector3 localDirection)
        {
            var cells = NavigationMesh.LocalMeshesByRoom[room].Cells;

            var targetCell = cells
                .Select(a => (n: a, d: Vector3.SqrMagnitude(a.CenterPosition - localPosition)))
                .Where(t => t.d < 50f && t.d > 1f)
                .OrderBy(t => t.d)
                .Select(t => t.n)
                .FirstOrDefault(a => Vector3.Dot(Vector3.Normalize(a.CenterPosition - localPosition), localDirection) > 0.999848f);

            return targetCell;
        }

        public Vertex CreateVertex(Vector3 position)
        {
            if (!RoomUtils.TryGetRoom(position, out var room))
            {
                return null;
            }

            Transform transform;
            var roomForm = NavigationMesh.GetForm(room.gameObject);

            if (!NavigationMesh.MeshesByRoomForm.TryGetValue(roomForm, out NavigationMesh mesh))
            {
                mesh = NavigationMesh.CreateMesh(roomForm);

                AddLoggingHandlers(mesh, roomForm);
            }

            transform = room.transform;

            var localPosition = transform.InverseTransformPoint(position);
            if (SelectedVertices.Count == 2)
            {
                localPosition = GetProjectedPosition(localPosition);
            }

            var newFormVertex = mesh.AddVertex(localPosition);
            return newFormVertex;
        }

        public bool DeleteNearestVertex()
        {
            if (Visuals.NearestVertex == null)
            {
                Debug.LogWarning($"No vertex found nearby to remove.");

                return false;
            }

            var vertex = Visuals.NearestVertex.Value;

            SelectedVertices.Remove(vertex);

            var form = NavigationMesh.GetForm(vertex.Transform.gameObject);
            var mesh = NavigationMesh.GetMesh(form);

            foreach (var cell in mesh.Cells.ToArray())
            {
                if (cell.Vertices.Contains(vertex.Local) && cell.Vertices.Count <= 3)
                {
                    mesh.RemoveCell(cell);

                    Debug.LogWarning($"Cell at local center position {cell.CenterPosition} dissolved under {form}.");
                }
            }

            if (!mesh.DeleteVertex(vertex.Local))
            {
                Debug.LogWarning($"No vertices at {form} to remove vertex from.");
                return false;
            }

            return true;
        }

        public bool MoveVertex(Vector3 newPosition)
        {
            var foundVertex = NavigationMesh.GetVertexNearby(PlayerEditing.Position, 1f);
            if (foundVertex == null)
            {
                return false;
            }
            var vertex = foundVertex.Value;

            var form = NavigationMesh.GetForm(vertex.Transform.gameObject);
            var transform = vertex.Transform;
            var newLocalPosition = transform.InverseTransformPoint(newPosition);

            if (SelectedVertices.Count == 2)
            {
                newLocalPosition = GetProjectedPosition(newLocalPosition);
            }

            var mesh = NavigationMesh.GetMesh(form);
            if (!mesh.MoveVertex(vertex.Local, newLocalPosition))
            {
                return false;
            }

            Debug.Log($"Vertex #{mesh.Vertices.IndexOf(vertex.Local)} of {form} moved to new local position {vertex.Position}.");

            return true;
        }

        public bool AddNearestVertexToSelection()
        {
            if (Visuals.NearestVertex == null)
            {
                Debug.LogWarning($"No vertex found nearby for selection.");
                return false;
            }
            var vertex = Visuals.NearestVertex.Value;

            var form = NavigationMesh.GetForm(vertex.Transform.gameObject);
            if (SelectedVertices.Any() && SelectedVertices.First().Transform != vertex.Transform)
            {
                Debug.LogWarning($"Form of the vertex for selection is different than of first selected vertex.");
                return false;
            }

            SelectedVertices.Add(vertex);

            Debug.Log($"Vertex at local position {vertex.Local.Position} added to selection under {form}.");

            return true;
        }

        public bool RemoveNearestVertexFromSelection()
        {
            if (Visuals.NearestVertex == null)
            {
                Debug.LogWarning($"No vertex found nearby to remove from selection.");
                return false;
            }
            var vertex = Visuals.NearestVertex.Value;

            SelectedVertices.Remove(vertex);

            var form = NavigationMesh.GetForm(vertex.Transform.gameObject);

            Debug.Log($"Vertex at local position {vertex.Local.Position} removed from selection under {form}.");

            return true;
        }

        public void ClearVertexSelection()
        {
            SelectedVertices.Clear();
        }

        public void ToggleAutoSelectingVertices(bool isEnabled)
        {
            AutoSelectModeEnabled = isEnabled;
        }

        public Cell MakeCell(Vector3 position)
        {
            if (SelectedVertices.Count < 3)
            {
                Debug.LogWarning($"Not enough vertices (min 3) selected.");
                return null;
            }

            if (!RoomUtils.TryGetRoom(position, out var room))
            {
                return null;
            }

            NavigationMesh mesh;
            string form;
            form = NavigationMesh.GetForm(room.gameObject);
            mesh = NavigationMesh.MeshesByRoomForm[form];

            var newCell = mesh.MakeCell(SelectedVertices.Select(sv => sv.Local));

            Debug.Log($"Cell #{mesh.Cells.IndexOf(newCell)} at local center position {newCell.CenterPosition} added under {form}.");

            SelectedVertices.Clear();
            AutoSelectModeEnabled = false;
            LabApiPlugin.Instance?.Presentation?.ShowEditorStoppedOnCellCreation(PlayerEditing);

            return newCell;
        }

        public static GameObject GetClosestConnector(Vector3 position, out Vector3Int direction, out Vector3Int orientation, out RoomIdentifier outRoom)
        {
            RoomUtils.TryGetRoom(position, out outRoom);

            return GetClosestConnector(position, out direction, out orientation, outRoom);
        }

        public static GameObject GetClosestConnector(Vector3 position, out Vector3Int direction, out Vector3Int orientation, RoomIdentifier room = null)
        {
            if (!room)
            {
                RoomUtils.TryGetRoom(position, out room);
            }

            var nearestRoom = room.ConnectedRooms.OrderBy(connectedRoom => Vector3.SqrMagnitude(connectedRoom.transform.position - position)).First();
            direction = NavigationMesh.GetDirectionToRoom(room, nearestRoom);

            var closestConnector = RoomConnector.AllConnectors.FirstOrDefault(c => c.Rooms.Contains(nearestRoom) && c.Rooms.Contains(room))?.gameObject
                ?? DoorVariant.AllDoors.FirstOrDefault(c => c.Rooms.Contains(nearestRoom) && c.Rooms.Contains(room))?.gameObject;

            orientation = NavigationMesh.GetConnectorOrientation(room, closestConnector?.transform.forward ?? default);
            return closestConnector;
        }

        public bool DissolveCell(Vector3 position)
        {
            if (Visuals.NearestCell == null)
            {
                Debug.LogWarning($"No cell found within to remove.");
                return false;
            }
            var cell = Visuals.NearestCell.Value;

            var form = NavigationMesh.GetForm(cell.Transform.gameObject);
            var mesh = NavigationMesh.GetMesh(form);

            if (!mesh.RemoveCell(cell.Local))
            {
                Debug.LogWarning($"Cell already does not exist in collection by {form}.");
            }
            else
            {
                Debug.Log($"Cell at local center position {cell.Local.CenterPosition} removed under {form}.");
            }

            return true;
        }

        public bool CreateVertexOnClosestRoomEdge(Vector3 position)
        {
            if (!RoomUtils.TryGetRoom(PlayerEditing.Position, out var room))
            {
                return false;
            }
            var roomForm = NavigationMesh.GetForm(room.gameObject);

            var localPosition = room.transform.InverseTransformPoint(position);

            if (!NavigationMesh.MeshesByRoomForm.TryGetValue(roomForm, out var mesh))
            {
                return false;
            }

            var (newVertexPos, cell, edge) = mesh.Cells
                .SelectMany(a => a.Edges.Select(e => (edge: (from: e.From, to: e.To), cell: a)))
                .Select(t => (
                    t.edge,
                    dirTo2: (t.edge.to.Position - t.edge.from.Position),
                    dirToPoint: (localPosition - t.edge.from.Position),
                    t.cell))
                .Select(t => (t.edge, t.dirTo2, dirToProj: (Vector3.Project(t.dirToPoint, t.dirTo2)), t.cell))
                .Where(t => Vector3.Dot(t.dirToProj, t.dirTo2) > 0f && t.dirToProj.sqrMagnitude < t.dirTo2.sqrMagnitude)
                .Select(t => (projected: (t.dirToProj + t.edge.from.Position), t.cell, t.edge))

                .OrderBy(t => Vector3.SqrMagnitude(t.projected - localPosition))
                .FirstOrDefault();

            if (cell == null)
            {
                return false;
            }

            var vertex = mesh.AddVertex(newVertexPos);

            mesh.AddVertexToCell(cell, vertex, edge.to);

            Debug.Log($"Vertex #{mesh.Vertices.IndexOf(vertex)} created on edge of cell #{mesh.Cells.IndexOf(cell)}");

            return true;
        }

        public bool SliceClosestRoomCellEdge(Vector3 position, Vector3 direction)
        {
            if (!RoomUtils.TryGetRoom(PlayerEditing.Position, out var room))
            {
                return false;
            }

            var roomForm = NavigationMesh.GetForm(room.gameObject);

            var localPosition = room.transform.InverseTransformPoint(position);
            var localDirection = room.transform.InverseTransformDirection(direction);

            if (!NavigationMesh.MeshesByRoomForm.TryGetValue(roomForm, out var mesh))
            {
                return false;
            }

            var lookPlane = new Plane(Vector3.Cross(localDirection, Vector3.up), localPosition);

            var (newVertexPos, cell, edge) = mesh.Cells
                .SelectMany(a => a.Edges.Select(e => (edge: (from: e.From, to: e.To), cell: a)))
                .Select(t => (
                    t.edge,
                    dirTo2: (t.edge.to.Position - t.edge.from.Position),
                    t.cell))
                .Select(t => (
                    t.edge, 
                    t.dirTo2, 
                    rayTo2: new Ray(t.edge.from.Position, t.dirTo2), 
                    t.cell))
                .Select(t => (
                    t.edge, 
                    t.dirTo2, 
                    t.rayTo2,
                    isHit: lookPlane.Raycast(t.rayTo2, out var distToHit),
                    distToHit,
                    t.cell))
                .Where(t => t.isHit)
                .Select(t => (
                    t.edge, 
                    t.dirTo2,
                    hitPoint: t.rayTo2.GetPoint(t.distToHit),
                    t.cell))
                .Select(t => (
                    t.edge, 
                    t.dirTo2,
                    t.hitPoint,
                    dirToHit: t.hitPoint - t.edge.from.Position,
                    t.cell))
                .Where(t => Vector3.Dot(t.dirToHit, t.dirTo2) > 0f && t.dirToHit.sqrMagnitude < t.dirTo2.sqrMagnitude)

                .OrderBy(t => Vector3.SqrMagnitude(t.hitPoint - localPosition))
                .Select(t => (t.hitPoint, t.cell, t.edge))
                .FirstOrDefault();

            if (cell == null)
            {
                return false;
            }

            var vertex = mesh.AddVertex(newVertexPos);

            mesh.AddVertexToCell(cell, vertex, edge.to);

            Debug.Log($"Vertex #{mesh.Vertices.IndexOf(vertex)} created on edge of cell #{mesh.Cells.IndexOf(cell)}");

            return true;
        }

        public bool CacheNearestCell()
        {
            Visuals.CachedCell = Visuals.NearestCell;

            return Visuals.CachedCell != null;
        }

        public bool TracePath(Vector3 position)
        {
            if (Visuals.CachedCell == null)
            {
                return false;
            }

            var targetCell = Visuals.NearestCell;
            if (targetCell == null)
            {
                return false;
            }

            Visuals.Path.Clear();

            NavigationMesh.FindShortestPath(Visuals.CachedCell.Value, targetCell.Value, Visuals.Path);
            if (Visuals.Path.Count == 0)
            {
                Debug.LogWarning($"No path found.");
            }

            return true;
        }

        private Vector3 GetProjectedPosition(Vector3 localPosition)
        {
            var lineSegment = (from: SelectedVertices.First().Local, to: SelectedVertices.Last().Local);

            var dirTo2 = (lineSegment.to.Position - lineSegment.from.Position);
            var dirToPoint = (localPosition - lineSegment.from.Position);
            var dirToProj = (Vector3.Project(dirToPoint, dirTo2));
            var projected = (dirToProj + lineSegment.from.Position);

            return projected;
        }

        private void UpdateEditing()
        {
            if (PlayerEditing != null && !PlayerEditing.ReferenceHub)
            {
                PlayerEditing = null;
            }

            if (PlayerEditing != LastPlayerEditing)
            {
                LastPlayerEditing = PlayerEditing;

                Visuals.PlayerEnabledVisualsFor = PlayerEditing;

                Debug.Log($"Visuals.PlayerEnabledVisualsFor.DisplayNickname = {Visuals.PlayerEnabledVisualsFor?.DisplayName}");
            }
        }

        private void UpdateMeshEventLogging()
        {
            if (PlayerEditing == LastPlayerEditing)
            {
                return;
            }

            var meshesByForm = NavigationMesh.MeshesByRoomForm;

            if (PlayerEditing != null)
            {
                foreach (var (form, mesh) in meshesByForm)
                {
                    AddLoggingHandlers(mesh, form);
                }
            }
            else
            {
                foreach (var (_, mesh) in meshesByForm)
                {
                    RemoveLoggingHandlers(mesh);
                }
            }
        }

        private readonly Dictionary<NavigationMesh, Action<Vertex>> vertexCreatedDelegatesByMesh = new();
        private readonly Dictionary<NavigationMesh, Action<Vertex>> vertexDeletedDelegatesByMesh = new();

        private void AddLoggingHandlers(NavigationMesh mesh, string form)
        {
            vertexCreatedDelegatesByMesh.Add(mesh, vertex => LogVertexCreated(vertex, form));
            mesh.VertexCreated += vertexCreatedDelegatesByMesh[mesh];

            vertexDeletedDelegatesByMesh.Add(mesh, vertex => LogVertexDeleted(vertex, form));
            mesh.VertexDeleted += vertexDeletedDelegatesByMesh[mesh];
        }

        private void RemoveLoggingHandlers(NavigationMesh mesh)
        {
            vertexCreatedDelegatesByMesh.Remove(mesh, out var vertexCreatedDelagate);
            mesh.VertexCreated -= vertexCreatedDelagate;

            vertexDeletedDelegatesByMesh.Remove(mesh, out var vertexDeletedDelagate);
            mesh.VertexDeleted -= vertexDeletedDelagate;
        }

        private void LogVertexCreated(Vertex formVertex, string form)
        {
            Debug.Log($"Vertex #{NavigationMesh.GetMesh(form).Vertices.IndexOf(formVertex)} at local position {formVertex.Position} added under {form}.");
        }

        private void LogVertexDeleted(Vertex formVertex, string form)
        {
            Debug.Log($"Vertex at local position {formVertex.Position} deleted under {form}.");
        }

        private void UpdateNearestVertex()
        {
            if (PlayerEditing != null)
            {
                Visuals.NearestVertex = NavigationMesh.GetVertexNearby(PlayerEditing.Position, .125f);
            }
        }

        private void UpdateFacingVertex()
        {
            if (PlayerEditing == null || !PlayerEditing.Camera)
            {
                return;
            }

            if (!RoomUtils.TryGetRoom(PlayerEditing.Position, out var room))
            {
                return;
            }

            var cameraPosition = PlayerEditing.Camera.position;
            var cameraForward = PlayerEditing.Camera.forward;

            Visuals.FacingVertex = Enumerable.Empty<Transform>().Prepend(room.transform)
                .Select(transform => (
                    room: transform.gameObject,
                    localPosition: transform.InverseTransformPoint(cameraPosition),
                    localForward: transform.InverseTransformDirection(cameraForward)))
                .Select(t => FindClosestVertexFacingAt(t.room, t.localPosition, t.localForward))
                .Where(v => v != null)
                .Select(v => new TransformVertex?(new(v, room.transform)))
                .FirstOrDefault();
        }

        private void UpdateNearestCell()
        {
            if (PlayerEditing != null && PlayerEditing.Camera)
            {
                var playerPosition = PlayerEditing.Position;
                Visuals.NearestCell = NavigationMesh.GetCellWithin(playerPosition);
            }
        }

        private void UpdateCachedCell()
        {
        }

        private void UpdateFacingCell()
        {
            if (PlayerEditing == null)
            {
                return;
            }

            if (!RoomUtils.TryGetRoom(PlayerEditing.Position, out var room))
            {
                return;
            }

            var cameraPosition = PlayerEditing.Camera.position;
            var cameraForward = PlayerEditing.Camera.forward;

            Visuals.FacingCell = Enumerable.Empty<Transform>().Prepend(room.transform)
                .Select(transform => (
                    room: transform.gameObject,
                    localPosition: transform.InverseTransformPoint(cameraPosition),
                    localForward: transform.InverseTransformDirection(cameraForward)))
                .Select(t => new TransformCell(FindClosestCellFacingAt(t.room, t.localPosition, t.localForward), t.room.transform))
                .Where(ta => ta.Local != null)
                .Select(ta => new TransformCell?(ta))
                .FirstOrDefault();
        }

        private void UpdateVertexAutoSelect()
        {
            if (PlayerEditing != null && AutoSelectModeEnabled && Visuals.NearestVertex != null)
            {
                if (SelectedVertices.Any() && SelectedVertices.First().Transform != Visuals.NearestVertex.Value.Transform)
                {
                    return;
                }

                if (!SelectedVertices.Contains(Visuals.NearestVertex.Value))
                {
                    SelectedVertices.Add(Visuals.NearestVertex.Value);
                }
                else if (SelectedVertices.Count > 1 && SelectedVertices.FirstOrDefault() == Visuals.NearestVertex.Value)
                {
                    AutoSelectModeEnabled = false;
                    LabApiPlugin.Instance?.Presentation?.ShowEditorStoppedOnFirstVertex(PlayerEditing);

                    Debug.Log($"Vertex auto-selection stopped on first vertex selected.");
                }
            }
        }

        #region Private constructor
        private NavigationMeshEditor()
        { }
        #endregion

        private IEnumerator<float> RunEachFrame(Action action)
        {
            while (true)
            {
                action.Invoke();

                yield return Timing.WaitForOneFrame;
            }
        }

        private IEnumerator<float> RunOncePerSecond(Action action)
        {
            while (true)
            {
                action.Invoke();

                yield return Timing.WaitForSeconds(1f);
            }
        }
    }
}
