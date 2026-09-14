#define UNITY_ASSERTIONS

using CommandSystem;
using LabApi.Features.Console;
using LabApi.Features.Wrappers;
using SCPSLBot.Navigation;
using SCPSLBot.Navigation.Mesh;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine.Assertions;

namespace SCPSLBot.Tests.Navigation
{
    internal class NavigationSystemTests : ICommand
    {
        public string Command { get; } = "nav";

        public string[] Aliases { get; } = new string[] { };

        public string Description { get; } = "Tests navigation.";

        public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
        {
            NavigationSystem.Instance.Terminate();

            TestInitMeshes();

            var existingForm = GetRandomExistingForm();
            TestCreateMesh("Non_Existing");

            TestCreateVertex(existingForm);     // #0
            TestCreateVertex(existingForm);     // #1
            TestCreateVertex(existingForm);     // #2
            TestCreateVertex(existingForm);     // #3
            TestCreateVertex(existingForm);     // #4

            TestDeleteVertex(existingForm, 2);  // #3
            TestCreateVertex(existingForm);     // #4

            TestMakeCell(existingForm, 0, 1, 2);        // #0
            TestMakeCell(existingForm, 2, 1, 3, 4);     // #1

            TestCreateVertex(existingForm);     // #5
            TestCreateVertex(existingForm);     // #6
            TestCreateVertex(existingForm);     // #7
            TestCreateVertex(existingForm);     // #8

            TestMakeCell(existingForm, 2, 3, 7, 8);     // #2

            TestRemoveCell(existingForm, 2);            // #1
            TestMakeCell(existingForm, 4, 3, 5, 6);     // #2

            TestRemoveCell(existingForm, 2);            // #1
            TestMakeCell(existingForm, 4, 5, 6);        // #2

            TestAddVertexToCell(existingForm, 2, 3, 5);
            TestAddVertexToCell(existingForm, 2, 7, 3);

            TestDeleteVertex(existingForm, 7);
            TestDeleteVertex(existingForm, 3);

            TestPersistance();

            response = $"Passed.";
            return true;
        }

        private void TestInitMeshes()
        {
            NavigationMesh.InitMeshes();

            Assert.AreEqual(Room.List.Count, NavigationMesh.RoomsByForm.Values.SelectMany(l => l).Count());
            foreach (var apiRoom in Room.List)
            {
                var room = apiRoom.Base.gameObject;
                var roomForm = room.name.EndsWith("(Clone)") ?
                    room.name.Remove(room.name.LastIndexOf("(Clone)")) :
                    room.name;

                Assert.IsTrue(NavigationMesh.RoomsByForm.TryGetValue(roomForm, out var rooms));
                Assert.IsTrue(rooms.Contains(room));

                Assert.IsTrue(NavigationMesh.MeshesByRoomForm.ContainsKey(roomForm));
                Assert.IsNotNull(NavigationMesh.MeshesByRoomForm[roomForm]);

                Assert.IsTrue(NavigationMesh.LocalMeshesByRoom.ContainsKey(room));
                Assert.AreEqual(NavigationMesh.MeshesByRoomForm[roomForm], NavigationMesh.LocalMeshesByRoom[room]);
            }

            Logger.Info(nameof(TestInitMeshes));
        }

        private void TestCreateMesh(string form)
        {
            var mesh = NavigationMesh.CreateMesh(form);

            Assert.IsNotNull(mesh);

            Assert.IsTrue(NavigationMesh.MeshesByRoomForm.ContainsKey(form));
            Assert.AreEqual(mesh, NavigationMesh.MeshesByRoomForm[form]);

            if (NavigationMesh.RoomsByForm.TryGetValue(form, out var rooms))
            {
                foreach (var room in rooms)
                {
                    Assert.IsTrue(NavigationMesh.LocalMeshesByRoom.ContainsKey(room));
                    Assert.AreEqual(mesh, NavigationMesh.LocalMeshesByRoom[room]);
                }
            }

            Logger.Info($"{nameof(TestCreateMesh)}({form})");
        }

