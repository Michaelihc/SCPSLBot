using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace SCPSLBot.Navigation.Mesh
{
    internal sealed class NavigationMeshDocument
    {
        private const int MaxRoomForms = 512;
        private const int MaxVerticesPerForm = 100000;
        private const int MaxCellsPerForm = 100000;
        private const int MaxVerticesPerCell = 256;

        private readonly List<RoomMeshDocument> rooms;

        private NavigationMeshDocument(List<RoomMeshDocument> rooms)
        {
            this.rooms = rooms;
        }

        public static NavigationMeshDocument Parse(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
            {
                throw new InvalidDataException("Navigation mesh is empty.");
            }

            using var stream = new MemoryStream(bytes, writable: false);
            using var reader = new BinaryReader(stream);
            var version = reader.ReadByte();
            if (version < 3 || version > 5)
            {
                throw new InvalidDataException($"Unsupported navigation mesh version {version}; expected 3 through 5.");
            }

            var roomCount = ReadBoundedCount(reader, MaxRoomForms, "room form");
            var roomNames = new HashSet<string>(StringComparer.Ordinal);
            var parsedRooms = new List<RoomMeshDocument>(roomCount);
            for (var roomIndex = 0; roomIndex < roomCount; roomIndex++)
            {
                var roomForm = reader.ReadString();
                if (string.IsNullOrWhiteSpace(roomForm) || roomForm.Length > 256 || !roomNames.Add(roomForm))
                {
                    throw new InvalidDataException($"Invalid or duplicate room form at index {roomIndex}.");
                }

                var vertexCount = ReadBoundedCount(reader, MaxVerticesPerForm, $"vertex for {roomForm}");
                var vertices = new Vector3[vertexCount];
                for (var vertexIndex = 0; vertexIndex < vertexCount; vertexIndex++)
                {
                    var vertex = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                    if (!IsFinite(vertex.x) || !IsFinite(vertex.y) || !IsFinite(vertex.z))
                    {
                        throw new InvalidDataException($"Room {roomForm} vertex {vertexIndex} is not finite.");
                    }

                    vertices[vertexIndex] = vertex;
                }

                var cellCount = ReadBoundedCount(reader, MaxCellsPerForm, $"cell for {roomForm}");
                var cells = new int[cellCount][];
                for (var cellIndex = 0; cellIndex < cellCount; cellIndex++)
                {
                    var cellVertexCount = ReadBoundedCount(reader, MaxVerticesPerCell, $"cell vertex for {roomForm}");
                    if (cellVertexCount < 3)
                    {
                        throw new InvalidDataException($"Room {roomForm} cell {cellIndex} has fewer than three vertices.");
                    }

                    var indices = new int[cellVertexCount];
                    var uniqueIndices = new HashSet<int>();
                    for (var index = 0; index < cellVertexCount; index++)
                    {
                        var vertexIndex = reader.ReadInt32();
                        if (vertexIndex < 0 || vertexIndex >= vertexCount || !uniqueIndices.Add(vertexIndex))
                        {
                            throw new InvalidDataException($"Room {roomForm} cell {cellIndex} contains an invalid or duplicate vertex index {vertexIndex}.");
                        }

                        indices[index] = vertexIndex;
                    }

                    cells[cellIndex] = indices;
                    if (version < 5)
                    {
                        var legacyConnectionCount = ReadBoundedCount(reader, MaxCellsPerForm, $"legacy connection for {roomForm}");
                        for (var connectionIndex = 0; connectionIndex < legacyConnectionCount; connectionIndex++)
                        {
                            var connectedCell = reader.ReadInt32();
                            if (connectedCell < 0 || connectedCell >= cellCount)
                            {
                                throw new InvalidDataException($"Room {roomForm} cell {cellIndex} references invalid legacy cell {connectedCell}.");
                            }
                        }
                    }
                }

                parsedRooms.Add(new RoomMeshDocument(roomForm, vertices, cells));
            }

            if (stream.Position != stream.Length)
            {
                throw new InvalidDataException($"Navigation mesh has {stream.Length - stream.Position} trailing bytes.");
            }

            return new NavigationMeshDocument(parsedRooms);
        }

        public void Publish()
        {
            foreach (var room in rooms)
            {
                if (!NavigationMesh.MeshesByRoomForm.TryGetValue(room.Form, out var mesh))
                {
                    mesh = NavigationMesh.CreateMesh(room.Form);
                }

                var vertices = new Vertex[room.Vertices.Length];
                for (var i = 0; i < room.Vertices.Length; i++)
                {
                    vertices[i] = mesh.AddVertex(room.Vertices[i]);
                }

                foreach (var cellIndices in room.Cells)
                {
                    var cellVertices = new Vertex[cellIndices.Length];
                    for (var i = 0; i < cellIndices.Length; i++)
                    {
                        cellVertices[i] = vertices[cellIndices[i]];
                    }

                    mesh.MakeCell(cellVertices);
                }
            }
        }

        private static int ReadBoundedCount(BinaryReader reader, int maximum, string label)
        {
            var count = reader.ReadInt32();
            if (count < 0 || count > maximum)
            {
                throw new InvalidDataException($"Invalid {label} count {count}; maximum is {maximum}.");
            }

            return count;
        }

        private static bool IsFinite(float value)
            => !float.IsNaN(value) && !float.IsInfinity(value);

        private sealed class RoomMeshDocument
        {
            public RoomMeshDocument(string form, Vector3[] vertices, int[][] cells)
            {
                Form = form;
                Vertices = vertices;
                Cells = cells;
            }

            public string Form { get; }
            public Vector3[] Vertices { get; }
            public int[][] Cells { get; }
        }
    }
}
