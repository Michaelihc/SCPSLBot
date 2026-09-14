using System;
using UnityEngine;
using MapGeneration;
using SCPSLBot.AI.FirstPersonControl.Perception.Senses;
using MapGeneration.Distributors;
using System.Collections.Generic;
using System.Linq;

namespace SCPSLBot.AI.FirstPersonControl.Mind.Item.Beliefs
{
    internal class ItemSpawnsLocation<C> : ItemLocations<C> where C : IItemBeliefCriteria
    {
        private readonly ItemType[] spawnItemTypes;
        private readonly RoomSightSense roomSense;
        private readonly ItemsWithinSightSense itemsSightSense;

        public ItemSpawnsLocation(C criteria, ItemType[] spawnItemTypes, RoomSightSense roomSense, ItemsWithinSightSense itemsSightSense) 
            : base(criteria)
        {
            this.spawnItemTypes = spawnItemTypes;
            this.itemsSightSense = itemsSightSense;
            this.roomSense = roomSense;

            this.roomSense.OnAfterSensedForeignRooms += OnAfterSensedForeignRooms;
            this.itemsSightSense.OnAfterSensedItemsWithinSight += OnAfterSensedItemsWithinSight;
        }

        public readonly Dictionary<Vector3, float> ItemSpawnProbability = new();

        private readonly HashSet<Vector3> visitedSpawnPositions = new();
        private readonly HashSet<Vector3> absentPositions = new();

        private void OnAfterSensedItemsWithinSight()
        {
            foreach (var spawnPosition in Positions)
            {
                if (this.itemsSightSense.IsPositionWithinFov(spawnPosition)
                    //    && (!itemsSightSense.IsPositionObstructed(Position.Value) || itemsSightSense.GetDistanceToPosition(Position.Value) < 1.5f))
                    && (!itemsSightSense.IsPositionObstructed(spawnPosition)))
                {
                    this.visitedSpawnPositions.Add(spawnPosition);
                    ItemSpawnProbability.Remove(spawnPosition);

                    absentPositions.Add(spawnPosition);
                }
            }

            RemoveAllPositions(absentPositions.Remove);
        }

        private readonly List<Vector3> itemSpawnPositions = new();
        private IEnumerable<Vector3> unvisitedSpawnPositions;

        private void OnAfterSensedForeignRooms()
        {
            var roomWithin = this.roomSense.RoomWithin;
            if (roomWithin == null)
            {
                if (BotLog.Verbose) Debug.Log($"RoomSightSense.RoomWithin is null");
                return;
            }

            var foreignRooms = this.roomSense.ForeignRooms;

            itemSpawnPositions.Clear();
            foreach (var foreignRoom in foreignRooms)
            {
                itemSpawnPositions.AddRange(this.GetItemSpawnPositions(foreignRoom));
            }
            itemSpawnPositions.AddRange(this.GetItemSpawnPositions(roomWithin));

            unvisitedSpawnPositions ??= itemSpawnPositions
                .Where(spawnPosition => !this.visitedSpawnPositions.Contains(spawnPosition));

            SetPositions(unvisitedSpawnPositions);
        }

        private readonly Dictionary<RoomIdentifier, Vector3[]> roomItemSpawnPositions = new();

        private readonly List<ItemSpawnpointBase> itemSpawnpoints = new();
        private IEnumerable<(Vector3 Position, float Prob)> spawnPositionsQuery;

        private Vector3[] GetItemSpawnPositions(RoomIdentifier room)
        {
            // TODO: remove when remaining nav mesh added in rooms
            if (room.Name == RoomName.LczGreenhouse || room.Name == RoomName.Hcz079)
            {
                return Array.Empty<Vector3>();
            }

            if (!this.roomItemSpawnPositions.TryGetValue(room, out var spawnPositions))
            {
                room.GetComponentsInChildren(itemSpawnpoints);

                spawnPositionsQuery ??= itemSpawnpoints
                    .Select(spawnPoint => (spawnPoint, prob: GetSpawnProbability(spawnPoint)))
                    .Where(t => t.prob > 0f)
                    .SelectMany(t => GetAcceptedPositions(t.spawnPoint), 
                        (t, spawnTransform) => (spawnTransform.position, t.prob));

                spawnPositions = spawnPositionsQuery.Select(t => t.Position).ToArray();
                this.roomItemSpawnPositions.Add(room, spawnPositions);

                foreach (var (position, prob) in spawnPositionsQuery)
                {
                    this.ItemSpawnProbability.Add(position, prob);
                }
            }

            return spawnPositions;
        }