        private Vertex TestCreateVertex(string form)
        {
            var mesh = NavigationMesh.MeshesByRoomForm[form];

            Vertex? emittedVertex = null;
            void vertexCreatedHandler(Vertex v)
            {
                emittedVertex = v;
            }
            mesh.VertexCreated += vertexCreatedHandler;

            var position = UnityEngine.Random.insideUnitSphere with { y = 0f } * 5f;
            var vertex = mesh.AddVertex(position);

            Assert.IsNotNull(vertex);
            Assert.IsTrue(mesh.Vertices.Contains(vertex));

            Assert.AreEqual(emittedVertex, vertex);

            mesh.VertexCreated -= vertexCreatedHandler;

            Logger.Info($"{nameof(TestCreateVertex)}({form})");
            return vertex;
        }

        private void TestDeleteVertex(string form, int vertexIdx)
        {
            var mesh = NavigationMesh.MeshesByRoomForm[form];
            var vertex = mesh.Vertices[vertexIdx];

            Vertex? emittedVertex = null;
            void vertexDeletedHandler(Vertex v)
            {
                emittedVertex = v;
            }
            mesh.VertexDeleted += vertexDeletedHandler;

            var countBefore = mesh.Vertices.Count;

            mesh.DeleteVertex(vertex);

            var countAfter = mesh.Vertices.Count;

            Assert.IsFalse(mesh.Vertices.Contains(vertex), $"Mesh contain deleted vertex.");
            Assert.AreEqual(1, countBefore - countAfter, $"Number of mesh vertices haven't decreased.");

            Assert.AreEqual(emittedVertex, vertex, $"Event with expected vertex haven't raised.");;

            foreach (var cell in mesh.Cells)
            {
                Assert.IsFalse(cell.Vertices.Contains(vertex), $"Cell contain deleted vertex.");
                Assert.IsFalse(cell.Edges.Any(e => e.From == vertex || e.To == vertex), $"Cell contain edges with deleted vertex.");

                var adjacentCells = cell.Edges
                    .Select(e => new Edge(e.To, e.From))
                    .SelectMany(ae => mesh.Cells.Where(oc => oc.Edges.Contains(ae)).Select(ac => (ac, ae)));

                Assert.AreEqual(adjacentCells.Count(), cell.AdjacentCells.Count, $"Cell adjacent cells count does not match expected.");
                Assert.AreEqual(adjacentCells.Count(), cell.AdjacentCellEdges.Count, $"Cell adjacent edges count does not match expected.");

                foreach (var (adjacentCell, adjacentEdge) in adjacentCells)
                {
                    Assert.IsTrue(cell.AdjacentCells.Contains(adjacentCell), $"Cell does not contain adjacent cell.");
                    Assert.AreEqual(adjacentEdge, cell.AdjacentCellEdges[adjacentCell], $"Cell does not contain adjacent edge.");
                }

                if (NavigationMesh.RoomsByForm.TryGetValue(form, out var rooms))
                {
                    foreach (var room in rooms)
                    {
                        var transformCell = new TransformCell(cell, room.transform);

                        Assert.AreEqual(adjacentCells.Count(), transformCell.AdjacentCells.Count(), $"Tranform cell adjacent cells count does not match expected.");
                        Assert.AreEqual(adjacentCells.Count(), transformCell.AdjacentCellEdges.Count, $"Transform cell adjacent edges count does not match expected.");

                        foreach (var (adjacentCell, adjacentEdge) in adjacentCells)
                        {
                            var adjacentTransformCell = new TransformCell(adjacentCell, room.transform);
                            var adjacentTransformEdge = new TransformEdge(adjacentEdge, room.transform);
                            Assert.IsTrue(transformCell.AdjacentCells.Contains(adjacentTransformCell), $"Tranform cell does not contain adjacent cell.");
                            Assert.AreEqual(adjacentTransformEdge, transformCell.AdjacentCellEdges[adjacentTransformCell], $"Tranform cell does not contain adjacent edge.");
                        }
                    }
                }
            }

            mesh.VertexDeleted -= vertexDeletedHandler;

            Logger.Info($"{nameof(TestDeleteVertex)}({form}, {vertexIdx})");
        }

