using InventorySystem.Items.Pickups;
using MapGeneration.Distributors;
using SCPSLBot.Performance;
using System.Collections.Generic;
using UnityEngine;

namespace SCPSLBot.AI.FirstPersonControl.Brain.Human
{
    internal readonly struct PickupEntry
    {
        public PickupEntry(ItemPickupBase pickup, Vector3 position, ItemType type)
        {
            Pickup = pickup;
            Position = position;
            Type = type;
        }

        public ItemPickupBase Pickup { get; }

        /// <summary>Position when the snapshot was taken.</summary>
        public Vector3 Position { get; }

        public ItemType Type { get; }
    }

    /// <summary>
    /// Process-wide, low-rate views of what lies around for the taking. The game (through LabAPI's
    /// pickup registry and <see cref="SpawnableStructure.AllInstances"/>) already tracks pickups
    /// and lockers, so one shared snapshot per refresh replaces a scene scan per bot; each bot
    /// filters the list by distance itself.
    /// </summary>
    internal static class LootWorld
    {
        private const float PickupRefreshesPerSecond = 1f;
        private const float LockerRefreshesPerSecond = 0.2f;

        private static readonly FixedRateRefreshGate PickupGate = new(PickupRefreshesPerSecond);
        private static readonly FixedRateRefreshGate LockerGate = new(LockerRefreshesPerSecond);
        private static readonly List<PickupEntry> PickupBuilder = new(256);
        private static readonly List<Locker> LockerBuilder = new(64);
        private static PickupEntry[] pickups = System.Array.Empty<PickupEntry>();
        private static Locker[] lockers = System.Array.Empty<Locker>();

        public static PickupEntry[] GetPickups(float now)
        {
            if (!PickupGate.TryAcquire(now))
            {
                return pickups;
            }

            PickupBuilder.Clear();
            foreach (var pickup in LabApi.Features.Wrappers.Pickup.Dictionary.Keys)
            {
                if (pickup == null || pickup.Info.Locked)
                {
                    continue;
                }

                PickupBuilder.Add(new PickupEntry(pickup, pickup.Position, pickup.Info.ItemId));
            }

            pickups = PickupBuilder.ToArray();
            return pickups;
        }

        public static Locker[] GetLockers(float now)
        {
            if (!LockerGate.TryAcquire(now))
            {
                return lockers;
            }

            LockerBuilder.Clear();
            foreach (var structure in SpawnableStructure.AllInstances)
            {
                if (structure is Locker locker && locker != null)
                {
                    LockerBuilder.Add(locker);
                }
            }

            lockers = LockerBuilder.ToArray();
            return lockers;
        }
    }
}
