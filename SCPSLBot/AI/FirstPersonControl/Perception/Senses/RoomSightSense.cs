using MapGeneration;
using SCPSLBot.AI.FirstPersonControl.Perception.Senses.Sight;
using SCPSLBot.Navigation.Mesh;
using System;
using System.Collections.Generic;
using Unity.Jobs;
using UnityEngine;

namespace SCPSLBot.AI.FirstPersonControl.Perception.Senses
{
    internal class RoomSightSense : SightSense, ISense
    {
        public List<TransformCell> ForeignRoomsCells { get; } = new();
        public List<RoomIdentifier> ForeignRooms => foreignRooms;
        public RoomIdentifier RoomWithin { get; private set; }

        public event Action<TransformCell> OnSensedForeignRoomCell;
        public event Action OnAfterSensedForeignRooms;

        public event Action<RoomIdentifier> OnSensedRoomWithin;

        private readonly FpcBotPlayer _fpcBotPlayer;
        private readonly List<RoomIdentifier> foreignRooms = new();
        private readonly HashSet<RoomIdentifier> foreignRoomsSet = new();
        private RoomIdentifier cachedTopologyRoom;
        private NavigationMesh cachedTopologyMesh;
        private int cachedTopologyCellCount = -1;
        private int cachedTopologyVersion = -1;
        private static float nextMissingAdjacencyWarningAt;

        public RoomSightSense(FpcBotPlayer botPlayer) : base(botPlayer)
        {
            _fpcBotPlayer = botPlayer;
        }

        public override void ProcessSightSensedItems()
        {
            UpdateRoomWithin();
            UpdateForeignRoomsCells();

            foreach (var sensedForeignRoomCell in ForeignRoomsCells)
            {
                OnSensedForeignRoomCell?.Invoke(sensedForeignRoomCell);
            }
            OnAfterSensedForeignRooms?.Invoke();
        }

        private void UpdateRoomWithin()
        {
            var playerPosition = _fpcBotPlayer.PlayerPosition;

            if (!RoomUtils.TryGetRoom(playerPosition, out var newRoomWithin))
            {
                if (BotLog.Verbose) Debug.LogWarning($"Could not determine room bot currently in");
                return;
            }

            OnSensedRoomWithin?.Invoke(newRoomWithin);
            RoomWithin = newRoomWithin;
        }

        private void UpdateForeignRoomsCells()
        {
            if (!RoomWithin
                || !NavigationMesh.LocalMeshesByRoom.TryGetValue(RoomWithin.gameObject, out var roomMesh))
            {
                ForeignRoomsCells.Clear();
                foreignRooms.Clear();
                foreignRoomsSet.Clear();
                cachedTopologyRoom = null;
                cachedTopologyMesh = null;
                cachedTopologyCellCount = -1;
                cachedTopologyVersion = -1;
                return;
            }

            // Room-to-room navmesh links are static during normal play. Rebuilding this topology on
            // every sight tick made every bot scan every cell in its room each frame. Keep the
            // existing per-frame sensing event cadence, but rebuild the cached lists only when the
            // room or its mesh changes (including a navmesh reload or editor cell-count change).
            if (RoomWithin == cachedTopologyRoom
                && ReferenceEquals(roomMesh, cachedTopologyMesh)
                && roomMesh.Cells.Count == cachedTopologyCellCount
                && NavigationMesh.TopologyVersion == cachedTopologyVersion)
            {
                return;
            }

            ForeignRoomsCells.Clear();
            foreignRooms.Clear();
            foreignRoomsSet.Clear();

            foreach (var localCell in roomMesh.Cells)
            {
                var transformCell = new TransformCell(localCell, RoomWithin.transform);
                foreach (var foreignCell in NavigationMesh.GetForeignConnectedCells(transformCell))
                {
                    var foreignRoom = foreignCell.Transform.GetComponent<RoomIdentifier>();
                    if (!foreignRoom)
                    {
                        continue;
                    }

                    if (foreignCell.Local?.AdjacentCells == null || foreignCell.Local.AdjacentCells.Count == 0)
                    {
                        if (Time.realtimeSinceStartup >= nextMissingAdjacencyWarningAt)
                        {
                            nextMissingAdjacencyWarningAt = Time.realtimeSinceStartup + 30f;
                            Debug.LogWarning("SCPSLBot skipped a foreign navigation cell with no adjacent local cell.");
                        }

                        continue;
                    }

                    var adjacentLocalCell = foreignCell.Local.AdjacentCells[0];
                    ForeignRoomsCells.Add(new TransformCell(adjacentLocalCell, foreignCell.Transform));

                    if (foreignRoomsSet.Add(foreignRoom))
                    {
                        foreignRooms.Add(foreignRoom);
                    }
                }
            }

            cachedTopologyRoom = RoomWithin;
            cachedTopologyMesh = roomMesh;
            cachedTopologyCellCount = roomMesh.Cells.Count;
            cachedTopologyVersion = NavigationMesh.TopologyVersion;
        }

        public void ProcessEnter(Collider other)
        {
        }

        public void ProcessExit(Collider other)
        {
        }

        public IEnumerator<JobHandle> ProcessSensibility()
        {
            yield break;
        }
    }
}