        private void TestMakeCell(string form, params int[] vertexIdxs)
        {
            var mesh = NavigationMesh.MeshesByRoomForm[form];
            var vertices = vertexIdxs.Select(idx => mesh.Vertices[idx]);
            var edges = vertices.Zip(vertices.Skip(1), (v1, v2) => new Edge(v1, v2)).Append(new(vertices.Last(), vertices.First()));

            Cell? emittedCell = null;
            void createdHandler(Cell a)
            {
                emittedCell = a;
            }
            mesh.CellCreated += createdHandler;

            var cell = mesh.MakeCell(vertices);

            Assert.IsNotNull(cell);

            Assert.AreEqual(vertexIdxs.Length, cell.Vertices.Count);
            foreach (var (vertex, cellVertex) in vertices.Zip(cell.Vertices, (vertex, cellVertex) => (vertex, cellVertex)))
            {
                Assert.AreEqual(vertex, cellVertex);
            }

            Assert.AreEqual(edges.Count(), cell.Edges.Count());
            foreach (var (edge, cellEdge) in edges.Zip(cell.Edges, (edge, cellEdge) => (edge, cellEdge)))
            {
                Assert.AreEqual(edge, cellEdge);
            }

            foreach (var edge in edges)
            {
                var adjacentEdge = new Edge(edge.To, edge.From);
                var adjacentCells = mesh.Cells.Where(c => c.Edges.Contains(adjacentEdge));
                foreach (var adjacentCell in adjacentCells)
                {
                    Assert.IsTrue(cell.AdjacentCells.Contains(adjacentCell), "Cell does not contain expected adjacent cell.");
                    Assert.IsTrue(cell.AdjacentCellEdges.TryGetValue(adjacentCell, out var cellAdjacentEdge), "Cell does not contain edge of expected adjacent cell.");
                    Assert.AreEqual(adjacentEdge, cellAdjacentEdge, "Cell does not contain expected edge of adjacent cell.");

                    Assert.IsTrue(adjacentCell.AdjacentCells.Contains(cell), "Adjacent cell does not contain expected cell.");
                    Assert.IsTrue(adjacentCell.AdjacentCellEdges.TryGetValue(cell, out var cellAdjacentAdjacentEdge), "Adjacent cell does not contain edge of expected cell.");
                    Assert.AreEqual(edge, cellAdjacentAdjacentEdge, "Adjacent cell does not contain expected edge of cell.");
                }
            }

            Assert.IsTrue(mesh.Cells.Contains(cell));

            if (NavigationMesh.RoomsByForm.TryGetValue(form, out var rooms))
            {
                foreach (var room in rooms)
                {
                    var transformCell = new TransformCell(cell, room.transform);
                    Assert.AreEqual(room.transform.TransformPoint(cell.CenterPosition), transformCell.CenterPosition);

                    Assert.IsTrue(NavigationMesh.ForeignConnectedCells.ContainsKey(transformCell));
                    Assert.IsNotNull(NavigationMesh.ForeignConnectedCells[transformCell]);

                    Assert.IsTrue(NavigationMesh.ForeignConnectedCellEdges.ContainsKey(transformCell));
                    Assert.IsNotNull(NavigationMesh.ForeignConnectedCellEdges[transformCell]);

                    foreach (var transformEdge in edges.Select(e => new TransformEdge(e, room.transform)))
                    {
                        var adjacentTransformEdge = new TransformEdge(transformEdge.To, transformEdge.From, room.transform);
                        var adjacentTransformCells = mesh.Cells.Where(c => c.Edges.Select(e => new TransformEdge(e, room.transform)).Contains(adjacentTransformEdge))
                            .Select(c => new TransformCell(c, room.transform));
                        foreach (var adjacentTransformCell in adjacentTransformCells)
                        {
                            Assert.IsTrue(transformCell.AdjacentCells.Contains(adjacentTransformCell), "Transform cell does not contain expected adjacent transform cell.");
                            Assert.IsTrue(transformCell.AdjacentCellEdges.TryGetValue(adjacentTransformCell, out var cellAdjacentEdge), "Transform cell does not contain edge of expected adjacent transform cell.");
                            Assert.AreEqual(adjacentTransformEdge, cellAdjacentEdge, "Transform cell does not contain expected edge of adjacent transform cell.");
                            
                            Assert.IsTrue(adjacentTransformCell.AdjacentCells.Contains(transformCell), "Transform adjacent cell does not contain expected transform cell.");
                            Assert.IsTrue(adjacentTransformCell.AdjacentCellEdges.TryGetValue(transformCell, out var cellAdjacentAdjacentEdge), "Transform adjacent cell does not contain edge of expected transform cell.");
                            Assert.AreEqual(transformEdge, cellAdjacentAdjacentEdge, "Transform adjacent cell does not contain expected edge of transform cell.");
                        }
                    }
                }
            }

            Assert.AreEqual(emittedCell, cell);

            mesh.CellCreated -= createdHandler;

            Logger.Info($"{nameof(TestMakeCell)}({form}, {string.Join(", ", vertexIdxs)})");
        }

