using InventorySystem.Items.Pickups;
using SCPSLBot.AI.FirstPersonControl.Perception.Senses;
using System.Collections.Generic;
using UnityEngine;

namespace SCPSLBot.AI.FirstPersonControl.Mind.Item.Beliefs
{
    /// <summary>
    /// Pickups matching <see cref="ItemLocations{C}.Criteria"/> the bot has seen and not yet seen
    /// gone. Tracked by pickup reference (<see cref="Location.Positions"/> mirrors their last
    /// seen positions index for index), so a pickup that rolled a little is the same entry and
    /// actions can ask for the pickup itself instead of matching positions.
    /// </summary>
    internal class ItemSightedLocation<C> : ItemLocations<C> where C : IItemBeliefCriteria
    {
        private const float MovedSqrMeters = 0.3f * 0.3f;
        private const float CloseAbsentMeters = 1.5f;

        private readonly ItemsWithinSightSense itemsSightSense;
        private readonly List<ItemPickupBase> pickups = new();
        private readonly List<bool> sensedThisPass = new();

        public ItemSightedLocation(C criteria, ItemsWithinSightSense itemsSightSense) : base(criteria)
        {
            this.itemsSightSense = itemsSightSense;
            this.itemsSightSense.OnSensedItemWithinSight += ProcessSensedItem;
            this.itemsSightSense.OnAfterSensedItemsWithinSight += HandleAfterSensedItems;
        }

        /// <summary>The pickup last seen at <paramref name="position"/>, if it still exists.</summary>
        public bool TryGetPickup(Vector3 position, out ItemPickupBase pickup)
        {
            for (var index = 0; index < Positions.Count; index++)
            {
                if ((Positions[index] - position).sqrMagnitude <= 0.01f)
                {
                    pickup = pickups[index];
                    return pickup != null;
                }
            }

            pickup = null;
            return false;
        }

        private void ProcessSensedItem(ItemPickupBase item)
        {
            if (!Criteria.EvaluateItem(item))
            {
                return;
            }

            var position = item.Position;
            for (var index = 0; index < pickups.Count; index++)
            {
                if (!ReferenceEquals(pickups[index], item))
                {
                    continue;
                }

                sensedThisPass[index] = true;
                if ((Positions[index] - position).sqrMagnitude > MovedSqrMeters)
                {
                    Positions[index] = position;
                    InvokeOnUpdate();
                }

                return;
            }

            pickups.Add(item);
            sensedThisPass.Add(true);
            Positions.Add(position);
            InvokeOnUpdate();
        }

        private void HandleAfterSensedItems()
        {
            var changed = false;
            for (var index = pickups.Count - 1; index >= 0; index--)
            {
                var sensed = sensedThisPass[index];
                sensedThisPass[index] = false;
                if (sensed)
                {
                    continue;
                }

                var pickup = pickups[index];
                var position = Positions[index];
                // Destroyed (taken) pickups go at once; others once the bot looks where the
                // pickup was and it is not there.
                var gone = pickup == null
                           || pickup.Info.Locked
                           || (itemsSightSense.IsPositionWithinFov(position)
                               && (itemsSightSense.GetDistanceToPosition(position) < CloseAbsentMeters
                                   || !itemsSightSense.IsPositionObstructed(position)));
                if (!gone)
                {
                    continue;
                }

                pickups.RemoveAt(index);
                sensedThisPass.RemoveAt(index);
                Positions.RemoveAt(index);
                changed = true;
            }

            if (changed)
            {
                InvokeOnUpdate();
            }
        }

        public override string ToString()
        {
            return $"{nameof(ItemSightedLocation<C>)}({this.Criteria}): {this.Positions.Count}";
        }
    }
}
