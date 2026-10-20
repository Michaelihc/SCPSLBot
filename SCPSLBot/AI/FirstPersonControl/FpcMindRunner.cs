using SCPSLBot.AI.FirstPersonControl.Mind;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Profiling;

namespace SCPSLBot.AI.FirstPersonControl
{
    /// <summary>
    /// Plans and executes the GOAP objective. Replans when a belief on the current plan changes,
    /// when the running action completes or fails, every few seconds while idle, and periodically
    /// while running (switching only for a clearly cheaper plan, so near-equal costs do not flap).
    /// Failed actions are remembered: targeted actions skip the failed target themselves, other
    /// actions are cooled down here and skipped by the planner.
    /// </summary>
    internal class FpcMindRunner : FpcMind, IDisposable
    {
        // Belief updates only trigger planning for beliefs on the current plan. With no plan at
        // all that set is empty, so an idle mind must retry on its own (new sightings, a keycard
        // picked up, a zone entered) instead of waiting for the next role change.
        private const float IdleReplanSeconds = 3f;
        // Costs are distances from where the bot stood at the last replan; refresh them while
        // walking and pick up blacklist expiries and beliefs that are not on the current plan.
        private const float RunningReplanSeconds = 5f;
        // A periodic replan replaces a still-enabled running action only when the new plan costs
        // less than this share of the running step alone (a lower bound of its whole plan).
        private const float SwitchCostRatio = 0.6f;
        private const float ActionCooldownBaseSeconds = 20f;
        private const float ActionCooldownMaxSeconds = 120f;
        private const float MinResumeBudgetSeconds = 5f;

        private float nextIdleReplanAt;
        private float nextRunningReplanAt;
        private float runningDeadline;
        private float pausedBudget = -1f;
        // True while the owning behavior ticks the running action; budgets only run down then.
        private bool executing;

        public IAction RunningAction { get; private set; }
        public float RunningActionCost { get; private set; }

        /// <summary>How the last action ended, for diagnostics.</summary>
        public string LastOutcome { get; private set; }

        private readonly Dictionary<IAction, (float Until, int Failures)> actionCooldowns = new();

        public readonly HashSet<IBelief> RelevantBeliefs = new();
        private readonly Dictionary<IBelief, Action> beliefUpdateHandlers = new();
        private bool isBeliefsUpdated = false;
        private bool isDisposed;

        public void SubscribeToBeliefUpdates()
        {
            if (isDisposed)
            {
                return;
            }

            foreach (var beliefs in Beliefs.Values)
            {
                foreach (var belief in beliefs)
                {
                    if (beliefUpdateHandlers.ContainsKey(belief))
                    {
                        continue;
                    }

                    var subscribedBelief = belief;
                    Action handler = () => OnBeliefUpdate(subscribedBelief);
                    beliefUpdateHandlers.Add(subscribedBelief, handler);
                    subscribedBelief.OnUpdate += handler;
                }
            }
        }

        public void Dispose()
        {
            if (isDisposed)
            {
                return;
            }

            isDisposed = true;

            foreach (var (belief, handler) in beliefUpdateHandlers)
            {
                try
                {
                    belief.OnUpdate -= handler;
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                }
            }
            beliefUpdateHandlers.Clear();

            foreach (var beliefs in Beliefs.Values)
            {
                foreach (var belief in beliefs)
                {
                    if (belief is not IDisposable disposableBelief)
                    {
                        continue;
                    }

                    try
                    {
                        disposableBelief.Dispose();
                    }
                    catch (Exception exception)
                    {
                        Debug.LogException(exception);
                    }
                }
            }

            RunningAction = null;
            RelevantBeliefs.Clear();
            actionCooldowns.Clear();
        }

        /// <summary>Forces a full replan on the next <see cref="UpdatePlan"/>.</summary>
        public void EvaluateGoalsToActions()
        {
            if (!isDisposed)
            {
                isBeliefsUpdated = true;
            }
        }

        /// <summary>A new life (role change): the running action and remembered failures no longer apply.</summary>
        public void ResetForNewLife()
        {
            if (isDisposed)
            {
                return;
            }

            StopRunning("role changed");
            actionCooldowns.Clear();
            isBeliefsUpdated = true;
        }