        private void TestRemoveCell(string form, int cellIdx)
        {
            var mesh = NavigationMesh.MeshesByRoomForm[form];
            var cell = mesh.Cells[cellIdx];

            Cell? emittedCell = null;
            void deletedHandler(Cell a)
            {
                emittedCell = a;
            }
            mesh.CellDeleted += deletedHandler;

            var countBefore = mesh.Cells.Count;

            mesh.RemoveCell(cell);

            var countAfter = mesh.Cells.Count;

            Assert.IsFalse(mesh.Cells.Contains(cell));
            Assert.AreEqual(1, countBefore - countAfter);

            foreach (var otherCell in mesh.Cells)
            {
                Assert.IsFalse(otherCell.AdjacentCells.Contains(cell));
                Assert.IsFalse(otherCell.AdjacentCellEdges.ContainsKey(cell));
            }

            if (NavigationMesh.RoomsByForm.TryGetValue(form, out var rooms))
            {
                foreach (var room in rooms)
                {
                    var transformCell = new TransformCell(cell, room.transform);
                    foreach (var otherCell in mesh.Cells)
                    {
                        var otherTransformCell = new TransformCell(otherCell, room.transform);
                        Assert.IsFalse(otherTransformCell.AdjacentCells.Contains(transformCell));
                        Assert.IsFalse(otherTransformCell.AdjacentCellEdges.ContainsKey(transformCell));
                    }

                    Assert.IsFalse(NavigationMesh.ForeignConnectedCells.ContainsKey(transformCell));
                    Assert.IsFalse(NavigationMesh.ForeignConnectedCellEdges.ContainsKey(transformCell));
                }
            }

            Assert.AreEqual(emittedCell, cell);

            mesh.CellDeleted -= deletedHandler;

            Logger.Info($"{nameof(TestRemoveCell)}({form}, {cellIdx})");
        }

