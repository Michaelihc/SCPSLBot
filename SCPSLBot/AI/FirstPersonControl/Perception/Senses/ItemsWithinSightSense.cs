using InventorySystem.Items.Pickups;
using SCPSLBot.AI.FirstPersonControl.Perception.Senses.Sight;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace SCPSLBot.AI.FirstPersonControl.Perception.Senses
{
    internal class ItemsWithinSightSense : MoveableWithinSightSense<ItemPickupBase>
    {
        // Spawnpoints farther than this are not "checked" by a glance down a corridor.
        private const float SpotCheckMeters = 15f;

        public HashSet<ItemPickupBase> ItemsWithinSight => ComponentsWithinSight;

        public event Action OnBeforeSensedItemsWithinSight;
        public event Action<ItemPickupBase> OnSensedItemWithinSight;
        public event Action OnAfterSensedItemsWithinSight;

        // Several spawn beliefs ask about the same spawnpoints in one pass; one line cast each.
        private readonly Dictionary<Vector3, bool> spotVisibility = new();
        private static readonly Predicate<ItemPickupBase> IsLocked = pickup => pickup == null || pickup.Info.Locked;

        public ItemsWithinSightSense(FpcBotPlayer botPlayer) : base(botPlayer)
        { }

        private LayerMask interactableLayerMask = LayerMask.GetMask("InteractableNoPlayerCollision");
        protected override LayerMask LayerMask => interactableLayerMask;

        /// <summary>
        /// True when <paramref name="position"/> is in front of the bot, within spot-check range
        /// and not hidden behind geometry. Memoized for the current sensing pass.
        /// </summary>
        public bool IsSpotVisible(Vector3 position)
        {
            if (!IsPositionWithinFov(position))
            {
                return false;
            }

            if ((position - BotPlayer.CameraPosition).sqrMagnitude > SpotCheckMeters * SpotCheckMeters)
            {
                return false;
            }

            if (!spotVisibility.TryGetValue(position, out var visible))
            {
                visible = !IsPositionObstructed(position);
                spotVisibility.Add(position, visible);
            }

            return visible;
        }

        public override void ProcessSightSensedItems()
        {
            spotVisibility.Clear();
            ItemsWithinSight.RemoveWhere(IsLocked);

            OnBeforeSensedItemsWithinSight?.Invoke();
            foreach (var item in ItemsWithinSight)
            {
                OnSensedItemWithinSight?.Invoke(item);
            }
            OnAfterSensedItemsWithinSight?.Invoke();
        }
    }
}
