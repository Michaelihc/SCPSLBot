using MapGeneration.Distributors;
using SCPSLBot.AI.FirstPersonControl.Perception.Senses;
using SCPSLBot.Infrastructure;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace SCPSLBot.AI.FirstPersonControl.Mind.Item.Beliefs
{
    /// <summary>
    /// Closed chambers of lockers the bot has seen that can hold one of the spawn item types.
    /// A chamber is remembered from the first sighting until it is seen open (then its contents
    /// are ordinary pickups), so turning away does not forget it. Whether a chamber accepts the
    /// item types is evaluated once per chamber; a sensing pass only walks the lockers in sight.
    /// </summary>
    internal class ItemSpawnsInSightedLocker<C> : ItemLocations<C> where C : IItemBeliefCriteria
    {
        private static readonly HashSet<StructureType> LockerStructureTypes = new() { StructureType.StandardLocker, StructureType.SmallWallCabinet };

        private readonly ItemType[] spawnItemTypes;
        private readonly LockersWithinSightSense lockersSightSense;
        private readonly Dictionary<LockerChamber, bool> acceptsSpawnTypes = new(ManagedReferenceComparer<LockerChamber>.Instance);
        private readonly List<Locker> lockers = new();
        private readonly List<int> chamberIndices = new();
        private int generation = int.MinValue;

        public ItemSpawnsInSightedLocker(C criteria, ItemType[] spawnItemTypes, LockersWithinSightSense lockersSightSense)
            : base(criteria)
        {
            this.spawnItemTypes = spawnItemTypes;
            this.lockersSightSense = lockersSightSense;

            this.lockersSightSense.OnAfterSightSensing += OnAfterSensedLockersWithinSight;
        }

        /// <summary>The locker and chamber index known at <paramref name="position"/>.</summary>
        public bool TryGetChamber(Vector3 position, out Locker locker, out int chamberIndex)
        {
            for (var index = 0; index < Positions.Count; index++)
            {
                if ((Positions[index] - position).sqrMagnitude <= 0.01f)
                {
                    locker = lockers[index];
                    chamberIndex = chamberIndices[index];
                    return locker != null && chamberIndex < locker.Chambers.Length;
                }
            }

            locker = null;
            chamberIndex = -1;
            return false;
        }

        private void OnAfterSensedLockersWithinSight()
        {
            var changed = ResetOnNewMap();

            foreach (var locker in lockersSightSense.LockersWithinSight)
            {
                if (locker == null || !LockerStructureTypes.Contains(locker.StructureType))
                {
                    continue;
                }

                var chambers = locker.Chambers;
                for (var chamberIndex = 0; chamberIndex < chambers.Length; chamberIndex++)
                {
                    var chamber = chambers[chamberIndex];
                    if (chamber == null || acceptsSpawnTypes.ContainsKey(chamber))
                    {
                        continue;
                    }

                    var accepts = Accepts(chamber);
                    acceptsSpawnTypes.Add(chamber, accepts);
                    if (accepts && !chamber.IsOpen)
                    {
                        lockers.Add(locker);
                        chamberIndices.Add(chamberIndex);
                        Positions.Add(chamber.transform.position);
                        changed = true;
                    }
                }
            }

            for (var index = Positions.Count - 1; index >= 0; index--)
            {
                var locker = lockers[index];
                if (locker != null && !locker.Chambers[chamberIndices[index]].IsOpen)
                {
                    continue;
                }

                lockers.RemoveAt(index);
                chamberIndices.RemoveAt(index);
                Positions.RemoveAt(index);
                changed = true;
            }

            if (changed)
            {
                InvokeOnUpdate();
            }
        }

        private bool ResetOnNewMap()
        {
            var current = Navigation.NavigationSystem.Instance.MapGeneration;
            if (current == generation)
            {
                return false;
            }

            generation = current;
            acceptsSpawnTypes.Clear();
            lockers.Clear();
            chamberIndices.Clear();
            if (Positions.Count == 0)
            {
                return false;
            }

            Positions.Clear();
            return true;
        }

        private bool Accepts(LockerChamber chamber)
        {
            foreach (var acceptable in chamber.AcceptableItems)
            {
                if (Array.IndexOf(spawnItemTypes, acceptable) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        public override string ToString()
        {
            return $"{nameof(ItemSpawnsInSightedLocker<C>)}({this.Criteria}): {this.Positions.Count}";
        }
    }
}