        public void Tick()
        {
            UpdatePlan();
            TickRunningAction();
        }

        /// <summary>
        /// Re-plans when a relevant belief changed, the running action ended, or a periodic retry
        /// is due; never ticks the action. Cheap when nothing is due.
        /// </summary>
        public void UpdatePlan()
        {
            if (isDisposed)
            {
                return;
            }

            var now = Time.time;
            var periodic = false;
            if (!isBeliefsUpdated)
            {
                if (RunningAction == null ? now < nextIdleReplanAt : now < nextRunningReplanAt)
                {
                    return;
                }

                periodic = RunningAction != null;
            }

            Profiler.BeginSample($"{nameof(FpcMindRunner)}.{nameof(UpdatePlan)}");
            try
            {
                isBeliefsUpdated = false;
                nextIdleReplanAt = now + IdleReplanSeconds;
                nextRunningReplanAt = now + RunningReplanSeconds;

                IEnumerable<IAction> enabledActions = GetEnabledActionsTowardsGoals();

                SelectActionAndRun(enabledActions, periodic);
            }
            finally
            {
                Profiler.EndSample();
            }
        }

        public void TickRunningAction()
        {
            if (isDisposed || RunningAction == null)
            {
                return;
            }

            Profiler.BeginSample($"{nameof(FpcMindRunner)}.{nameof(TickRunningAction)}");
            try
            {
                if (Time.time > runningDeadline)
                {
                    FailRunningAction("timed out");
                    return;
                }

                ActionStatus status;
                try
                {
                    status = RunningAction.Tick();
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                    status = ActionStatus.Failed;
                }

                switch (status)
                {
                    case ActionStatus.Completed:
                        StopRunning($"{RunningAction}: completed");
                        isBeliefsUpdated = true;
                        break;
                    case ActionStatus.Failed:
                        FailRunningAction("failed");
                        break;
                }
            }
            finally
            {
                Profiler.EndSample();
            }
        }

        /// <summary>The running action cannot reach its effect (for example locomotion gave up on the route).</summary>
        public void FailRunningAction(string reason)
        {
            var action = RunningAction;
            if (action == null)
            {
                return;
            }

            bool handled;
            try
            {
                handled = action.OnFailed();
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                handled = false;
            }

            if (!handled)
            {
                var failures = actionCooldowns.TryGetValue(action, out var previous) ? previous.Failures + 1 : 1;
                var seconds = Mathf.Min(ActionCooldownMaxSeconds, ActionCooldownBaseSeconds * (1 << Mathf.Min(failures - 1, 3)));
                actionCooldowns[action] = (Time.time + seconds, failures);
            }

            if (BotLog.Verbose) Debug.Log($"[Mind] {action} {reason}");
            StopRunning($"{action}: {reason}");
            isBeliefsUpdated = true;
        }

        /// <summary>
        /// The objective stops being the active behavior: the running action lets go of what it
        /// was doing (a pickup search, a locker, a held keycard) and its time budget freezes.
        /// </summary>
        public void PauseRunningAction()
        {
            if (!executing)
            {
                return;
            }

            executing = false;
            if (RunningAction == null)
            {
                return;
            }

            pausedBudget = Mathf.Max(0f, runningDeadline - Time.time);
            try
            {
                RunningAction.Stop();
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }
        }

        /// <summary>The objective is active again: the running action starts over with the budget it had left.</summary>
        public void ResumeRunningAction()
        {
            var wasExecuting = executing;
            executing = true;
            if (RunningAction != null && !wasExecuting)
            {
                try
                {
                    RunningAction.Reset();
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                }

                if (pausedBudget >= 0f)
                {
                    runningDeadline = Time.time + Mathf.Max(pausedBudget, MinResumeBudgetSeconds);
                }
            }

            pausedBudget = -1f;
        }

        private void OnBeliefUpdate(IBelief updatedBelief)
        {
            if (!RelevantBeliefs.Contains(updatedBelief))
            {
                if (BotLog.Verbose) Debug.Log($"[I] Belief updated: {updatedBelief}");
                return;
            }

            isBeliefsUpdated = true;
            if (BotLog.Verbose) Debug.Log($"[R] Belief updated: {updatedBelief}");
        }

