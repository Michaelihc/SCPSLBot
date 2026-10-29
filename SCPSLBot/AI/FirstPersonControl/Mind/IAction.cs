namespace SCPSLBot.AI.FirstPersonControl.Mind
{
    internal enum ActionStatus
    {
        /// <summary>Still working toward its effect; tick again next frame.</summary>
        Running,
        /// <summary>Done; the runner replans on the same frame.</summary>
        Completed,
        /// <summary>Cannot succeed (rejected, unreachable, nothing to interact with); the runner remembers the failure and replans.</summary>
        Failed,
    }

    internal interface IAction
    {
        void SetEnabledByBeliefs(FpcMind fpcMind);
        void SetImpactsBeliefs(FpcMind fpcMind);

        /// <summary>Planning cost of this step from the bot's current state; evaluated at replan time only.</summary>
        float Cost { get; }

        /// <summary>Seconds the action may run (while the objective is active) before the runner fails it; read once when it starts.</summary>
        float TimeoutSeconds { get; }

        ActionStatus Tick();

        /// <summary>The runner selected this action; forget per-run progress.</summary>
        void Reset();

        /// <summary>The action stops running for any reason (completed, failed, replaced).</summary>
        void Stop();

        /// <summary>
        /// The action failed (it reported <see cref="ActionStatus.Failed"/>, timed out, or locomotion
        /// gave up on its destination). Return true when the action remembered the failed target
        /// itself and may still pursue another one; false makes the runner cool the action down.
        /// </summary>
        bool OnFailed();
    }
}
