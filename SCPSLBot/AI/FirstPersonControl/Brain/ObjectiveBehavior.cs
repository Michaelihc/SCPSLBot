using PlayerRoles;

namespace SCPSLBot.AI.FirstPersonControl.Brain
{
    /// <summary>
    /// Runs the GOAP mind (EscapeTheFacility) for the roles that have an objective. The mind only
    /// chooses destinations and interactions; doors and elevators on the way are locomotion's job.
    /// Scores <see cref="BehaviorPriority.Objective"/> while the mind has an action to run and 0
    /// while it has no plan, so exploration fills the gaps (and discovers what the mind needs).
    /// A route locomotion gives up on fails the running action; the mind remembers the failed
    /// target and plans around it.
    /// </summary>
    internal sealed class ObjectiveBehavior : IBotBehavior
    {
        private readonly FpcBotPlayer botPlayer;
        private PlayerRoleBase lifeRole;

        public ObjectiveBehavior(FpcBotPlayer botPlayer)
        {
            this.botPlayer = botPlayer;
        }

        public string Name => "objective";

        /// <summary>The GOAP action being executed, for diagnostics.</summary>
        public string Detail => botPlayer.MindRunner.RunningAction?.ToString();

        public static bool AppliesTo(RoleTypeId role) => role is RoleTypeId.ClassD or RoleTypeId.Scientist;

        public float Evaluate()
        {
            var role = botPlayer.BotHub.PlayerHub.roleManager.CurrentRole;
            if (role == null || !AppliesTo(role.RoleTypeId))
            {
                return 0f;
            }

            var mind = botPlayer.MindRunner;
            if (!ReferenceEquals(role, lifeRole))
            {
                // A new spawn: the previous life's action, failures and paused budget are stale.
                lifeRole = role;
                mind.ResetForNewLife();
            }

            // Planning is cheap unless something is due; it must run while another behavior is
            // active too, or the mind would never find a plan to bid with.
            mind.UpdatePlan();
            return mind.RunningAction != null ? BehaviorPriority.Objective : 0f;
        }

        public void OnEnter()
        {
            botPlayer.Locomotion.DiscardAbandonRequest();
            botPlayer.MindRunner.ResumeRunningAction();
        }

        public void OnExit()
        {
            botPlayer.MindRunner.PauseRunningAction();
        }

        public bool Tick()
        {
            var mind = botPlayer.MindRunner;
            if (botPlayer.Locomotion.ConsumeAbandonRequest())
            {
                mind.FailRunningAction("route abandoned by locomotion");
            }

            mind.UpdatePlan();
            if (mind.RunningAction == null)
            {
                return false;
            }

            mind.TickRunningAction();
            return mind.RunningAction != null;
        }
    }
}
