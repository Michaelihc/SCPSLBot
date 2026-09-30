using System;

namespace SCPSLBot.AI.FirstPersonControl.Objectives
{
    [Flags]
    internal enum BotObjectiveActions
    {
        None = 0,
        /// <summary>Stand still this tick.</summary>
        Hold = 1 << 0,
        /// <summary>Re-plan the path to the goal.</summary>
        Replan = 1 << 1,
        /// <summary>Penalize the crossing being attempted so the next plan avoids it, then re-plan.</summary>
        AvoidCrossing = 1 << 2,
        /// <summary>Resolve the requested point to a navigable goal again.</summary>
        ResolveGoal = 1 << 3,
    }

    /// <summary>
    /// Phase and recovery decisions for one bot objective: walk to the goal, yield to combat, hold at
    /// the goal or at the end of the reachable path, periodically retry an unreachable goal, and
    /// escalate replans when walking stops getting closer. Pure (no engine types) so the transitions
    /// are unit-testable; FpcBotObjective applies the returned actions.
    /// </summary>
    internal sealed class BotObjectivePolicy
    {
        public const float ArrivalMeters = 0.75f;
        public const float HoldLeashMeters = 2.5f;
        public const float ProgressMeters = 0.5f;
        public const float StallSeconds = 4f;
        public const int AvoidCrossingAtStall = 2;
        public const int ResolveGoalAtStall = 3;
        public const float NoPathGraceSeconds = 1.5f;
        public const float UnreachableRecheckSeconds = 5f;
        public const float MaxEngageRadius = 1000f;
        public const float VerticalToleranceMeters = 1.5f;

        private float lastProgressAt;
        private float bestRemaining;
        private int lastProgressStamp = int.MinValue;
        private int stallsWithoutProgress;
        private float noPathSince = float.NaN;
        private float recheckAt;

        public BotObjectivePolicy(float now)
        {
            Resume(now);
        }

        public BotObjectivePhase Phase { get; private set; }

        /// <summary>Holding short of the goal because the reachable path ends there.</summary>
        public bool AtNearestReachable { get; private set; }

        public int StallCount { get; private set; }
        public int Engagements { get; private set; }

        public static bool IsValidRequest(float x, float y, float z, float engageRadius)
            => Finite(x) && Finite(y) && Finite(z) && Finite(engageRadius)
               && engageRadius >= 0f && engageRadius <= MaxEngageRadius;

        /// <summary>
        /// Distance used for arrival and progress: horizontal, plus any height difference beyond
        /// <see cref="VerticalToleranceMeters"/>, so floor and standing-root goals (about 1 m apart)
        /// both count as reached while a goal one storey up or down does not.
        /// </summary>
        public static float GoalDistance(float horizontalMeters, float verticalMeters)
        {
            var excess = Math.Abs(verticalMeters) - VerticalToleranceMeters;
            return excess > 0f ? horizontalMeters + excess : horizontalMeters;
        }

        /// <summary>Combat claimed this tick.</summary>
        public void NoteEngaged()
        {
            if (Phase == BotObjectivePhase.Engaging)
            {
                return;
            }

            Engagements++;
            Phase = BotObjectivePhase.Engaging;
        }

        /// <summary>
        /// First decision of a tick combat did not claim. <see cref="BotObjectiveActions.Hold"/> means
        /// stand still; otherwise walk toward the goal and report the result to <see cref="ObserveMove"/>.
        /// </summary>
        public BotObjectiveActions BeginTick(float now, float remainingMeters)
        {
            switch (Phase)
            {
                case BotObjectivePhase.Engaging:
                    // Combat moved the bot; the walk back must not be charged to the stall window.
                    Resume(now);
                    return BotObjectiveActions.None;
                case BotObjectivePhase.Holding when AtNearestReachable:
                    if (now < recheckAt)
                    {
                        return BotObjectiveActions.Hold;
                    }

                    // A wall may have opened or the mesh changed since the path was cut short.
                    Resume(now);
                    return BotObjectiveActions.ResolveGoal | BotObjectiveActions.Replan;
                case BotObjectivePhase.Holding:
                    if (remainingMeters <= HoldLeashMeters)
                    {
                        return BotObjectiveActions.Hold;
                    }

                    Resume(now);
                    return BotObjectiveActions.None;
                default:
                    return BotObjectiveActions.None;
            }
        }

        /// <summary>
        /// Second decision of a walking tick, from the navigator's plan toward the goal.
        /// Distances are <see cref="GoalDistance"/> values; <paramref name="pathEndMeters"/> measures to
        /// the last point of that plan.
        /// </summary>
        public BotObjectiveActions ObserveMove(float now, float remainingMeters, bool hasCompletePath,
            bool hasPartialPath, float pathEndMeters, int progressStamp)
        {
            if (remainingMeters <= ArrivalMeters)
            {
                StartHolding(now, atNearestReachable: false);
                return BotObjectiveActions.Hold;
            }

            if (!hasCompletePath && !hasPartialPath)
            {
                // No plan: the first plan is pending, the mesh is being rebuilt, or nothing is
                // reachable beyond the bot's feet. Waiting is not a stall.
                if (float.IsNaN(noPathSince))
                {
                    noPathSince = now;
                }

                lastProgressAt = now;
                if (now - noPathSince < NoPathGraceSeconds)
                {
                    return BotObjectiveActions.None;
                }

                StartHolding(now, atNearestReachable: true);
                return BotObjectiveActions.Hold | BotObjectiveActions.ResolveGoal;
            }

            noPathSince = float.NaN;
            if (pathEndMeters <= ArrivalMeters)
            {
                StartHolding(now, atNearestReachable: true);
                return BotObjectiveActions.Hold;
            }

            if (progressStamp != lastProgressStamp || remainingMeters < bestRemaining - ProgressMeters)
            {
                lastProgressStamp = progressStamp;
                bestRemaining = Math.Min(bestRemaining, remainingMeters);
                lastProgressAt = now;
                stallsWithoutProgress = 0;
                return BotObjectiveActions.None;
            }

            if (now - lastProgressAt < StallSeconds)
            {
                return BotObjectiveActions.None;
            }

            lastProgressAt = now;
            StallCount++;
            stallsWithoutProgress++;
            if (stallsWithoutProgress >= ResolveGoalAtStall)
            {
                stallsWithoutProgress = 0;
                return BotObjectiveActions.ResolveGoal | BotObjectiveActions.Replan;
            }

            return stallsWithoutProgress >= AvoidCrossingAtStall
                ? BotObjectiveActions.AvoidCrossing
                : BotObjectiveActions.Replan;
        }

        private void Resume(float now)
        {
            Phase = BotObjectivePhase.Moving;
            AtNearestReachable = false;
            lastProgressAt = now;
            bestRemaining = float.PositiveInfinity;
            stallsWithoutProgress = 0;
            noPathSince = float.NaN;
        }

        private void StartHolding(float now, bool atNearestReachable)
        {
            Phase = BotObjectivePhase.Holding;
            AtNearestReachable = atNearestReachable;
            recheckAt = now + UnreachableRecheckSeconds;
            stallsWithoutProgress = 0;
            noPathSince = float.NaN;
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