        private void TestAddVertexToCell(string form, int cellIdx, int vertexIdx, int beforeVertexIdx)
        {
            // Arrange
            var mesh = NavigationMesh.MeshesByRoomForm[form];
            var cell = mesh.Cells[cellIdx];
            var vertex = mesh.Vertices[vertexIdx];
            var beforeVertex = mesh.Vertices[beforeVertexIdx];

            var oldEdge = cell.Edges.First(e => e.To == beforeVertex);
            var newEdges = new Edge[] {
                new(oldEdge.From, vertex),
                new(vertex, oldEdge.To)
            };

            var adjacentOldEdge = new Edge(oldEdge.To, oldEdge.From);
            var adjacentOldCells = mesh.Cells.Where(c => c.Edges.Contains(adjacentOldEdge));

            var adjacentNewEdges = newEdges.Select(ne => new Edge(ne.To, ne.From));
            var adjacentNewCells = adjacentNewEdges.SelectMany(ane => mesh.Cells.Where(p => p.Edges.Contains(ane)).Select(c => (c, ane)));

            // Act
            mesh.AddVertexToCell(cell, vertex, beforeVertex);

            // Assert
            Assert.AreEqual(cell.Vertices.IndexOf(beforeVertex)-1, cell.Vertices.IndexOf(vertex), "Cell does not contain added vertex before other vertex.");

            Assert.IsFalse(cell.Edges.Contains(oldEdge), $"Cell contain old edge {oldEdge}");
            foreach (var newEdge in newEdges)
            {
                Assert.IsTrue(cell.Edges.Contains(newEdge), $"Cell does not contain new edge {newEdge}");
            }

            foreach (var ((adjacentNewCell, adjacentNewEdge), i) in adjacentNewCells.Select((t, i) => (t, i)))
            {
                Assert.IsTrue(cell.AdjacentCells.Contains(adjacentNewCell), $"Cell does not contain adjacent new cell.");
                Assert.AreEqual(cell.AdjacentCellEdges[adjacentNewCell], adjacentNewEdge, $"Cell does not contain adjacent new edge.");

                Assert.IsTrue(adjacentNewCell.AdjacentCells.Contains(cell), $"Adjacent new cell does not contain cell.");
                Assert.AreEqual(adjacentNewCell.AdjacentCellEdges[cell], newEdges[i], $"Adjacent new cell does not contain new edge.");
            }

            foreach (var adjacentOldCell in adjacentOldCells)
            {
                Assert.IsFalse(cell.AdjacentCells.Contains(adjacentOldCell), $"Cell contain old adjacent cells");
                Assert.IsFalse(cell.AdjacentCellEdges.ContainsKey(adjacentOldCell), $"Cell contain old adjacent cells of adjacent old edge");

                Assert.IsFalse(adjacentOldCell.AdjacentCells.Contains(cell), $"Adjacent old cell contain cell.");
                Assert.IsFalse(adjacentOldCell.AdjacentCellEdges.ContainsKey(cell), $"Adjacent old cell contain cell of old edge");
            }

            if (NavigationMesh.RoomsByForm.TryGetValue(form, out var rooms))
            {
                foreach (var room in rooms)
                {
                    var transformCell = new TransformCell(cell, room.transform);
                    var newTransformEdges = newEdges.Select(e => new TransformEdge(e, room.transform)).ToArray();
                    foreach (var ((adjacentNewCell, adjacentNewEdge), i) in adjacentNewCells.Select((t, i) => (t, i)))
                    {
                        var adjacentNewTransformCell = new TransformCell(adjacentNewCell, room.transform);
                        var adjacentNewTransformEdge = new TransformEdge(adjacentNewEdge, room.transform);
                        Assert.IsTrue(transformCell.AdjacentCells.Contains(adjacentNewTransformCell), $"Transform cell does not contain adjacent new cell.");
                        Assert.AreEqual(transformCell.AdjacentCellEdges[adjacentNewTransformCell], adjacentNewTransformEdge, $"Transform cell does not contain adjacent new edge.");

                        Assert.IsTrue(adjacentNewTransformCell.AdjacentCells.Contains(transformCell), $"Adjacent new tranform cell does not contain cell.");
                        Assert.AreEqual(adjacentNewTransformCell.AdjacentCellEdges[transformCell], newTransformEdges[i], $"Adjacent new tranform cell does not contain new edge.");
                    }

                    foreach (var adjacentOldCell in adjacentOldCells)
                    {
                        var adjacentOldTransformCell = new TransformCell(adjacentOldCell, room.transform);
                        Assert.IsFalse(transformCell.AdjacentCells.Contains(adjacentOldTransformCell), $"Transform cell contain old adjacent cells");
                        Assert.IsFalse(transformCell.AdjacentCellEdges.ContainsKey(adjacentOldTransformCell), $"Transform cell contain old adjacent cells of adjacent old edge");

                        Assert.IsFalse(adjacentOldTransformCell.AdjacentCells.Contains(transformCell), $"Adjacent old tranform cell contain cell.");
                        Assert.IsFalse(adjacentOldTransformCell.AdjacentCellEdges.ContainsKey(transformCell), $"Adjacent old tranform cell contain cell of old edge");
                    }
                }
            }

            Logger.Info($"{nameof(TestAddVertexToCell)}({form}, {cellIdx}, {vertexIdx}, {beforeVertexIdx})");
        }

        private void TestPersistance()
        {
            Logger.Info($"{nameof(TestPersistance)}");

            // Arrange
            var meshesByRoomForm = NavigationMesh.MeshesByRoomForm.ToArray();

            // Act
            byte[] buffer;

            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                NavigationMesh.WriteMeshes(writer);

                buffer = stream.GetBuffer();
            }