        private float GetSpawnProbability(ItemSpawnpointBase spawnpoint)
        {
            var numMatchingItemTypes = this.spawnItemTypes.Count(spawnpoint.InPresets);
            if (numMatchingItemTypes == 0)
            {
                return 0f;
            }

            var totalNumItemTypes = spawnpoint.PresetsCount();
            return totalNumItemTypes > 0 ? (float)numMatchingItemTypes / totalNumItemTypes : 0f;
        }

        private IEnumerable<Transform> GetAcceptedPositions(ItemSpawnpointBase spawnpoint)
        {
            var positionVariants = spawnpoint switch
            {
                PredefinedItemSpawnpoint predefinedSpawnpoint => this.spawnItemTypes
                    .Where(st => predefinedSpawnpoint.TargetItem == st)
                    .SelectMany(_ => predefinedSpawnpoint.PossibleSpawnpoints),

                RandomItemSpawnpoint randomSpawnpoint => this.spawnItemTypes
                    .SelectMany(st => randomSpawnpoint.Presets
                        .Where(p => p.TargetItem == st))
                    .SelectMany(p => p.PossibleSpawnpoints)
                    .Distinct(),

                RandomItemGroupSpawnpoint randomGroupSpawnpoint => this.spawnItemTypes
                    .SelectMany(st => randomGroupSpawnpoint.Presets
                        .SelectMany(g => g.Items)
                        .Where(p => p.TargetItem == st))
                    .Select(p => p.Position)
                    .Distinct(),

                _ => UnsupportedSpawnpointDiagnostics.EmptyPositions(spawnpoint)
            };

            return positionVariants;
        }

        public override string ToString()
        {
            return $"{nameof(ItemSpawnsLocation<C>)}({this.Criteria}): {this.Positions.Count}";
        }
    }

    internal static class ItemSpawnpointBaseExtensions
    {
        public static bool InPresets(this ItemSpawnpointBase spawnpoint, ItemType itemType)
        {
            var spawnPointAcceptedItems = spawnpoint switch
            {
                PredefinedItemSpawnpoint predefinedSpawnpoint => [ predefinedSpawnpoint.TargetItem ],
                RandomItemSpawnpoint randomSpawnpoint => randomSpawnpoint.Presets.Select(p => p.TargetItem),
                RandomItemGroupSpawnpoint randomGroupSpawnpoint => randomGroupSpawnpoint.Presets.SelectMany(g => g.Items).Select(p => p.TargetItem),
                _ => UnsupportedSpawnpointDiagnostics.EmptyItems(spawnpoint)
            };
            
            return spawnPointAcceptedItems.Any(i => i == itemType);
        }

        public static int PresetsCount(this ItemSpawnpointBase spawnpoint)
        {
            var acceptedItemsCount = spawnpoint switch
            {
                PredefinedItemSpawnpoint predefinedSpawnpoint => 1,
                RandomItemSpawnpoint randomSpawnpoint => randomSpawnpoint.Presets.Length,
                RandomItemGroupSpawnpoint randomGroupSpawnpoint => randomGroupSpawnpoint.Presets
                    .SelectMany(g => g.Items)
                    .Select(p => p.TargetItem)
                    .Distinct()
                    .Count(),
                _ => UnsupportedSpawnpointDiagnostics.ZeroPresets(spawnpoint)
            };

            return acceptedItemsCount;
        }
    }

    internal static class UnsupportedSpawnpointDiagnostics
    {
        private static readonly HashSet<Type> WarnedTypes = new();

        public static IEnumerable<Transform> EmptyPositions(ItemSpawnpointBase spawnpoint)
        {
            WarnOnce(spawnpoint);
            return Enumerable.Empty<Transform>();
        }

        public static IEnumerable<ItemType> EmptyItems(ItemSpawnpointBase spawnpoint)
        {
            WarnOnce(spawnpoint);
            return Enumerable.Empty<ItemType>();
        }

        public static int ZeroPresets(ItemSpawnpointBase spawnpoint)
        {
            WarnOnce(spawnpoint);
            return 0;
        }

        private static void WarnOnce(ItemSpawnpointBase spawnpoint)
        {
            var type = spawnpoint?.GetType();
            if (type != null && WarnedTypes.Add(type))
            {
                Debug.LogWarning($"SCPSLBot skipped unsupported item spawnpoint type {type.FullName}.");
            }
        }
    }
}
