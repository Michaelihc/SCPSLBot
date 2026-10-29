using MapGeneration;
using SCPSLBot.AI.FirstPersonControl.Mind.Item.Beliefs;
using SCPSLBot.AI.FirstPersonControl.Mind.Room.Beliefs;
using SCPSLBot.AI.FirstPersonControl.Mind.Spacial;
using System;
using UnityEngine;

namespace SCPSLBot.AI.FirstPersonControl.Mind.Item.Actions
{
    /// <summary>
    /// Last resort while looking for an item: walk into a neighbouring room (least recently
    /// visited first) so new spawnpoints, lockers and pickups come into view.
    /// </summary>
    internal class GoToSearchRoom<C> : GoTo<RoomEnterLocation> where C : IItemBeliefCriteria, IEquatable<C>
    {
        private const float ArrivalMeters = 1.5f;
        private const float SearchedRoomSeconds = 120f;

        public readonly C Criteria;
        public FacilityZone? Zone;

        public GoToSearchRoom(C criteria, FacilityZone zone, FpcBotPlayer botPlayer) : this(criteria, botPlayer)
        {
            this.Zone = zone;
        }

        public GoToSearchRoom(C criteria, FpcBotPlayer botPlayer) : base(botPlayer)
        {
            this.Criteria = criteria;
        }

        public override void SetEnabledByBeliefs(FpcMind fpcMind)
        {
            if (Zone.HasValue)
            {
                fpcMind.ActionEnabledBy<ZoneWithin, FacilityZone?>(this, b => Zone.Value, b => b.Zone);
            }

            base.SetEnabledByBeliefs(fpcMind);
        }

        public override void SetImpactsBeliefs(FpcMind fpcMind)
        {
            fpcMind.ActionImpacts<ItemSightedLocation<C>>(this, b => b.Criteria.Equals(Criteria));
        }

        public override float Weight => 20f;    // to make this action as last resort as possible
        public override float Cost => Weight * 10;

        // Entries are ordered least recently visited first.
        protected override float ScoreTarget(Vector3 position, int index) => index;

        protected override ActionStatus TickTowardsTarget()
        {
            if (!MoveToTarget(ArrivalMeters))
            {
                return ActionStatus.Running;
            }

            MarkTargetDone(SearchedRoomSeconds);
            return ActionStatus.Completed;
        }

        public override string ToString()
        {
            return $"{nameof(GoToSearchRoom<C>)}({this.Criteria}{(Zone.HasValue ? $", {Zone.Value}" : string.Empty)})";
        }
    }
}
