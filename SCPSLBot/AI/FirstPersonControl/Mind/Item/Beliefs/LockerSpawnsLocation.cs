using MapGeneration;
using MapGeneration.Distributors;
using SCPSLBot.AI.FirstPersonControl.Mind.Spacial;
using SCPSLBot.AI.FirstPersonControl.Perception.Senses;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace SCPSLBot.AI.FirstPersonControl.Mind.Item.Beliefs
{
    /// <summary>
    /// Structure spawnpoints of <see cref="StructureType"/> in the current and neighbouring rooms
    /// the bot has not looked at yet (a locker may stand there). Rebuilt only when the room or the
    /// topology changes; a spot counts as checked once it is seen from up close.
    /// </summary>
    internal class LockerSpawnsLocation : Location
    {
        public StructureType StructureType { get; }

        private readonly RoomSightSense roomSense;
        private readonly ItemsWithinSightSense spotSense;
        private readonly Predicate<Vector3> isAbsent;

        public LockerSpawnsLocation(StructureType type, RoomSightSense roomSense, ItemsWithinSightSense spotSense)
        {
            this.StructureType = type;

            this.roomSense = roomSense;
            this.spotSense = spotSense;
            isAbsent = absentPositions.Remove;

            this.roomSense.OnAfterSensedForeignRooms += OnAfterSensedForeignRooms;
            this.spotSense.OnAfterSensedItemsWithinSight += OnAfterSensedSpots;
        }

        private readonly HashSet<Vector3> visitedLockerSpawnPositions = new();
        private readonly HashSet<Vector3> absentPositions = new();
        private readonly List<Vector3> spawnPositions = new();
        private readonly Dictionary<RoomIdentifier, Vector3[]> roomLockerSpawnPositions = new();
        private readonly List<StructureSpawnpoint> structureSpawnpoints = new();
        private int generation = int.MinValue;

        private void OnAfterSensedSpots()
        {
            if (Positions.Count == 0)
            {
                return;
            }

            foreach (var spawnPosition in Positions)
            {
                if (spotSense.IsSpotVisible(spawnPosition))
                {
                    visitedLockerSpawnPositions.Add(spawnPosition);
                    absentPositions.Add(spawnPosition);
                }
            }

            if (absentPositions.Count > 0)
            {
                RemoveAllPositions(isAbsent);
                absentPositions.Clear();
            }
        }

        private void OnAfterSensedForeignRooms()
        {
            var roomWithin = this.roomSense.RoomWithin;
            if (roomWithin == null)
            {
                if (BotLog.Verbose) Debug.Log($"RoomSightSense.RoomWithin is null");
                return;
            }

            var current = Navigation.NavigationSystem.Instance.MapGeneration;
            if (current != generation)
            {
                generation = current;
                visitedLockerSpawnPositions.Clear();
                roomLockerSpawnPositions.Clear();
            }

            spawnPositions.Clear();
            foreach (var foreignRoom in this.roomSense.ForeignRooms)
            {
                AddUnvisited(this.GetLockerSpawnPositions(foreignRoom));
            }
            AddUnvisited(this.GetLockerSpawnPositions(roomWithin));

            this.SetPositions(spawnPositions);
        }

        private void AddUnvisited(Vector3[] positions)
        {
            foreach (var position in positions)
            {
                if (!visitedLockerSpawnPositions.Contains(position) && !spawnPositions.Contains(position))
                {
                    spawnPositions.Add(position);
                }
            }
        }

        private Vector3[] GetLockerSpawnPositions(RoomIdentifier room)
        {
            if (!this.roomLockerSpawnPositions.TryGetValue(room, out var positions))
            {
                room.GetComponentsInChildren(structureSpawnpoints);

                var matching = new List<Vector3>();
                foreach (var spawnPoint in structureSpawnpoints)
                {
                    if (Array.IndexOf(spawnPoint.CompatibleStructures, this.StructureType) >= 0)
                    {
                        matching.Add(spawnPoint.transform.position);
                    }
                }

                positions = matching.ToArray();
                this.roomLockerSpawnPositions.Add(room, positions);
            }

            return positions;
        }

        public override string ToString()
        {
            return $"{nameof(LockerSpawnsLocation)}: {this.Positions.Count}";
        }
    }
}
