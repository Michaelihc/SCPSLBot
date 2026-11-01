using SCPSLBot.Navigation.Commands;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace SCPSLBot.Navigation.Mesh
{
    internal partial class NavigationMesh
    {
        public List<Vertex> Vertices { get; } = new();
        public event Action<Vertex> VertexCreated;
        public event Action<Vertex> VertexDeleted;

        public List<Cell> Cells { get; } = new();
        public event Action<Cell> CellCreated;
        public event Action<Cell> CellDeleted;

        /// <summary>True for meshes filled at runtime from live collision probes; never persisted.</summary>
        public bool IsGenerated { get; set; }

        // Square-grid forms answer point lookups from here; any later edit falls back to a scan.
        private Dictionary<(int, int), List<Cell>> cellGrid;
        private float cellGridStep;

        private NavigationMesh()
        {
            VertexDeleted += RemoveVertexFromCells;
        }

        #region Mesh manipulation

        public Vertex AddVertex(Vector3 position)
        {
            var newVertex = new Vertex(position);
            Vertices.Add(newVertex);

            VertexCreated?.Invoke(newVertex);
            MarkTopologyChanged();

            return newVertex;
        }

        public bool DeleteVertex(Vertex vertex)
        {
            if (!Vertices.Remove(vertex))
            {
                return false;
            }

            VertexDeleted?.Invoke(vertex);
            MarkTopologyChanged();

            return true;
        }

        private void RemoveVertexFromCells(Vertex vertex)
        {
            foreach (var cell in Cells)
            {
                cell.RemoveVertex(vertex);

                RemoveAdjacentCells(cell);
                AddAdjacentCells(cell);
            }
        }

        public bool MoveVertex(Vertex vertex, Vector3 newPosition)
        {
            vertex.Position = newPosition;
            cellGrid = null;
            MarkTopologyChanged();

            return true;
        }

        public Cell MakeCell(IEnumerable<Vertex> vertices)
        {
            var newCell = new Cell(vertices);
            Cells.Add(newCell);
            cellGrid = null;

            AddAdjacentCells(newCell);

            CellCreated?.Invoke(newCell);
            MarkTopologyChanged();

            return newCell;
        }

        /// <summary>Adds a cell whose adjacency the caller sets itself.</summary>
        public Cell MakeUnlinkedCell(IEnumerable<Vertex> vertices)
        {
            var newCell = new Cell(vertices);
            Cells.Add(newCell);
            CellCreated?.Invoke(newCell);
            MarkTopologyChanged();
            return newCell;
        }

        public void SetCellGrid(float step, Dictionary<(int, int), List<Cell>> grid)
        {
            cellGridStep = step;
            cellGrid = grid;
        }

        /// <summary>The cells that may hold <paramref name="localPosition"/>.</summary>
        public IReadOnlyList<Cell> GetCellsAt(Vector3 localPosition)
        {
            if (cellGrid == null)
            {
                return Cells;
            }

            var key = (Mathf.RoundToInt(localPosition.x / cellGridStep), Mathf.RoundToInt(localPosition.z / cellGridStep));
            return cellGrid.TryGetValue(key, out var cells) ? cells : Array.Empty<Cell>();
        }

        private void AddAdjacentCells(Cell newCell)
        {
            foreach (var adjacentCell in Cells)
            {
                foreach (var edge in newCell.Edges)
                {
                    var adjacentEdge = new Edge(edge.To, edge.From);
                    if (adjacentCell.Edges.Contains(adjacentEdge))
                    {
                        newCell.AddAdjacentCell(adjacentCell, adjacentEdge);
                        adjacentCell.AddAdjacentCell(newCell, edge);
                    }
                }
            }
        }

        /// <summary>Drops every vertex and cell so the form can be filled again.</summary>
        public void ClearCells()
        {
            foreach (var cell in Cells.ToArray())
            {
                RemoveCell(cell);
            }

            Vertices.Clear();
            MarkTopologyChanged();
        }

        public bool RemoveCell(Cell cell)
        {
            var cells = Cells;
            if (!cells.Remove(cell))
            {
                return false;
            }

            cellGrid = null;

            RemoveAdjacentCells(cell);

            CellDeleted?.Invoke(cell);
            MarkTopologyChanged();

            return true;
        }

        private void RemoveAdjacentCells(Cell cell)
        {
            foreach (var otherCell in Cells)
            {
                cell.RemoveAdjacentCell(otherCell);
                otherCell.RemoveAdjacentCell(cell);
            }
        }

        public void AddVertexToCell(Cell cell, Vertex vertex, Vertex beforeVertex)
        {
            cell.AddVertex(vertex, beforeVertex);

            RemoveAdjacentCells(cell);
            AddAdjacentCells(cell);
            MarkTopologyChanged();
        }

        #endregion
        #region Mesh reading/writing

        public void ReadMesh(BinaryReader binaryReader, byte version)
        {
            ///
            /// Vertices reading
            /// 

            var vertexCount = binaryReader.ReadInt32();

            for (var j = 0; j < vertexCount; j++)
            {
                var vertexPosition = new Vector3()
                {
                    x = binaryReader.ReadSingle(),
                    y = binaryReader.ReadSingle(),
                    z = binaryReader.ReadSingle()
                };

                var newVertex = AddVertex(vertexPosition);
            }

            ///
            /// Cells reading
            ///

            var cellsCount = binaryReader.ReadInt32();

            var cellsVertices = new int[cellsCount][];
            var cellsConnections = new int[cellsCount][];

            for (var j = 0; j < cellsCount; j++)
            {
                var cellVerticesCount = binaryReader.ReadInt32();
                var cellVertices = new int[cellVerticesCount];
                for (var k = 0; k < cellVerticesCount; k++)
                {
                    cellVertices[k] = binaryReader.ReadInt32();
                }
                cellsVertices[j] = cellVertices;

                var newCell = MakeCell(cellVertices.Select(vertexIdx => Vertices[vertexIdx]));

                if (version < 5)
                {
                    var connectedCellsCount = binaryReader.ReadInt32();
                    var connectedCells = new int[connectedCellsCount];
                    for (var k = 0; k < connectedCellsCount; k++)
                    {
                        connectedCells[k] = binaryReader.ReadInt32();
                    }
                    cellsConnections[j] = connectedCells;
                }
            }
        }

        public void WriteMesh(BinaryWriter binaryWriter)
        {
            binaryWriter.Write(Vertices.Count);
            foreach (var vertex in Vertices)
            {
                binaryWriter.Write(vertex.Position.x);
                binaryWriter.Write(vertex.Position.y);
                binaryWriter.Write(vertex.Position.z);
            }

            binaryWriter.Write(Cells.Count);
            foreach (var cell in Cells)
            {
                binaryWriter.Write(cell.Vertices.Count);
                foreach (var vertexIdx in cell.Vertices.Select(cellVertex => Vertices.IndexOf(cellVertex)))
                {
                    binaryWriter.Write(vertexIdx);
                }
            }
        }

        #endregion
    }
}
