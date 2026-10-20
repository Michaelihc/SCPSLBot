namespace SCPSLBot.AI.FirstPersonControl.Brain
{
    /// <summary>
    /// One non-combat activity a bot can pursue (explore, loot, heal, revive a corpse, run a GOAP
    /// objective). <see cref="BotBrain"/> asks every behavior for a utility score at a low decision
    /// rate and ticks only the winner every frame. Combat and explicit orders pre-empt the brain.
    /// </summary>
    internal interface IBotBehavior
    {
        string Name { get; }

        /// <summary>
        /// Utility of running this behavior now; 0 or less means not applicable. Called at
        /// <see cref="BotBrain.DecisionIntervalSeconds"/>, never per frame, so it may do bounded
        /// work (a cached lookup, a few distance checks) but must not path-plan or scan the scene.
        /// Use the <see cref="BehaviorPriority"/> bands so unrelated behaviors compare sensibly.
        /// </summary>
        float Evaluate();

        void OnEnter();

        void OnExit();

        /// <summary>
        /// Runs every frame while this behavior is active and drives movement/look/interaction.
        /// Return false when the behavior finished or can no longer make progress; the brain then
        /// re-arbitrates on the same frame instead of waiting for the next decision tick.
        /// </summary>
        bool Tick();
    }

    /// <summary>A behavior with a short live status for <c>bot_debug</c> and the spectator overlay.</summary>
    internal interface IDescribedBehavior
    {
        /// <summary>Null when there is nothing to add to the behavior name.</summary>
        string Detail { get; }
    }

    /// <summary>Utility bands. A behavior returns a value inside its band, scaled by urgency.</summary>
    internal static class BehaviorPriority
    {
        /// <summary>Wandering/patrolling when nothing else applies.</summary>
        public const float Idle = 0.1f;
        /// <summary>Long-running objective (escape, reach a zone) planned by GOAP.</summary>
        public const float Objective = 0.4f;
        /// <summary>Opportunistic: grab a useful item nearby.</summary>
        public const float Opportunity = 0.5f;
        /// <summary>Role ability out of combat: SCP-049 revive, SCP-049-2 consume, SCP-3114 disguise, SCP-939 ambush.</summary>
        public const float RoleAbility = 0.6f;
        /// <summary>Self-preservation: heal when hurt and safe.</summary>
        public const float Survival = 0.7f;
    }
}
