using SCPSLBot.AI.FirstPersonControl.Mind.Item;
using SCPSLBot.AI.FirstPersonControl.Mind.Item.Actions;
using SCPSLBot.AI.FirstPersonControl.Mind.Item.Beliefs;
using System;
using UnityEngine;

namespace SCPSLBot.AI.FirstPersonControl.Mind.Scp914
{
    /// <summary>Walks to the output chamber and looks at the upgraded item so the sighting beliefs pick it up.</summary>
    internal class GoToItemInOutakeChamber<C> : GoTo<ItemsInOutakeChamber, IItemBeliefCriteria> where C : IItemBeliefCriteria, IEquatable<C>
    {
        private const float ArrivalMeters = 1.5f;
        private const float LookedSeconds = 10f;

        public GoToItemInOutakeChamber(C criteria, FpcBotPlayer botPlayer) : base(criteria, botPlayer)
        {
        }

        private Scp914Location scp914Location;

        public override void SetEnabledByBeliefs(FpcMind fpcMind)
        {
            this.scp914Location = fpcMind.GetBelief<Scp914Location>();

            base.SetEnabledByBeliefs(fpcMind);
        }

        public override void SetImpactsBeliefs(FpcMind fpcMind)
        {
            fpcMind.ActionImpacts<ItemSightedLocation<C>>(this, b => b.Criteria.Equals(this.Criteria));
        }

        public override float Weight { get; } = 1f;

        protected override ActionStatus TickTowardsTarget()
        {
            if (!scp914Location.OutakeChamberPosition.HasValue)
            {
                return ActionStatus.Failed;
            }

            var outakeChamberPosition = scp914Location.OutakeChamberPosition.Value;
            var arrived = Vector3.Distance(outakeChamberPosition, botPlayer.CameraPosition) <= ArrivalMeters;
            if (arrived)
            {
                botPlayer.Locomotion.Stop();
            }
            else
            {
                botPlayer.MoveToPosition(outakeChamberPosition);
            }

            if (!LookAt(Target, 5f) || !arrived)
            {
                return ActionStatus.Running;
            }

            MarkTargetDone(LookedSeconds);
            return ActionStatus.Completed;
        }

        public override string ToString()
        {
            return $"{nameof(GoToItemInOutakeChamber<C>)}({this.Criteria})";
        }
    }
}
