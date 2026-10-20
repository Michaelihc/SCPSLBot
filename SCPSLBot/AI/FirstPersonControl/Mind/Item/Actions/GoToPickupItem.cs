using InventorySystem.Items.Pickups;
using SCPSLBot.AI.FirstPersonControl.Mind.Item.Beliefs;
using System;
using UnityEngine;

namespace SCPSLBot.AI.FirstPersonControl.Mind.Item.Actions
{
    /// <summary>Walks to the nearest seen pickup matching the criteria and picks it up like a player (<see cref="BotPickupSession"/>).</summary>
    internal class GoToPickupItem<C> : GoTo<ItemSightedLocation<C>, C> where C : IItemBeliefCriteria, IEquatable<C>
    {
        private const float RejectedPickupSeconds = 90f;

        private readonly BotPickupSession session;
        private readonly BotPickupMemory pickupMemory;

        public GoToPickupItem(C criteria, FpcBotPlayer botPlayer) : base(criteria, botPlayer)
        {
            session = new BotPickupSession(botPlayer);
            pickupMemory = BotPickupMemory.For(botPlayer);
        }

        public override void SetImpactsBeliefs(FpcMind fpcMind)
        {
            fpcMind.ActionImpacts<ItemInInventory<C>>(this, b => b.Criteria.Equals(Criteria));
        }

        public override float Weight { get; } = 1f;

        protected override float ScoreTarget(Vector3 position, int index)
        {
            if (location.TryGetPickup(position, out var pickup) && pickupMemory.IsBlocked(pickup))
            {
                return float.PositiveInfinity;
            }

            return base.ScoreTarget(position, index);
        }

        public override void Stop()
        {
            session.Cancel();
            base.Stop();
        }

        protected override ActionStatus TickTowardsTarget()
        {
            if (!location.TryGetPickup(Target, out var pickup))
            {
                return ActionStatus.Completed;
            }

            session.Begin(pickup);
            switch (session.Tick())
            {
                case PickupProgress.PickedUp:
                    return ActionStatus.Completed;
                case PickupProgress.Rejected:
                    if (BotLog.Verbose) Debug.LogWarning($"Pickup rejected for item {pickup} by {botPlayer}.");
                    pickupMemory.Block(session.Serial, RejectedPickupSeconds);
                    return ActionStatus.Failed;
                default:
                    return ActionStatus.Running;
            }
        }

        public override string ToString()
        {
            return $"{nameof(GoToPickupItem<C>)}({this.Criteria})";
        }
    }
}