        private bool IsCoolingDown(IAction action)
        {
            return actionCooldowns.Count > 0
                   && actionCooldowns.TryGetValue(action, out var cooldown)
                   && Time.time < cooldown.Until;
        }

        private bool IsEnabled(IAction action)
        {
            foreach (var belief in BeliefsEnablingActions[action])
            {
                if (!belief.IsEnabledAction(action))
                {
                    return false;
                }
            }

            return true;
        }

        private void StartRunning(IAction action, float cost)
        {
            RunningAction = action;
            RunningActionCost = cost;
            action.Reset();
            var budget = Mathf.Max(1f, action.TimeoutSeconds);
            if (executing)
            {
                runningDeadline = Time.time + budget;
                pausedBudget = -1f;
            }
            else
            {
                // Selected while another behavior runs: the clock starts when the objective does.
                runningDeadline = float.MaxValue;
                pausedBudget = budget;
            }
        }

        private void StopRunning(string outcome)
        {
            var action = RunningAction;
            RunningAction = null;
            RunningActionCost = 0f;
            pausedBudget = -1f;
            if (action == null)
            {
                return;
            }

            LastOutcome = outcome;
            try
            {
                action.Stop();
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }
        }

        #region Action Finding

        public readonly Dictionary<IAction, float> VisitedActionsTotalCosts = new();
        private readonly Dictionary<IAction, float> remainingActionsToExplore = new();

        public readonly Dictionary<IBelief, IAction> VisitedActionsEnabledBy = new();
        public readonly Dictionary<IBelief, IGoal> VisitedGoalsEnabledBy = new();

        public readonly Dictionary<IAction, IAction> VisitedActionsImpactedBy = new();
        public readonly Dictionary<IAction, IGoal> VisitedGoalsImpactedBy = new();

        public readonly Dictionary<IAction, IAction> RelevantActionsImpactingActions = new();
        public readonly Dictionary<IGoal, IAction> RelevantActionsImpactingGoals = new();

        private IEnumerable<IAction> GetEnabledActionsTowardsGoals()
        {
            Profiler.BeginSample($"{nameof(FpcMindRunner)}.{nameof(GetEnabledActionsTowardsGoals)}");
            try
            {
                RelevantBeliefs.Clear();

                foreach (var goal in BeliefsEnablingGoals.Keys)
                {
                    VisitedGoalsEnabledBy.Clear();
                    VisitedActionsEnabledBy.Clear();
                    VisitedActionsImpactedBy.Clear();
                    VisitedGoalsImpactedBy.Clear();

                    foreach (var enabledAction in FindEnabledActions(goal))
                    {
                        RelevantActionsImpactingActions.Clear();
                        RelevantActionsImpactingGoals.Clear();

                        var actionImpacting = enabledAction;

                        while (VisitedActionsImpactedBy.TryGetValue(actionImpacting, out var actionImpactedBy))
                        {
                            RelevantActionsImpactingActions[actionImpactedBy] = actionImpacting;
                            foreach (var visitedBelief in BeliefsEnablingActions[actionImpacting])
                            {
                                if (VisitedActionsEnabledBy.ContainsKey(visitedBelief))
                                {
                                    RelevantBeliefs.Add(visitedBelief);
                                }
                            }

                            actionImpacting = actionImpactedBy;
                        }

                        RelevantActionsImpactingGoals[goal] = actionImpacting;
                        foreach (var visitedBelief in BeliefsEnablingActions[actionImpacting])
                        {
                            if (VisitedActionsEnabledBy.ContainsKey(visitedBelief))
                            {
                                RelevantBeliefs.Add(visitedBelief);
                            }
                        }

                        yield return enabledAction;
                        break;
                    }
                }
            }
            finally
            {
                Profiler.EndSample();
            }
        }

        private IEnumerable<IAction> FindEnabledActions(IGoal goal)
        {
            VisitedActionsTotalCosts.Clear();
            remainingActionsToExplore.Clear();

            foreach (var b in BeliefsEnablingGoals[goal])
            {
                VisitedGoalsEnabledBy[b] = goal;

                if (b.EvaluateEnabling(goal))
                {
                    continue;
                }

                ProcessActionsImpacting(b, goal);
            }

            while (remainingActionsToExplore.Count > 0)
            {
                IAction actionImpacting = null;
                var lowestCost = float.PositiveInfinity;
                foreach (var candidate in remainingActionsToExplore)
                {
                    if (candidate.Value < lowestCost)
                    {
                        lowestCost = candidate.Value;
                        actionImpacting = candidate.Key;
                    }
                }

                if (actionImpacting == null)
                {
                    // Only unaffordable (infinite cost) actions remain.
                    yield break;
                }

                remainingActionsToExplore.Remove(actionImpacting);

                if (IsActionEnabled(actionImpacting))
                {
                    yield return actionImpacting;
                }
            }
        }

