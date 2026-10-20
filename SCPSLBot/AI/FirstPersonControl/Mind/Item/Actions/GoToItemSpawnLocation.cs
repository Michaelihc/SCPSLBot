using SCPSLBot.AI.FirstPersonControl.Mind.Item.Beliefs;
using System;
using UnityEngine;

namespace SCPSLBot.AI.FirstPersonControl.Mind.Item.Actions
{
    /// <summary>Walks to an item spawnpoint that may hold a matching item and looks at it; the sighting beliefs take it from there.</summary>
    internal class GoToItemSpawnLocation<C> : GoTo<ItemSpawnsLocation<C>, C> where C : IItemBeliefCriteria, IEquatable<C>
    {
        private const float ArrivalMeters = 1.75f;
        private const float CheckedSpotSeconds = 600f;

        public GoToItemSpawnLocation(C criteria, FpcBotPlayer botPlayer) : base(criteria, botPlayer)
        {
        }

        public override void SetImpactsBeliefs(FpcMind fpcMind)
        {
            fpcMind.ActionImpacts<ItemSightedLocation<C>>(this, b => b.Criteria.Equals(Criteria));
        }

        public override float Weight => HasTarget() && location.ItemSpawnProbability.TryGetValue(Target, out var probability) && probability > 0f
            ? 1f / probability
            : 1f;

        protected override ActionStatus TickTowardsTarget()
        {
            if (!MoveToTarget(ArrivalMeters))
            {
                return ActionStatus.Running;
            }

            if (!LookAt(Target))
            {
                return ActionStatus.Running;
            }

            // Looked at it: whatever lies there is now seen (or the spot is empty).
            MarkTargetDone(CheckedSpotSeconds);
            return ActionStatus.Completed;
        }

        public override string ToString()
        {
            return $"{nameof(GoToItemSpawnLocation<C>)}({this.Criteria})";
        }
    }
}
