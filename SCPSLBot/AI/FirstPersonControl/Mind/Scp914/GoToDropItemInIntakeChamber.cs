using SCPSLBot.AI.FirstPersonControl.Mind.Item;
using SCPSLBot.AI.FirstPersonControl.Mind.Item.Beliefs;
using SCPSLBot.AI.FirstPersonControl.Mind.Spacial;
using System;
using UnityEngine;

namespace SCPSLBot.AI.FirstPersonControl.Mind.Scp914
{
    internal class GoToDropItemInIntakeChamber<C> : GoTo<Scp914Location> where C : IItemBeliefCriteria, IEquatable<C>
    {
        private const float DropArrivalMeters = 0.4f;
        // Long enough for the machine to run; the upgraded pickup gets a new serial.
        private const float DroppedInputSeconds = 120f;

        public C Criteria { get; }
        public GoToDropItemInIntakeChamber(C criteria, FpcBotPlayer botPlayer) : base(botPlayer)
        {
            this.Criteria = criteria;
        }

        private ItemInInventory<C> itemInInventoryBelief;
        private ItemInIntakeChamber<C> itemInIntakeChamber;

        public override void SetEnabledByBeliefs(FpcMind fpcMind)
        {
            this.itemInInventoryBelief = fpcMind.ActionEnabledBy<ItemInInventory<C>>(this, b => b.Criteria.Equals(this.Criteria), b => b.Item);

            base.SetEnabledByBeliefs(fpcMind);
        }

        public override void SetImpactsBeliefs(FpcMind fpcMind)
        {
            this.itemInIntakeChamber = fpcMind.ActionImpacts<ItemInIntakeChamber<C>>(this, b => b.Criteria.Equals(this.Criteria));
        }

        public override float Weight { get; } = 1f;

        public override float Cost => (HasTarget() && location.IntakeChamberPosition.HasValue
            ? Vector3.Distance(location.IntakeChamberPosition.Value, botPlayer.CameraPosition)
            : UnknownDistanceMeters) * Weight;

        protected override ActionStatus TickTowardsTarget()
        {
            if (!location.IntakeChamberPosition.HasValue)
            {
                return ActionStatus.Failed;
            }

            var itemDropPosition = location.IntakeChamberPosition.Value;
            var playerPosition = botPlayer.PlayerPosition with { y = itemDropPosition.y };

            if (Vector3.Distance(playerPosition, itemDropPosition) > DropArrivalMeters)
            {
                botPlayer.MoveToPosition(itemDropPosition);
                return ActionStatus.Running;
            }

            var itemToDrop = itemInInventoryBelief.Item;
            if (itemToDrop == null)
            {
                return ActionStatus.Failed;
            }

            botPlayer.Locomotion.Stop();
            // Neither this mind nor the loot behavior may take the input back out of the chamber.
            BotPickupMemory.For(botPlayer).Block(itemToDrop.ItemSerial, DroppedInputSeconds);
            itemToDrop.ServerDropItem(true);
            itemInIntakeChamber.Update(playerPosition - itemDropPosition);
            return ActionStatus.Completed;
        }

        public override string ToString()
        {
            return $"{nameof(GoToDropItemInIntakeChamber<C>)}({this.Criteria})";
        }
    }
}
