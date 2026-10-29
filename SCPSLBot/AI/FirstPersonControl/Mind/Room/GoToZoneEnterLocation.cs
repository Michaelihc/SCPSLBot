using MapGeneration;
using SCPSLBot.AI.FirstPersonControl.Mind.Room.Beliefs;
using SCPSLBot.AI.FirstPersonControl.Mind.Spacial;
using System;

namespace SCPSLBot.AI.FirstPersonControl.Mind.Room
{
    /// <summary>
    /// Crosses one zone border (nearest checkpoint or gate). Preconditions, in the order the
    /// planner works on them: be in <see cref="FromZone"/>, be able to pass the border (keycard
    /// or a gate held open), be able to pass every border beyond it on the way out, know an
    /// entry point, no unbreakable window on the way.
    /// <para>
    /// The onward borders keep the plan the same on both sides of a border: without them a bot
    /// that cannot leave the next zone entered it, planned the missing keycard (often SCP-914,
    /// back the way it came), stepped back over the border and planned to enter again.
    /// </para>
    /// </summary>
    internal class GoToZoneEnterLocation : GoTo<ZoneEnterLocation>
    {
        private const float ArrivalMeters = 1.25f;
        // Standing at the entry point this long without the zone belief changing means the
        // crossing did not happen (or the bot is off the mesh there).
        private const float ArrivedWithoutZoneSeconds = 3f;

        private float arrivedAt = -1f;

        public FacilityZone Zone { get; }
        public FacilityZone FromZone { get; }

        private ZoneWithin zoneWithin;

        private readonly (FacilityZone From, FacilityZone To)[] onwardBorders;

        public GoToZoneEnterLocation(FacilityZone zone, FacilityZone fromZone, (FacilityZone From, FacilityZone To)[] onwardBorders, FpcBotPlayer botPlayer) : base(botPlayer)
        {
            Zone = zone;
            FromZone = fromZone;
            this.onwardBorders = onwardBorders ?? Array.Empty<(FacilityZone, FacilityZone)>();
        }

        public override void SetEnabledByBeliefs(FpcMind fpcMind)
        {
            zoneWithin = fpcMind.ActionEnabledBy<ZoneWithin, FacilityZone?>(this, b => FromZone, b => b.Zone);
            fpcMind.ActionEnabledBy<ZoneAccess>(this, b => b.From == FromZone && b.To == Zone, b => b.CanPass);
            foreach (var (from, to) in onwardBorders)
            {
                fpcMind.ActionEnabledBy<ZoneAccess>(this, b => b.From == from && b.To == to, b => b.CanPass);
            }

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

            if (!MoveToTarget(ArrivalMeters))
            {
                arrivedAt = -1f;
                return ActionStatus.Running;
            }

            botPlayer.Locomotion.Stop();
            if (arrivedAt < 0f)
            {
                arrivedAt = UnityEngine.Time.time;
            }

            return UnityEngine.Time.time - arrivedAt > ArrivedWithoutZoneSeconds ? ActionStatus.Failed : ActionStatus.Running;
        }

        public override void Reset()
        {
            arrivedAt = -1f;
            base.Reset();
        }

        public override string ToString()
        {
            return $"{nameof(GoToZoneEnterLocation)}({Zone} from {FromZone})";
        }
    }
}
