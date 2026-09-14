using MapGeneration;
using SCPSLBot.Navigation.Mesh;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace SCPSLBot.AI.FirstPersonControl
{
    internal class FpcBotNavigator
    {
        private Vector3 lastPlayerPosition;
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
        private Vector3 targetCellClosestPositionToGoal;

        private readonly FpcBotPlayer botPlayer;

        public FpcBotNavigator(FpcBotPlayer botPlayer)
        {
            this.botPlayer = botPlayer;

            this.PathSegments = PointsPath.Zip(PointsPath.Skip(1), (point, nextPoint) => (point, nextPoint));
            this.CellPathSegments = CellsPath.Zip(CellsPath.Skip(1), (cell, nextCell) => (cell, nextCell));
        }

        public Vector3 GetPositionTowards(Vector3 goalPosition)
        {
            this.UpdateNavigationTo(goalPosition);

            // No navigable path: hold position rather than walking straight into walls toward an
            // unreachable goal. Stuck recovery / target reselection takes it from here.
            if (!hasPath)
            {
                return botPlayer.PlayerPosition;
            }

            while (!IsAtLastCell())
            {
                Vector3 nextTargetPosition = GetNextCorner(goalPosition);
                if (HorizontalDistanceToSegment(botPlayer.PlayerPosition, nextTargetPosition, nextTargetPosition) > 0.2f)
                {
                    return nextTargetPosition;
                }

                var nextCell = CellsPath[currentPathIdx + 1];
                if (!currentCell.AdjacentCellEdges.ContainsKey(nextCell)
                    && !NavigationMesh.TryGetForeignConnectedEdge(currentCell, nextCell, out _))
                {
                    // Edgeless links (elevators) require their dedicated obstacle logic; do not
                    // claim traversal merely because their holding point was reached.
                    return nextTargetPosition;
                }

                // Reaching the requested edge point is sufficient evidence to advance. Waiting for
                // a strict plane-side sign can deadlock at exact zero after native collision stops
                // the capsule on the boundary.
                currentCell = CellsPath[++currentPathIdx];
            }

            if (goalCell != null && isGoalOutside)
            {
                return targetCellClosestPositionToGoal;
            }

            return goalPosition;
        }

        public bool HasPath => hasPath;

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
                        //Log.Debug($"New current cell {this.currentCell}.");
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
            }

            if (withinCell != null && targetCell != null && (targetCell != this.goalCell || withinCell.Value != this.currentCell))
            {
                this.currentCell = withinCell.Value;
                this.goalCell = targetCell.Value;
                //Log.Debug($"New start cell {withinCell}.");
                //Log.Debug($"New goal cell {targetCell}.");

                NavigationMesh.FindShortestPath(withinCell.Value, targetCell.Value, this.CellsPath);
                this.currentPathIdx = 0;
                this.hasPath = this.CellsPath.Count > 0;

                //Log.Debug($"New path of {this.CellsPath.Count} cells:");
                //foreach (var cellInPath in CellsPath)
                //{
                //    Log.Debug($"Cell {cellInPath}.");
                //}

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

                if (!partialPath) 
                {
                    this.PointsPath.Add(goalPosition);
                }
            }
        }

        public TransformCell? GetCellWithin()
        {
            var playerPosition = botPlayer.PlayerPosition;
            cellWithin = NavigationMesh.GetCellWithin(playerPosition);
            lastPlayerPosition = playerPosition;

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
                    nextTargetPosition = targetCellEdge.From.Position;
                }

                if (Vector3.Dot(relTargetEdgePos.to, dirToAheadTargetEdgeNormals.from) < 0)
                {
                    nextTargetPosition = targetCellEdge.To.Position;
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
                    nextTargetPosition = targetCellEdge.From.Position;
                }

                if (Vector3.Dot(relNextTargetEdgePos.to, dirToGoalNormal) < 0)
                {
                    nextTargetPosition = targetCellEdge.To.Position;
                }
            }

            return nextTargetPosition;
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
    }
}
