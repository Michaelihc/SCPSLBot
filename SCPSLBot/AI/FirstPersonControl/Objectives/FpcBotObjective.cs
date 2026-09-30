using LabLogger = LabApi.Features.Console.Logger;
using SCPSLBot.Navigation;
using UnityEngine;

namespace SCPSLBot.AI.FirstPersonControl.Objectives
{
    /// <summary>
    /// Runs a bot's objective each tick: combat bounded to the engage radius first, then walking to
    /// (or holding at) the resolved goal through the bot's navigator. The objective state lives in
    /// <see cref="BotManager"/> so it survives role changes; this type only applies
    /// <see cref="BotObjectivePolicy"/> decisions to the navigator, movement and stuck recovery.
    /// </summary>
    internal sealed class FpcBotObjective
    {
        private const float NearestPointSearchMeters = 60f;

        private readonly FpcBotPlayer botPlayer;
        private BotObjectiveState current;

        public FpcBotObjective(FpcBotPlayer botPlayer)
        {
            this.botPlayer = botPlayer;
        }

        /// <summary>Runs one tick and returns the phase the objective is in afterwards.</summary>
        public BotObjectivePhase Tick(BotObjectiveState state)
        {
            var now = Time.time;
            var navigator = botPlayer.Navigator;
            if (!ReferenceEquals(state, current))
            {
                current = state;
                botPlayer.StuckRecovery.Reset();
                navigator.ForceReplan();
            }

            var backend = NavigationSystem.Instance.Backend;
            if (!state.GoalResolved || backend != null && state.ResolvedTopologyVersion != backend.TopologyVersion)
            {
                ResolveGoal(state, backend);
            }

            if (state.EngageRadius > 0f && botPlayer.Combat.TickWithinRadius(state.EngageRadius))
            {
                state.Policy.NoteEngaged();
                botPlayer.StuckRecovery.Tick(navigator.CurrentWaypoint);
                botPlayer.StuckRecovery.ConsumeAbandonRequest();
                return state.Policy.Phase;
            }

            var remaining = GoalDistance(botPlayer.PlayerPosition, state.Goal);
            state.DistanceRemaining = remaining;
            var actions = state.Policy.BeginTick(now, remaining);
            Apply(state, backend, actions);
            if ((actions & BotObjectiveActions.Hold) != 0)
            {
                Hold();
                return state.Policy.Phase;
            }

            botPlayer.MoveToPosition(state.Goal, out var waypoint);
            state.HasPath = navigator.HasPath;
            actions = state.Policy.ObserveMove(now, remaining, navigator.HasPath, navigator.HasPartialPath,
                PathEndDistance(navigator), navigator.ProgressStamp);
            Apply(state, backend, actions);
            if ((actions & BotObjectiveActions.Hold) != 0)
            {
                Hold();
                LogFirstArrival(state, remaining);
                return state.Policy.Phase;
            }

            if (actions != BotObjectiveActions.None)
            {
                LabLogger.Warn($"[BotOrders] OBJECTIVE_STALL bot={BotManager.BotName(botPlayer.BotHub.PlayerHub)} stalls={state.Policy.StallCount} actions={actions} pos={BotManager.Format(botPlayer.PlayerPosition)} goal={BotManager.Format(state.Goal)} remaining={remaining:F2} path={navigator.DescribeState()}");
            }

            botPlayer.StuckRecovery.Tick(waypoint);
            if (botPlayer.StuckRecovery.ConsumeAbandonRequest())
            {
                // The waypoint ladder gave up: resolve the goal again rather than dropping the objective.
                state.GoalResolved = false;
                navigator.ForceReplan();
            }

            return state.Policy.Phase;
        }

        private void Apply(BotObjectiveState state, INavigationBackend backend, BotObjectiveActions actions)
        {
            if ((actions & BotObjectiveActions.ResolveGoal) != 0)
            {
                ResolveGoal(state, backend);
            }

            if ((actions & BotObjectiveActions.AvoidCrossing) != 0)
            {
                botPlayer.Navigator.ReportBlockedCrossing();
            }
            else if ((actions & BotObjectiveActions.Replan) != 0)
            {
                botPlayer.Navigator.ForceReplan();
            }
        }

        private void Hold()
        {
            botPlayer.Move.DesiredLocalDirection = Vector3.zero;
            botPlayer.StuckRecovery.Reset();
        }

        // Off-mesh points resolve to the nearest navmesh point; an island the bot cannot reach then
        // yields a partial path whose end is the nearest reachable point.
        private void ResolveGoal(BotObjectiveState state, INavigationBackend backend)
        {
            if (backend == null || !NavigationSystem.Instance.IsReadyForCurrentMap)
            {
                state.Goal = state.Point;
                state.GoalResolved = false;
                state.GoalIsNearestPoint = false;
                return;
            }

            state.GoalResolved = true;
            state.ResolvedTopologyVersion = backend.TopologyVersion;
            var hub = botPlayer.BotHub.PlayerHub;
            if (BotManager.IsGoalOnNavigation(hub, backend, state.Point))
            {
                state.Goal = state.Point;
                state.GoalIsNearestPoint = false;
                return;
            }

            if (backend.TryGetNearestPoint(state.Point, NearestPointSearchMeters, out var nearest, out var distance))
            {
                var moved = !state.GoalIsNearestPoint || (nearest - state.Goal).sqrMagnitude > 0.01f;
                state.Goal = nearest;
                state.GoalIsNearestPoint = true;
                if (moved)
                {
                    LabLogger.Info($"[BotOrders] OBJECTIVE_NEAREST bot={BotManager.BotName(hub)} point={BotManager.Format(state.Point)} goal={BotManager.Format(nearest)} distance={distance:F2}");
                }

                return;
            }

            state.Goal = state.Point;
            state.GoalIsNearestPoint = false;
            if (!state.OffMeshLogged)
            {
                state.OffMeshLogged = true;
                LabLogger.Warn($"[BotOrders] OBJECTIVE_OFF_MESH bot={BotManager.BotName(hub)} point={BotManager.Format(state.Point)} search={NearestPointSearchMeters:F0}");
            }
        }

        private float PathEndDistance(IBotNavigator navigator)
        {
            var points = navigator.PointsPath;
            var end = points != null && points.Count > 1 ? points[points.Count - 1] : navigator.CurrentWaypoint;
            return GoalDistance(botPlayer.PlayerPosition, end);
        }

        internal static float GoalDistance(Vector3 position, Vector3 goal)
            => BotObjectivePolicy.GoalDistance(BotManager.HorizontalDistance(position, goal), position.y - goal.y);

        private void LogFirstArrival(BotObjectiveState state, float remaining)
        {
            if (state.ArrivalLogged)
            {
                return;
            }

            state.ArrivalLogged = true;
            LabLogger.Info($"[BotOrders] OBJECTIVE_HOLD bot={BotManager.BotName(botPlayer.BotHub.PlayerHub)} nearestReachable={state.Policy.AtNearestReachable} elapsed={Time.time - state.IssuedAt:F1} remaining={remaining:F2} pos={BotManager.Format(botPlayer.PlayerPosition)} goal={BotManager.Format(state.Goal)}");
        }
    }
}
