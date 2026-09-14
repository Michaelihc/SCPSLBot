using LabLogger = LabApi.Features.Console.Logger;
using MapGeneration;
using SCPSLBot.AI.FirstPersonControl;
using SCPSLBot.Navigation.Mesh;
using SCPSLBot.Navigation.Policy;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace SCPSLBot.Navigation.Authored
{
    /// <summary>
    /// Cell-mesh navigator of the authored backend: A* over room cells plus a funnel over the
    /// shared portal edges. Kept for one release behind navigation.backend = authored.
    /// </summary>
    internal class AuthoredBotNavigator : IBotNavigator
    {
        // A capsule can stop slightly short of a corner it is steering at; anything closer than
        // this counts as reached so the funnel advances instead of grinding on the last few cm.
        private const float CornerArrivalDistance = 0.35f;
        private const float OffMeshRecoveryRadius = 6f;
        private const float BlockedCrossingPenaltyMeters = 60f;
        private const float BlockedCrossingSeconds = 45f;
        private static float nextBlockedCrossingLogAt;

        private TransformCell? cellWithin;

        private TransformCell currentCell;
        private TransformCell? goalCell;
        public List<TransformCell> CellsPath { get; } = new();
        public IEnumerable<(TransformCell Cell, TransformCell NextCell)> CellPathSegments { get; }
        private int currentPathIdx = -1;

        public Vector3 GoalPosition { get; private set; }
        public List<Vector3> PointsPath { get; } = new();
        public IEnumerable<(Vector3 point, Vector3 nextPoint)> PathSegments { get; }

        private bool isGoalOutside;
        private bool hasPath;
        private bool hasPartialPath;
        private Vector3 targetCellClosestPositionToGoal;
        private int plannedTopologyVersion = -1;

        private readonly FpcBotPlayer botPlayer;

        public AuthoredBotNavigator(FpcBotPlayer botPlayer)
        {
            this.botPlayer = botPlayer;

            this.PathSegments = PointsPath.Zip(PointsPath.Skip(1), (point, nextPoint) => (point, nextPoint));
            this.CellPathSegments = CellsPath.Zip(CellsPath.Skip(1), (cell, nextCell) => (cell, nextCell));
        }

        /// <summary>True when a complete path to the goal cell exists.</summary>
        public bool HasPath => hasPath;

        /// <summary>True when only a partial path (toward the reachable cell nearest the goal) exists.</summary>
        public bool HasPartialPath => hasPartialPath;

        /// <summary>The steering point returned by the last <see cref="GetPositionTowards"/> call.</summary>
        public Vector3 CurrentWaypoint { get; private set; }

        /// <summary>
        /// Increments whenever the bot genuinely advances along its path (a portal was crossed) or
        /// a new goal was planned. Replans that merely swap waypoints do not touch it, so progress
        /// detectors can tell real movement from a waypoint flip-flop.
        /// </summary>
        public int ProgressStamp { get; private set; }

        private int replansInWindow;
        private float replanWindowStart;
        private static float nextReplanStormLogAt;

        // An unreachable goal must not flood A* every tick; retry only after the start or goal
        // cell changed or a short cooldown elapsed.
        private const float FailedPlanRetrySeconds = 1f;
        private TransformCell? failedPlanFrom;
        private TransformCell? failedPlanTo;
        private float failedPlanRetryAt;

        /// <summary>Index of the cell the bot is currently on within <see cref="CellsPath"/>.</summary>
        public int CurrentPathIndex => currentPathIdx;

        public int PathNodeCount => CellsPath.Count;

        public int AreaMask => ~0;

        private readonly List<TransformCell> reachScratch = new();

        public bool CanReach(Vector3 goalPosition)
        {
            var within = GetCellWithin();
            var target = NavigationMesh.GetCellWithin(goalPosition);
            if (!within.HasValue || !target.HasValue)
            {
                return false;
            }

            return NavigationMesh.FindShortestPath(within.Value, target.Value, reachScratch, allowPartial: false) == NavigationMesh.PathOutcome.Complete;
        }

        public bool IsOnMesh() => GetCellWithin() != null;

        public bool HasReached(Vector3 point)
        {
            var within = GetCellWithin();
            if (within.HasValue && Vector3.Distance(within.Value.CenterPosition, point) < 0.05f)
            {
                return true;
            }

            var position = botPlayer.PlayerPosition;
            return Vector3.Distance(Vector3.ProjectOnPlane(position, Vector3.up), Vector3.ProjectOnPlane(point, Vector3.up)) <= 1.5f
                   && Mathf.Abs(position.y - point.y) <= 1.5f;
        }

        /// <summary>
        /// Elevator crossings are edgeless links between cells (a connected cell without a
        /// connecting edge). Doors are unknown here; the elevation belief probes the world.
        /// </summary>
        public bool TryGetElevatorLink(out ElevatorLinkSegment link)
        {
            foreach (var (cell, nextCell) in CellPathSegments)
            {
                if (!cell.AdjacentCellEdges.ContainsKey(nextCell)
                    && !NavigationMesh.TryGetForeignConnectedEdge(cell, nextCell, out _))
                {
                    link = new ElevatorLinkSegment(cell.CenterPosition, nextCell.CenterPosition, null, null);
                    return true;
                }
            }

            link = default;
            return false;
        }

        public string DescribeState()
        {
            var kind = hasPath ? "complete" : hasPartialPath ? "partial" : "none";
            return $"{kind}/{currentPathIdx}/{CellsPath.Count}";
        }

        public Vector3 GetPositionTowards(Vector3 goalPosition)
        {
            this.UpdateNavigationTo(goalPosition);

            // No navigable path at all: hold position rather than walking straight into walls
            // toward an unreachable goal. Stuck recovery / target reselection takes it from here.
            if (!hasPath && !hasPartialPath)
            {
                CurrentWaypoint = botPlayer.PlayerPosition;
                return CurrentWaypoint;
            }

            while (!IsAtLastCell())
            {
                Vector3 nextTargetPosition = GetNextCorner(goalPosition);
                if (HorizontalDistanceToSegment(botPlayer.PlayerPosition, nextTargetPosition, nextTargetPosition) > CornerArrivalDistance)
                {
                    CurrentWaypoint = nextTargetPosition;
                    return nextTargetPosition;
                }

                var nextCell = CellsPath[currentPathIdx + 1];
                if (!currentCell.AdjacentCellEdges.ContainsKey(nextCell)
                    && !NavigationMesh.TryGetForeignConnectedEdge(currentCell, nextCell, out _))
                {
                    // Edgeless links (elevators) require their dedicated obstacle logic; do not
                    // claim traversal merely because their holding point was reached.
                    CurrentWaypoint = nextTargetPosition;
                    return nextTargetPosition;
                }

                // Reaching the requested edge point is sufficient evidence to advance the funnel.
                // Waiting for a strict plane-side sign can deadlock at exact zero after native
                // collision stops the capsule on the boundary. This is not counted as progress:
                // the bot has not physically crossed yet (it may be pressed against a closed door).
                currentCell = CellsPath[++currentPathIdx];
            }

            if (hasPartialPath)
            {
                // Walk as close to the unreachable goal as the mesh allows, then hold there.
                CurrentWaypoint = CellsPath[CellsPath.Count - 1].CenterPosition;
                return CurrentWaypoint;
            }

            if (goalCell != null && isGoalOutside)
            {
                CurrentWaypoint = targetCellClosestPositionToGoal;
                return CurrentWaypoint;
            }

            CurrentWaypoint = goalPosition;
            return goalPosition;
        }

        private void UpdateNavigationTo(Vector3 goalPosition)
        {
            var playerPosition = botPlayer.FpcRole.FpcModule.transform.position;

            if (!IsAtLastCell())
            {
                bool isEdgeReached;
                do
                {
                    var nextTargetCell = this.CellsPath[this.currentPathIdx + 1];
                    if (!this.currentCell.AdjacentCellEdges.TryGetValue(nextTargetCell, out var nextTargetCellEdge)
                        && !NavigationMesh.TryGetForeignConnectedEdge(this.currentCell, nextTargetCell, out nextTargetCellEdge))
                    {
                        // Edgeless segment (e.g. an elevator link, which registers a connected cell
                        // but no connecting edge): advance only once the bot has actually arrived in
                        // the next cell (elevator/obstacle handling carries it there); otherwise wait.
                        var arrivedCell = GetCellWithin();
                        isEdgeReached = arrivedCell.HasValue && arrivedCell.Value == nextTargetCell;
                    }
                    else
                    {
                        isEdgeReached = NavigationMesh.IsAtPositiveEdgeSide(playerPosition, nextTargetCellEdge);

                        // Native role spawns can land exactly on a nav-cell boundary. The strict
                        // positive-side test then leaves the next corner equal to the bot position,
                        // producing zero movement forever. Treat a bot touching the edge as crossed
                        // only when a small probe toward the next cell is on its positive side.
                        if (!isEdgeReached
                            && HorizontalDistanceToSegment(playerPosition, nextTargetCellEdge.From.Position, nextTargetCellEdge.To.Position) <= 0.2f)
                        {
                            var towardNextCell = Vector3.ProjectOnPlane(nextTargetCell.CenterPosition - currentCell.CenterPosition, Vector3.up);
                            if (towardNextCell.sqrMagnitude > 0.001f)
                            {
                                isEdgeReached = NavigationMesh.IsAtPositiveEdgeSide(
                                    playerPosition + towardNextCell.normalized * 0.2f,
                                    nextTargetCellEdge);
                            }
                        }
                    }

                    if (isEdgeReached)
                    {
                        this.currentCell = this.CellsPath[++this.currentPathIdx];
                        ProgressStamp++;
                    }
                }
                while (isEdgeReached && !IsAtLastCell());
            }

            var withinCell = GetCellWithin();
            var targetCell = NavigationMesh.GetCellWithin(goalPosition);

            if (targetCell == null)
            {
                if (RoomUtils.TryGetRoom(goalPosition, out var goalRoom) && goalRoom != null)
                {
                    var nearestEdge = NavigationMesh.GetNearestEdge(goalPosition, out var closestPoint, goalRoom);
                    if (nearestEdge.HasValue
                        && NavigationMesh.LocalMeshesByRoom.TryGetValue(goalRoom.gameObject, out var goalRoomMesh))
                    {
                        var nearestLocalEdge = new Edge(nearestEdge.Value.From, nearestEdge.Value.To);
                        targetCell = goalRoomMesh.Cells
                            .Where(a => a.Edges.Any(e => e == nearestLocalEdge))
                            .Select(a => new TransformCell?(new (a, goalRoom.transform)))
                            .FirstOrDefault();
                        targetCellClosestPositionToGoal = closestPoint;
                    }
                }

                isGoalOutside = true;
            }
            else
            {
                isGoalOutside = false;
            }

            if (targetCell == null)
            {
                hasPath = false;
                hasPartialPath = false;
                return;
            }

            var samePlan = goalCell.HasValue
                           && targetCell.Value == goalCell.Value
                           && plannedTopologyVersion == NavigationMesh.TopologyVersion
                           && CellsPath.Count > 0
                           && currentPathIdx >= 0;

            if (withinCell == null)
            {
                if (samePlan)
                {
                    // Momentarily off the mesh (a cell boundary, stairs, clutter top): keep following
                    // the existing path. Replanning from a guessed cell every tick flip-flops waypoints.
                    return;
                }

                // No usable plan at all: start from the nearest cell instead of freezing.
                if (!RoomUtils.TryGetRoom(playerPosition, out var currentRoom)
                    || currentRoom == null
                    || !NavigationMesh.TryGetNearestCell(playerPosition, currentRoom, OffMeshRecoveryRadius, out var nearestCell))
                {
                    hasPath = false;
                    hasPartialPath = false;
                    return;
                }

                withinCell = nearestCell;
            }

            // A path is kept while the bot stays on it (current, next, or previous cell). Level
            // ambiguity or a lagging edge-crossing test must not trigger a replan every tick.
            if (samePlan && TryAlignPathIndex(withinCell.Value))
            {
                return;
            }

            if (failedPlanFrom.HasValue
                && failedPlanFrom.Value == withinCell.Value
                && failedPlanTo.HasValue
                && failedPlanTo.Value == targetCell.Value
                && Time.time < failedPlanRetryAt)
            {
                hasPath = false;
                hasPartialPath = false;
                return;
            }

            NoteReplan(withinCell.Value, targetCell.Value);
            if (!goalCell.HasValue || targetCell.Value != goalCell.Value || Vector3.Distance(GoalPosition, goalPosition) > 0.5f)
            {
                // A new goal is progress for the owner's stuck accounting; a replan toward the same
                // goal is not, otherwise a replan storm would look like movement.
                ProgressStamp++;
            }

            this.currentCell = withinCell.Value;
            this.goalCell = targetCell.Value;
            plannedTopologyVersion = NavigationMesh.TopologyVersion;

            var outcome = NavigationMesh.FindShortestPath(withinCell.Value, targetCell.Value, this.CellsPath, allowPartial: true);
            this.currentPathIdx = 0;
            this.hasPath = outcome == NavigationMesh.PathOutcome.Complete;
            this.hasPartialPath = outcome == NavigationMesh.PathOutcome.Partial;
            if (outcome == NavigationMesh.PathOutcome.None)
            {
                failedPlanFrom = withinCell.Value;
                failedPlanTo = targetCell.Value;
                failedPlanRetryAt = Time.time + FailedPlanRetrySeconds;
            }
            else
            {
                failedPlanFrom = null;
                failedPlanTo = null;
            }

            this.GoalPosition = goalPosition;

            this.PointsPath.Clear();
            this.PointsPath.Add(playerPosition);

            var partialPath = false;
            foreach (var (cell, nextCell) in CellPathSegments)
            {
                if (!cell.AdjacentCellEdges.TryGetValue(nextCell, out var e)
                    && !NavigationMesh.TryGetForeignConnectedEdge(cell, nextCell, out e))
                {
                    partialPath = true;
                    break;
                }
                this.PointsPath.Add(Vector3.Lerp(e.From.Position, e.To.Position, .5f));
            }

            if (!partialPath && hasPath)
            {
                this.PointsPath.Add(goalPosition);
            }
        }

        // A plan that is rebuilt many times per second means the bot's cell keeps changing while it
        // stands still (stacked levels, boundary jitter). Log it so the map spot can be fixed.
        private void NoteReplan(TransformCell fromCell, TransformCell toCell)
        {
            if (Time.time - replanWindowStart > 1f)
            {
                replanWindowStart = Time.time;
                replansInWindow = 0;
            }

            replansInWindow++;
            if (replansInWindow >= 8 && Time.time >= nextReplanStormLogAt)
            {
                nextReplanStormLogAt = Time.time + 5f;
                LabLogger.Warn($"[BotNav] REPLAN_STORM bot={botPlayer.BotHub.PlayerHub?.nicknameSync?.MyNick} replansPerSecond={replansInWindow} pos={Format(botPlayer.PlayerPosition)} from={Describe(fromCell)} to={Describe(toCell)} previousCell={Describe(currentCell)}");
            }
        }

        private bool TryAlignPathIndex(TransformCell withinCell)
        {
            if (CellsPath.Count == 0 || currentPathIdx < 0)
            {
                return false;
            }

            if (withinCell == currentCell)
            {
                return true;
            }

            // Still in the cell before the one the funnel already advanced to (e.g. waiting at a
            // closed door): keep the index, never step it back, or the funnel and this alignment
            // would alternate every tick.
            if (currentPathIdx > 0 && CellsPath[currentPathIdx - 1] == withinCell)
            {
                return true;
            }

            var to = Mathf.Min(CellsPath.Count - 1, currentPathIdx + 2);
            for (var index = currentPathIdx + 1; index <= to; index++)
            {
                if (CellsPath[index] == withinCell)
                {
                    currentPathIdx = index;
                    currentCell = withinCell;
                    ProgressStamp++;
                    return true;
                }
            }

            return false;
        }

        public TransformCell? GetCellWithin()
        {
            var playerPosition = botPlayer.PlayerPosition;
            cellWithin = NavigationMesh.GetCellWithin(playerPosition);

            return cellWithin;
        }

        private Vector3 GetNextCorner(Vector3 goalPosition)
        {
            var playerPosition = botPlayer.PlayerPosition;

            var nextTargetCell = this.CellsPath[this.currentPathIdx + 1];
            if (!currentCell.AdjacentCellEdges.TryGetValue(nextTargetCell, out var targetCellEdge)
                && !NavigationMesh.TryGetForeignConnectedEdge(currentCell, nextTargetCell, out targetCellEdge))
            {
                return currentCell.CenterPosition;
            }
            var nextTargetEdgeMiddlePosition = Vector3.Lerp(targetCellEdge.From.Position, targetCellEdge.To.Position, 0.5f);

            var nextTargetPosition = nextTargetEdgeMiddlePosition;

            var aheadPathIdx = this.currentPathIdx + 1;

            while (nextTargetEdgeMiddlePosition == nextTargetPosition && aheadPathIdx < this.CellsPath.Count - 1)
            {
                aheadPathIdx++;

                var relTargetEdgePos = (
                    from: targetCellEdge.From.Position - playerPosition,
                    to: targetCellEdge.To.Position - playerPosition);

                var aheadTargetCell = this.CellsPath[aheadPathIdx];
                if (!nextTargetCell.AdjacentCellEdges.TryGetValue(aheadTargetCell, out var aheadTargetCellEdge)
                    && !NavigationMesh.TryGetForeignConnectedEdge(nextTargetCell, aheadTargetCell, out aheadTargetCellEdge))
                {
                    goalPosition = nextTargetCell.CenterPosition;
                    break;
                }

                var relAheadTargetEdgePos = (
                    from: aheadTargetCellEdge.From.Position - playerPosition,
                    to: aheadTargetCellEdge.To.Position - playerPosition);

                var dirToAheadTargetEdgeNormals = (
                    from: Vector3.Cross(relAheadTargetEdgePos.from, Vector3.up),
                    to: Vector3.Cross(relAheadTargetEdgePos.to, Vector3.up));

                if (Vector3.Dot(relTargetEdgePos.from, dirToAheadTargetEdgeNormals.from) < 0)
                {
                    targetCellEdge.From = aheadTargetCellEdge.From;
                }

                if (Vector3.Dot(relTargetEdgePos.to, dirToAheadTargetEdgeNormals.to) > 0)
                {
                    targetCellEdge.To = aheadTargetCellEdge.To;
                }


                if (Vector3.Dot(relTargetEdgePos.from, dirToAheadTargetEdgeNormals.to) > 0)
                {
                    nextTargetPosition = InsetCorner(targetCellEdge.From.Position, targetCellEdge.To.Position);
                }

                if (Vector3.Dot(relTargetEdgePos.to, dirToAheadTargetEdgeNormals.from) < 0)
                {
                    nextTargetPosition = InsetCorner(targetCellEdge.To.Position, targetCellEdge.From.Position);
                }

                nextTargetCell = aheadTargetCell;
            }

            if (nextTargetPosition == nextTargetEdgeMiddlePosition)
            {
                nextTargetPosition = goalPosition;

                var relNextTargetEdgePos = (
                    from: targetCellEdge.From.Position - playerPosition,
                    to: targetCellEdge.To.Position - playerPosition);

                var relGoalPos = goalPosition - playerPosition;
                var dirToGoalNormal = Vector3.Cross(relGoalPos, Vector3.up);

                if (Vector3.Dot(relNextTargetEdgePos.from, dirToGoalNormal) > 0)
                {
                    nextTargetPosition = InsetCorner(targetCellEdge.From.Position, targetCellEdge.To.Position);
                }

                if (Vector3.Dot(relNextTargetEdgePos.to, dirToGoalNormal) < 0)
                {
                    nextTargetPosition = InsetCorner(targetCellEdge.To.Position, targetCellEdge.From.Position);
                }
            }

            return nextTargetPosition;
        }

        // Portal endpoints sit on walls and door frames; steer at a point the capsule can reach.
        private static Vector3 InsetCorner(Vector3 corner, Vector3 otherEnd)
        {
            var (x, z) = PortalCornerPolicy.InsetCorner(corner.x, corner.z, otherEnd.x, otherEnd.z, NavigationAgentProfile.CornerInset);
            return new Vector3(x, corner.y, z);
        }

        private static float HorizontalDistanceToSegment(Vector3 point, Vector3 from, Vector3 to)
        {
            point = Vector3.ProjectOnPlane(point, Vector3.up);
            from = Vector3.ProjectOnPlane(from, Vector3.up);
            to = Vector3.ProjectOnPlane(to, Vector3.up);

            var segment = to - from;
            if (segment.sqrMagnitude < 0.0001f)
            {
                return Vector3.Distance(point, from);
            }

            var t = Mathf.Clamp01(Vector3.Dot(point - from, segment) / segment.sqrMagnitude);
            return Vector3.Distance(point, from + segment * t);
        }

        private bool IsAtLastCell()
        {
            return this.currentPathIdx >= this.CellsPath.Count - 1;
        }

        // Forces UpdateNavigationTo to rebuild the cell path on the next call (used by stuck recovery).
        public void ForceReplan()
        {
            goalCell = null;
            currentPathIdx = -1;
        }

        /// <summary>
        /// Marks the crossing the bot is currently attempting as blocked for a while (both
        /// directions) so the next plan prefers another route when one exists, then replans.
        /// Returns false when the bot is not attempting a crossing.
        /// </summary>
        public bool ReportBlockedCrossing()
        {
            if (IsAtLastCell() || currentPathIdx < 0 || currentPathIdx + 1 >= CellsPath.Count)
            {
                ForceReplan();
                return false;
            }

            var from = CellsPath[currentPathIdx];
            var to = CellsPath[currentPathIdx + 1];
            NavigationMesh.PenalizeLink(from, to, BlockedCrossingPenaltyMeters, BlockedCrossingSeconds);
            NavigationMesh.PenalizeLink(to, from, BlockedCrossingPenaltyMeters, BlockedCrossingSeconds);

            if (Time.time >= nextBlockedCrossingLogAt)
            {
                nextBlockedCrossingLogAt = Time.time + 5f;
                LabLogger.Warn($"[BotNav] CROSSING_PENALIZED bot={botPlayer.BotHub.PlayerHub?.nicknameSync?.MyNick} from={Describe(from)} to={Describe(to)} pos={Format(botPlayer.PlayerPosition)} penalized={NavigationMesh.PenalizedLinkCount}");
            }

            ForceReplan();
            return true;
        }

        private static string Describe(TransformCell cell)
        {
            var form = cell.Transform != null ? NavigationMesh.GetForm(cell.Transform.gameObject) : "null";
            return $"{form}@{Format(cell.CenterPosition)}";
        }

        private static string Format(Vector3 value) => $"({value.x:F1},{value.y:F1},{value.z:F1})";
    }
}
