namespace SCPSLBot.Navigation.Policy
{
    /// <summary>
    /// Decides when a runtime-navmesh bot recomputes its path. Pure so the replan cadence (which
    /// is what keeps per-bot query cost at "one per replan, not per tick") is unit-testable.
    /// </summary>
    internal sealed class PathReplanPolicy
    {
        // A chased target moves up to a meter between AI ticks; only a real jump replans at once,
        // creeping goals are re-planned at the bounded cadence below.
        public const float GoalMoveReplanMeters = 2f;
        public const float MovingGoalReplanSeconds = 1f;
        public const float FailedPlanRetrySeconds = 1f;

        private bool hasPlan;
        private bool forced;
        private float plannedAt;
        private int plannedGeneration = -1;
        private float failedAt = float.NegativeInfinity;
        private bool lastPlanFailed;

        public bool HasPlan => hasPlan;

        public void Reset()
        {
            hasPlan = false;
            forced = false;
            plannedGeneration = -1;
            lastPlanFailed = false;
            failedAt = float.NegativeInfinity;
        }

        public void Force()
        {
            forced = true;
        }

        /// <summary>A query that failed only because the surface was being rebuilt: plan again next call.</summary>
        public void NoteTransientFailure()
        {
            hasPlan = false;
            forced = true;
            lastPlanFailed = false;
        }

        public void NotePlanned(float now, int generation, bool failed)
        {
            hasPlan = !failed;
            forced = false;
            plannedAt = now;
            plannedGeneration = generation;
            lastPlanFailed = failed;
            failedAt = failed ? now : float.NegativeInfinity;
        }

        /// <summary>
        /// <paramref name="goalDisplacement"/> is how far the requested goal moved since the last
        /// plan; <paramref name="pathInvalid"/> is true when the engine reports the stored path no
        /// longer exists (a tile was rebuilt under it); <paramref name="generation"/> is the navmesh
        /// generation (bake or reconcile) the caller currently sees.
        /// </summary>
        public bool ShouldReplan(float now, float goalDisplacement, bool pathInvalid, int generation)
        {
            if (lastPlanFailed)
            {
                // Retry an unreachable goal only after a cooldown, unless something changed.
                if (forced || goalDisplacement > GoalMoveReplanMeters || generation != plannedGeneration)
                {
                    return true;
                }

                return now - failedAt >= FailedPlanRetrySeconds;
            }

            if (!hasPlan || forced || pathInvalid || generation != plannedGeneration)
            {
                return true;
            }

            if (goalDisplacement > GoalMoveReplanMeters)
            {
                return true;
            }

            // A goal that keeps creeping (a chased player) is re-planned at a bounded cadence.
            return goalDisplacement > 0.05f && now - plannedAt >= MovingGoalReplanSeconds;
        }
    }
}