        private void ProcessActionsImpacting(IBelief belief, IGoal goalToEnable)
        {
            foreach (var actionImpacting in ActionsImpactingBeliefs[belief])
            {
                if (IsCoolingDown(actionImpacting))
                {
                    continue;
                }

                VisitedGoalsImpactedBy[actionImpacting] = goalToEnable;

                if (!belief.CanImpactedByAction(actionImpacting, goalToEnable))
                {
                    continue;
                }

                var actionImpactingCost = actionImpacting.Cost;
                remainingActionsToExplore[actionImpacting] = actionImpactingCost;
                VisitedActionsTotalCosts[actionImpacting] = actionImpactingCost;
            }
        }

        /// <summary>
        /// True when every precondition holds. Otherwise only the first unsatisfied precondition
        /// is expanded (its impacting actions queued): preconditions are ordered by the actions so
        /// that the one to work on first comes first.
        /// </summary>
        private bool IsActionEnabled(IAction actionToEnable)
        {
            foreach (var b in BeliefsEnablingActions[actionToEnable])
            {
                VisitedActionsEnabledBy[b] = actionToEnable;

                if (b.IsEnabledAction(actionToEnable))
                {
                    continue;
                }

                ProcessActionsImpacting(b, actionToEnable);
                return false;
            }

            return true;
        }

        private void ProcessActionsImpacting(IBelief belief, IAction actionToEnable)
        {
            var actionToEnableCostToGoal = VisitedActionsTotalCosts[actionToEnable];

            foreach (var actionImpacting in ActionsImpactingBeliefs[belief])
            {
                if (IsCoolingDown(actionImpacting))
                {
                    continue;
                }

                if (!belief.CanImpactedByAction(actionImpacting, actionToEnable))
                {
                    continue;
                }

                var actionImpactingCostToGoal = actionToEnableCostToGoal + actionImpacting.Cost;
                if (VisitedActionsTotalCosts.TryGetValue(actionImpacting, out var knownCost) && knownCost <= actionImpactingCostToGoal)
                {
                    continue;
                }

                remainingActionsToExplore[actionImpacting] = actionImpactingCostToGoal;
                VisitedActionsTotalCosts[actionImpacting] = actionImpactingCostToGoal;

                VisitedActionsImpactedBy[actionImpacting] = actionToEnable;
            }
        }

        #endregion

        private void SelectActionAndRun(IEnumerable<IAction> enabledActions, bool periodic)
        {
            Profiler.BeginSample($"{nameof(FpcMindRunner)}.{nameof(SelectActionAndRun)}");
            try
            {
                var selectedAction = enabledActions.FirstOrDefault();
                var selectedCost = 0f;
                if (selectedAction != null)
                {
                    selectedCost = VisitedActionsTotalCosts.TryGetValue(selectedAction, out var total) ? total : selectedAction.Cost;
                }

                var running = RunningAction;
                if (selectedAction == running)
                {
                    if (running != null)
                    {
                        RunningActionCost = selectedCost;
                    }

                    return;
                }

                // A periodic refresh keeps a running action that still works unless the new plan
                // is clearly cheaper; belief changes and completions replan freely.
                if (periodic
                    && running != null
                    && IsEnabled(running)
                    && (selectedAction == null || selectedCost >= running.Cost * SwitchCostRatio))
                {
                    return;
                }

                if (BotLog.Verbose) Debug.Log($"New Action for bot: {selectedAction} (Cost: {selectedCost})");

                if (running != null)
                {
                    StopRunning($"{running}: replaced");
                }

                if (selectedAction != null)
                {
                    StartRunning(selectedAction, selectedCost);
                }
            }
            finally
            {
                Profiler.EndSample();
            }
        }
    }
}
