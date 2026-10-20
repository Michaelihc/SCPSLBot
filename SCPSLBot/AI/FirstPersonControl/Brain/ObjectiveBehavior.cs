using PlayerRoles;
using UnityEngine;

namespace SCPSLBot.AI.FirstPersonControl.Brain
{
    /// <summary>
    /// Runs the GOAP mind (EscapeTheFacility) for the roles that have an objective. The mind only
    /// chooses destinations and interactions; doors and elevators on the way are locomotion's job.
    /// Scores <see cref="BehaviorPriority.Objective"/> while the mind has an action to run and 0
    /// while it has no plan, so exploration fills the gaps (and discovers what the mind needs).
    /// </summary>
    internal sealed class ObjectiveBehavior : IBotBehavior
    {
        // A goal locomotion had to abandon (route blocked, stuck ladder exhausted) would otherwise
        // be re-selected at once; explore elsewhere for a while and let beliefs change.
        private const float AbandonCooldownSeconds = 10f;

        private readonly FpcBotPlayer botPlayer;
        private float cooldownUntil;

        public ObjectiveBehavior(FpcBotPlayer botPlayer)
        {
            this.botPlayer = botPlayer;
        }

        public string Name => "objective";

        /// <summary>The GOAP action being executed, for diagnostics.</summary>
        public string Detail => botPlayer.MindRunner.RunningAction?.GetType().Name;

        public static bool AppliesTo(RoleTypeId role) => role is RoleTypeId.ClassD or RoleTypeId.Scientist;

        public float Evaluate()
        {
            if (!AppliesTo(botPlayer.BotHub.PlayerHub.roleManager.CurrentRole.RoleTypeId) || Time.time < cooldownUntil)
            {
                return 0f;
            }

            // Planning is cheap unless a relevant belief changed; it must run while another
            // behavior is active too, or the mind would never find a plan to bid with.
            botPlayer.MindRunner.UpdatePlan();
            return botPlayer.MindRunner.RunningAction != null ? BehaviorPriority.Objective : 0f;
        }

        public void OnEnter()
        {
            botPlayer.Locomotion.DiscardAbandonRequest();
        }

        public void OnExit()
        {
        }

        public bool Tick()
        {
            var mind = botPlayer.MindRunner;
            if (botPlayer.Locomotion.ConsumeAbandonRequest())
            {
                cooldownUntil = Time.time + AbandonCooldownSeconds;
                mind.EvaluateGoalsToActions();
                return false;
            }

            mind.UpdatePlan();
            if (mind.RunningAction == null)
            {
                return false;
            }

            mind.TickRunningAction();
            return true;
        }
    }
}
