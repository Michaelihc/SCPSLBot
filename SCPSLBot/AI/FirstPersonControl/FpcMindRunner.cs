using SCPSLBot.AI.FirstPersonControl.Mind;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Profiling;

namespace SCPSLBot.AI.FirstPersonControl
{
    internal class FpcMindRunner : FpcMind, IDisposable
    {
        public IAction RunningAction { get; private set; }
        public float RunningActionCost { get; private set; }

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
        }

        public void EvaluateGoalsToActions()
        {
            if (!isDisposed)
            {
                isBeliefsUpdated = true;
            }
        }

        public void Tick()
        {
            if (isDisposed)
            {
                return;
            }

            Profiler.BeginSample($"{nameof(FpcMindRunner)}.{nameof(Tick)}");
            try
            {
                if (isBeliefsUpdated)
                {
                    isBeliefsUpdated = false;

                    IEnumerable<IAction> enabledActions = GetEnabledActionsTowardsGoals();

                    SelectActionAndRun(enabledActions);
                }

                RunningAction?.Tick();
            }
            finally
            {
                Profiler.EndSample();
            }
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
            //Debug.Log($"  Goal {goal.GetType().Name}...");

            VisitedActionsTotalCosts.Clear();
            remainingActionsToExplore.Clear();

            foreach (var b in BeliefsEnablingGoals[goal])
            {
                VisitedGoalsEnabledBy[b] = goal;

                if (b.EvaluateEnabling(goal))
                {
                    //Debug.Log($"    Belief {b} already satisfies goal.");
                    continue;
                }

                //Debug.Log($"    Belief {b} needs to satisfy goal.");

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

                remainingActionsToExplore.Remove(actionImpacting);

                //Debug.Log($"      Exploring action {actionImpacting}.");
                foreach (var enabledAction in GetEnabledActionsEnabling(actionImpacting))
                {
                    yield return enabledAction;
                }
            }
        }

        private void ProcessActionsImpacting(IBelief belief, IGoal goalToEnable)
        {
            foreach (var actionImpacting in ActionsImpactingBeliefs[belief])
            {
                VisitedGoalsImpactedBy[actionImpacting] = goalToEnable;

                if (!belief.CanImpactedByAction(actionImpacting, goalToEnable))
                {
                    //Debug.Log($"      Action {actionImpacting} cannot impact belief.");
                    continue;
                }

                var actionImpactingCost = actionImpacting.Cost;
                remainingActionsToExplore.Add(actionImpacting, actionImpactingCost);
                VisitedActionsTotalCosts[actionImpacting] = actionImpactingCost;

                //Debug.Log($"      Action {actionImpacting} can impact belief with cost {actionImpactingCost}.");
            }
        }

        private IEnumerable<IAction> GetEnabledActionsEnabling(IAction actionToEnable)
        {
            //var prefix = "      ";

            var beliefsEnabling = BeliefsEnablingActions[actionToEnable];

            var actionEnabled = true;
            foreach (var b in beliefsEnabling)
            {
                VisitedActionsEnabledBy[b] = actionToEnable;

                if (b.IsEnabledAction(actionToEnable))
                {
                    //Debug.Log($"{prefix}  Belief {b} already satisfies action.");
                    continue;
                }

                //Debug.Log($"{prefix}  Belief {b} needs to satisfy action.");
                actionEnabled = false;

                ProcessActionsImpacting(b, actionToEnable);

                break;
            }

            if (actionEnabled)
            {
                //Debug.Log($"{prefix}Action {actionToEnable} conditions fulfilled.");

                yield return actionToEnable;
            }
        }

        private void ProcessActionsImpacting(IBelief belief, IAction actionToEnable)
        {
            //var prefix = "        ";

            var actionToEnableCostToGoal = VisitedActionsTotalCosts[actionToEnable];

            foreach (var actionImpacting in ActionsImpactingBeliefs[belief])
            {
                if (!belief.CanImpactedByAction(actionImpacting, actionToEnable))
                {
                    //Debug.Log($"{prefix}  Action {actionImpacting} cannot impact belief.");
                    continue;
                }

                var actionImpactingCostToGoal = actionToEnableCostToGoal + actionImpacting.Cost;
                if (VisitedActionsTotalCosts.ContainsKey(actionImpacting) && VisitedActionsTotalCosts[actionImpacting] < actionImpactingCostToGoal)
                {
                    //Debug.Log($"{prefix}  Action {actionImpacting} can impact belief but cost takes more ({VisitedActionsTotalCosts[actionImpacting]} < {actionImpactingCostToGoal}).");
                    continue;
                }

                //Debug.Log($"{prefix}  Action {actionImpacting} can impact belief with least total cost {actionImpactingCostToGoal}.");

                remainingActionsToExplore[actionImpacting] = actionImpactingCostToGoal;
                VisitedActionsTotalCosts[actionImpacting] = actionImpactingCostToGoal;

                VisitedActionsImpactedBy[actionImpacting] = actionToEnable;
            }
        }

        #endregion

        private void SelectActionAndRun(IEnumerable<IAction> enabledActions)
        {
            Profiler.BeginSample($"{nameof(FpcMindRunner)}.{nameof(SelectActionAndRun)}");

            var selectedAction = enabledActions.FirstOrDefault();

            var prevAction = RunningAction;

            RunningAction = selectedAction ?? null;
            RunningActionCost = selectedAction?.Cost ?? 0f;

            if (BotLog.Verbose) Debug.Log($"New Action for bot: {RunningAction} (Cost: {RunningActionCost})");

            if (RunningAction != prevAction)
            {
                RunningAction?.Reset();
            }

            Profiler.EndSample();
        }
    }
}
