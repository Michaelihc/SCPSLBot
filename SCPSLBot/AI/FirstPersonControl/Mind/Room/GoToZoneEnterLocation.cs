using MapGeneration;
using SCPSLBot.AI.FirstPersonControl.Mind.Room.Beliefs;
using SCPSLBot.AI.FirstPersonControl.Mind.Spacial;
using System;

namespace SCPSLBot.AI.FirstPersonControl.Mind.Room
{
    /// <summary>
    /// Crosses one zone border (nearest checkpoint or gate). Preconditions, in the order the
    /// planner works on them: be in <see cref="FromZone"/>, be able to pass the border (keycard
    /// or a gate held open), know an entry point, no unbreakable window on the way.
    /// </summary>
    internal class GoToZoneEnterLocation : GoTo<ZoneEnterLocation>
    {
        private const float ArrivalMeters = 1.25f;

        public FacilityZone Zone { get; }
        public FacilityZone FromZone { get; }

        private ZoneWithin zoneWithin;

        public GoToZoneEnterLocation(FacilityZone zone, FacilityZone fromZone, FpcBotPlayer botPlayer) : base(botPlayer)
        {
            Zone = zone;
            FromZone = fromZone;
        }

        public override void SetEnabledByBeliefs(FpcMind fpcMind)
        {
            zoneWithin = fpcMind.ActionEnabledBy<ZoneWithin, FacilityZone?>(this, b => FromZone, b => b.Zone);
            fpcMind.ActionEnabledBy<ZoneAccess>(this, b => b.From == FromZone && b.To == Zone, b => b.CanPass);

            base.SetEnabledByBeliefs(fpcMind);
        }

        protected override ZoneEnterLocation SetEnabledByLocation(FpcMind fpcMind, Func<ZoneEnterLocation, bool> currentGetter)
        {
            return fpcMind.ActionEnabledBy<ZoneEnterLocation>(this, b => b.Zone == Zone && b.FromZone == FromZone, currentGetter);
        }

        public override void SetImpactsBeliefs(FpcMind fpcMind)
        {
            fpcMind.ActionImpacts<ZoneWithin, FacilityZone?>(this, b => Zone);
        }

        public override float Weight { get; } = 1f;

        protected override ActionStatus TickTowardsTarget()
        {
            if (zoneWithin.Zone == Zone)
            {
                return ActionStatus.Completed;
            }

            return MoveToTarget(ArrivalMeters) ? ActionStatus.Completed : ActionStatus.Running;
        }

        public override string ToString()
        {
            return $"{nameof(GoToZoneEnterLocation)}({Zone} from {FromZone})";
        }
    }
}
