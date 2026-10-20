using SCPSLBot.AI.FirstPersonControl.Mind.Spacial;

namespace SCPSLBot.AI.FirstPersonControl.Mind.Escape
{
    /// <summary>
    /// Walks into the escape zone once every border on the way is passable, from whichever zone
    /// the bot is in: the navmesh plans the whole route and locomotion crosses checkpoints, gates
    /// and elevators. Without that access the planner first works on the next zone border.
    /// </summary>
    internal class GoToEscapeLocation : GoTo<FacilityEscapeLocation>
    {
        private const float ArrivalMeters = 1.5f;
        // Escaping changes the role at once; standing in the zone this long means it did not apply.
        private const float NoEscapeSeconds = 8f;

        private float arrivedAt = -1f;

        public GoToEscapeLocation(FpcBotPlayer botPlayer) : base(botPlayer)
        {
        }

        public override void SetEnabledByBeliefs(FpcMind fpcMind)
        {
            fpcMind.ActionEnabledBy<EscapeRouteAccess>(this, b => b.CanReachExit);

            base.SetEnabledByBeliefs(fpcMind);
        }

        public override void SetImpactsBeliefs(FpcMind fpcMind)
        {
            fpcMind.ActionImpacts<PlayerEscaped>(this);
        }

        public override float Weight { get; } = 1f;

        public override void Reset()
        {
            arrivedAt = -1f;
            base.Reset();
        }

        protected override ActionStatus TickTowardsTarget()
        {
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

            return UnityEngine.Time.time - arrivedAt > NoEscapeSeconds ? ActionStatus.Failed : ActionStatus.Running;
        }

        public override string ToString()
        {
            return $"{nameof(GoToEscapeLocation)}";
        }
    }
}