            NavigationMesh.ResetMeshes();
            NavigationMesh.InitMeshes();

            using (var stream = new MemoryStream(buffer))
            using (var reader = new BinaryReader(stream))
            {
                NavigationMesh.ReadMeshes(reader);
            }

            // Assert
            Assert.AreEqual(meshesByRoomForm.Length, NavigationMesh.MeshesByRoomForm.Count, $"Number of mesh forms does not match.");
            foreach (var (form, mesh) in meshesByRoomForm)
            {
                Assert.IsTrue(NavigationMesh.MeshesByRoomForm.ContainsKey(form), $"Does not contain room form of mesh.");

                Assert.AreEqual(mesh.Vertices.Count, NavigationMesh.MeshesByRoomForm[form].Vertices.Count, $"Mesh vertices count do not match.");
                foreach (var (vertexBefore, vertexAfter) in mesh.Vertices.Zip(NavigationMesh.MeshesByRoomForm[form].Vertices, (l, r) => (l, r)))
                {
                    AssertVertexSame(vertexBefore, vertexAfter, $"Mesh vertex is not same.");
                }

                Assert.AreEqual(mesh.Cells.Count, NavigationMesh.MeshesByRoomForm[form].Cells.Count, $"Mesh cells count do not match.");
                foreach (var (cellBefore, cellAfter) in mesh.Cells.Zip(NavigationMesh.MeshesByRoomForm[form].Cells, (l, r) => (l, r)))
                {
                    AssertCellSame(cellBefore, cellAfter, $"Mesh cell is not same.");

                    Assert.AreEqual(cellBefore.AdjacentCells.Count, cellAfter.AdjacentCells.Count, $"Mesh cell adjacent cells count do not match.");
                    foreach (var (adjacentCellBefore, adjacentCellAfter) in cellBefore.AdjacentCells.Zip(cellAfter.AdjacentCells, (l, r) => (l, r)))
                    {
                        AssertCellSame(adjacentCellBefore, adjacentCellAfter, $"Mesh cell adjacent cell is not same.");
                    }

                    Assert.AreEqual(cellBefore.AdjacentCellEdges.Count, cellAfter.AdjacentCellEdges.Count, $"Mesh cell adjacent edges count do not match.");
                    foreach (var (adjacentCellEdgeBefore, adjacentCellEdgeAfter) in cellBefore.AdjacentCellEdges.Zip(cellAfter.AdjacentCellEdges, (l, r) => (l, r)))
                    {
                        var (adjacentCellBefore, adjacentEdgeBefore) = adjacentCellEdgeBefore;
                        var (adjacentCellAfter, adjacentEdgeAfter) = adjacentCellEdgeAfter;
                        AssertCellSame(adjacentCellBefore, adjacentCellAfter, $"Mesh cell adjacent edge cell is not same.");
                        AssertEdgeSame(adjacentEdgeBefore, adjacentEdgeAfter, $"Mesh cell adjacent edge is not equal.");
                    }
                }
            }
        }

        private void AssertCellSame(Cell expected, Cell actual, string message)
        {
            Assert.AreEqual(expected.Vertices.Count, actual.Vertices.Count, $"{message} Cell vertices count do not match.");
            foreach (var (cellVertexBefore, cellVertexAfter) in expected.Vertices.Zip(actual.Vertices, (l, r) => (l, r)))
            {
                AssertVertexSame(cellVertexBefore, cellVertexAfter, $"{message} Cell vertex is not same.");
            }
        }

        private void AssertEdgeSame(in Edge expected, in Edge actual, string message)
        {
            AssertVertexSame(expected.From, actual.From, $"{message} Edge from is not equal.");
            AssertVertexSame(expected.To, actual.To, $"{message} Edge to is not equal.");
        }

        private void AssertVertexSame(Vertex expected, Vertex actual, string message)
        {
            Assert.AreEqual(expected.Position, actual.Position, $"{message} Position do not match.");
        }

        private string GetRandomExistingForm()
        {
            var random = new Random();
            var count = NavigationMesh.RoomsByForm.Keys.Count;
            return NavigationMesh.RoomsByForm.Keys.ElementAt(random.Next(count - 1));
        }
    }
}
