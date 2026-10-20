using MapGeneration.Distributors;
using SCPSLBot.AI.FirstPersonControl.Mind.Item.Beliefs;
using SCPSLBot.AI.FirstPersonControl.Mind.Spacial;
using System;
using UnityEngine;

namespace SCPSLBot.AI.FirstPersonControl.Mind.Item.Actions
{
    /// <summary>Walks to a spot where a locker may have spawned and looks at it, so its chambers become known.</summary>
    internal class GoToLockerSpawnLocation<C> : GoTo<LockerSpawnsLocation> where C : IItemBeliefCriteria, IEquatable<C>
    {
        private const float ArrivalMeters = 1.75f;
        private const float CheckedSpotSeconds = 600f;

        public C Criteria { get; }
        public StructureType StructureType { get; }
        public GoToLockerSpawnLocation(C criteria, StructureType structureType, FpcBotPlayer botPlayer) : base(botPlayer)
        {
            this.Criteria = criteria;
            this.StructureType = structureType;
        }

        public override void SetImpactsBeliefs(FpcMind fpcMind)
        {
            fpcMind.ActionImpacts<ItemSpawnsInSightedLocker<C>>(this, b => b.Criteria.Equals(Criteria));
        }

        public override float Weight { get; } = 1f / ((1f / 4f) * 3f - Mathf.Pow(1f / 4f, 3f));

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

            MarkTargetDone(CheckedSpotSeconds);
            return ActionStatus.Completed;
        }

        public override string ToString()
        {
            return $"{nameof(GoToLockerSpawnLocation<C>)}({this.Criteria})";
        }
    }
}
