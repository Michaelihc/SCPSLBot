using Interactables.Interobjects.DoorUtils;
using SCPSLBot.AI.FirstPersonControl;
using SCPSLBot.Navigation.Policy;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using LabLogger = LabApi.Features.Console.Logger;

namespace SCPSLBot.Navigation.Runtime
{
    /// <summary>
    /// Per-bot planner on the runtime navmesh: one <see cref="NavMesh.CalculatePath"/> per replan
    /// (never per tick), string-pulled corners followed with the same arrival distance as the
    /// authored funnel, an area mask built from the bot's keycards so it never plans through a
    /// door it cannot open, and elevator links exposed to the elevation behaviors. Behaviors may
    /// steer at two goals in one tick (a combat target and the door on the way to it), so the
    /// last two plans are kept and switched by goal instead of being rebuilt.
    /// </summary>
    internal sealed class RuntimeBotNavigator : IBotNavigator
    {
        private const float CornerArrivalDistance = 0.35f;
        private const float CornerVerticalTolerance = 1.5f;
        private const float StartSampleRadius = 2f;
        private const float StartRecoveryRadius = 6f;
        private const float GoalSampleRadius = 2f;
        private const float GoalRecoveryRadius = 6f;
        private const float DirectGoalDistance = 4f;
        private const float LinkArrivalDistance = 2.5f;
        private const float CornerInsetMeters = 0.12f;
        private const int MaxCorners = 256;
        private const int PlanSlots = 2;

        private static readonly Vector3[] cornerBuffer = new Vector3[MaxCorners];
        private static float nextReplanStormLogAt;
        private static float nextBlockedCrossingLogAt;
        private static float nextPlanFailureLogAt;

        private sealed class Plan
        {
            public readonly List<Vector3> Corners = new();
            public readonly List<int> LinkCornerIndices = new();
            public readonly List<NavMeshLinkRegistry.Link> LinkAtCorner = new();
            public readonly List<Vector3> Points = new();
            public readonly PathReplanPolicy Policy = new();
            public int CornerIndex;
            public bool HasPath;
            public bool HasPartialPath;
            public bool GoalOffMesh;
            public bool HasGoal;
            public Vector3 Goal;
            public float LastUsedAt = float.NegativeInfinity;

            public void Reset()
            {
                Corners.Clear();
                LinkCornerIndices.Clear();
                LinkAtCorner.Clear();
                Points.Clear();
                CornerIndex = 0;
                HasPath = false;
                HasPartialPath = false;
                GoalOffMesh = false;
            }
        }

        private readonly FpcBotPlayer botPlayer;
        private readonly RuntimeNavMeshService service;
        private readonly NavMeshPath path = new();
        private readonly Plan[] plans = new Plan[PlanSlots];
        private Plan current;
        private int replansInWindow;
        private float replanWindowStart;
        private readonly Vector3[] recentGoals = new Vector3[4];
        private int recentGoalIndex;

        public RuntimeBotNavigator(FpcBotPlayer botPlayer, RuntimeNavMeshService service)
        {
            this.botPlayer = botPlayer;
            this.service = service;
            for (var index = 0; index < plans.Length; index++)
            {
                plans[index] = new Plan();
            }

            current = plans[0];
        }

        public bool HasPath => current.HasPath;
        public bool HasPartialPath => current.HasPartialPath;
        public Vector3 CurrentWaypoint { get; private set; }
        public Vector3 GoalPosition => current.Goal;
        public int ProgressStamp { get; private set; }
        public int PathNodeCount => current.Corners.Count;
        public List<Vector3> PointsPath => current.Points;

        public IEnumerable<(Vector3 point, Vector3 nextPoint)> PathSegments
        {
            get
            {
                var points = current.Points;
                for (var index = 0; index + 1 < points.Count; index++)
                {
                    yield return (points[index], points[index + 1]);
                }
            }
        }

        public string DescribeState()
        {
            var kind = current.HasPath ? "complete" : current.HasPartialPath ? "partial" : "none";
            return $"{kind}/{current.CornerIndex}/{current.Corners.Count}";
        }

        public Vector3 GetPositionTowards(Vector3 goalPosition)
        {
            var position = botPlayer.PlayerPosition;
            if (!service.IsBuilt)
            {
                current.Reset();
                CurrentWaypoint = position;
                return position;
            }

            recentGoals[recentGoalIndex] = goalPosition;
            recentGoalIndex = (recentGoalIndex + 1) % recentGoals.Length;
            current = SelectPlan(goalPosition);
            current.LastUsedAt = Time.time;
            var displacement = current.HasGoal ? Vector3.Distance(current.Goal, goalPosition) : float.PositiveInfinity;
            if (current.Policy.ShouldReplan(Time.time, displacement, pathInvalid: false, service.SurfaceGeneration))
            {
                // Tiles are swapped while a reconcile rebuild is in flight; a query in that window
                // can come back invalid. Keep following the current plan until the surface settles.
                var keepDuringRebuild = service.IsReconciling
                                        && (current.HasPath || current.HasPartialPath)
                                        && displacement <= PathReplanPolicy.GoalMoveReplanMeters;
                if (!keepDuringRebuild)
                {
                    Replan(current, goalPosition);
                }
            }

            if (!current.HasPath && !current.HasPartialPath)
            {
                // No navigable path at all: hold rather than walking into walls. Stuck recovery and
                // target reselection take it from here.
                CurrentWaypoint = position;
                return position;
            }

            AdvanceCorners(current, position);

            var corners = current.Corners;
            if (current.CornerIndex < corners.Count - 1)
            {
                CurrentWaypoint = corners[current.CornerIndex];
                return CurrentWaypoint;
            }

            // Last corner reached. A goal that sits just off the mesh (an elevator chamber, a
            // chamber intake, a spot on clutter) is approached directly when it is close.
            if (current.HasPath && current.GoalOffMesh && HorizontalDistance(corners[corners.Count - 1], current.Goal) <= DirectGoalDistance)
            {
                CurrentWaypoint = current.Goal;
                return CurrentWaypoint;
            }

            // A complete plan whose goal crept (a chased player) steers at the requested point
            // for the last leg; the plan is refreshed at the bounded cadence.
            CurrentWaypoint = current.HasPath ? goalPosition : corners[corners.Count - 1];
            return CurrentWaypoint;
        }

        public int AreaMask => service.Config.KeycardAreaRouting ? service.Areas.BuildAreaMask(ResolvePermissions(), IsBypass()) : NavMesh.AllAreas;

        public bool CanReach(Vector3 goalPosition)
        {
            if (!service.IsBuilt)
            {
                return false;
            }

            var position = botPlayer.PlayerPosition;
            var permissions = ResolvePermissions();
            var areaMask = service.Config.KeycardAreaRouting ? service.Areas.BuildAreaMask(permissions, IsBypass()) : NavMesh.AllAreas;
            if (!TrySample(position, StartSampleRadius, StartRecoveryRadius, areaMask, out var start, out var startMask)
                || !TrySample(goalPosition, GoalSampleRadius, GoalRecoveryRadius, NavMesh.AllAreas, out var goal, out _))
            {
                return false;
            }

            return NavMesh.CalculatePath(start, goal, areaMask | startMask, path) && path.status == NavMeshPathStatus.PathComplete;
        }

        public void ForceReplan()
        {
            foreach (var plan in plans)
            {
                plan.Policy.Force();
            }
        }

        public bool ReportBlockedCrossing()
        {
            var corners = current.Corners;
            if (!service.IsBuilt || corners.Count == 0 || current.CornerIndex >= corners.Count - 1)
            {
                ForceReplan();
                return false;
            }

            // Carve just ahead of the bot, toward the corner it is failing to reach, so its own
            // footprint stays on the mesh and the next plan routes around the blocker.
            var position = botPlayer.PlayerPosition;
            var target = corners[current.CornerIndex];
            var toward = Vector3.ProjectOnPlane(target - position, Vector3.up);
            var ahead = toward.sqrMagnitude > 0.01f ? toward.normalized : Vector3.ProjectOnPlane(botPlayer.PlayerForward, Vector3.up).normalized;
            var distance = Mathf.Clamp(toward.magnitude * 0.5f, 0.8f, 1.5f);
            var spot = position + ahead * distance;
            service.BlockCrossing(spot);
            if (Time.time >= nextBlockedCrossingLogAt)
            {
                nextBlockedCrossingLogAt = Time.time + 5f;
                LabLogger.Warn($"[BotNav] CROSSING_PENALIZED bot={BotName} spot={Format(spot)} corner={Format(target)} pos={Format(position)} obstacles={service.Obstacles.Count}");
            }

            ForceReplan();
            return true;
        }

        public bool IsOnMesh()
        {
            return service.IsBuilt && NavMesh.SamplePosition(botPlayer.PlayerPosition, out _, StartSampleRadius, NavMesh.AllAreas);
        }

        public bool TryGetElevatorLink(out ElevatorLinkSegment link)
        {
            var plan = current;
            for (var index = 0; index < plan.LinkCornerIndices.Count; index++)
            {
                var cornerAt = plan.LinkCornerIndices[index];
                if (cornerAt + 1 < plan.CornerIndex)
                {
                    continue;
                }

                var registryLink = plan.LinkAtCorner[index];
                var origin = plan.Corners[cornerAt];
                var destination = plan.Corners[cornerAt + 1];
                var startsAtA = Vector3.Distance(origin, registryLink.LandingA) <= Vector3.Distance(origin, registryLink.LandingB);
                link = new ElevatorLinkSegment(
                    origin,
                    destination,
                    startsAtA ? registryLink.DoorA : registryLink.DoorB,
                    startsAtA ? registryLink.DoorB : registryLink.DoorA);
                return true;
            }

            link = default;
            return false;
        }

        public bool HasReached(Vector3 point)
        {
            var position = botPlayer.PlayerPosition;
            return HorizontalDistance(position, point) <= LinkArrivalDistance && Mathf.Abs(position.y - point.y) <= CornerVerticalTolerance;
        }

        // The plan whose goal matches, else the least recently used slot.
        private Plan SelectPlan(Vector3 goalPosition)
        {
            Plan best = null;
            var bestDistance = PathReplanPolicy.GoalMoveReplanMeters;
            foreach (var plan in plans)
            {
                if (!plan.HasGoal)
                {
                    continue;
                }

                var distance = Vector3.Distance(plan.Goal, goalPosition);
                if (distance <= bestDistance)
                {
                    bestDistance = distance;
                    best = plan;
                }
            }

            if (best != null)
            {
                return best;
            }

            Plan oldest = plans[0];
            foreach (var plan in plans)
            {
                if (plan.LastUsedAt < oldest.LastUsedAt)
                {
                    oldest = plan;
                }
            }

            return oldest;
        }

        private void Replan(Plan plan, Vector3 goalPosition)
        {
            var position = botPlayer.PlayerPosition;
            NoteReplan();
            var newGoal = !plan.HasGoal || Vector3.Distance(plan.Goal, goalPosition) > PathReplanPolicy.GoalMoveReplanMeters;
            plan.Reset();
            plan.HasGoal = true;
            plan.Goal = goalPosition;

            var permissions = ResolvePermissions();
            var areaMask = service.Config.KeycardAreaRouting ? service.Areas.BuildAreaMask(permissions, IsBypass()) : NavMesh.AllAreas;
            if (!TrySample(position, StartSampleRadius, StartRecoveryRadius, areaMask, out var start, out var startMask))
            {
                FailPlan(plan, "start-off-mesh", position, goalPosition, permissions, NavMeshPathStatus.PathInvalid);
                return;
            }

            // A bot standing inside a keycard doorway must still be able to leave it.
            areaMask |= startMask;

            if (!TrySample(goalPosition, GoalSampleRadius, GoalRecoveryRadius, NavMesh.AllAreas, out var goal, out _))
            {
                FailPlan(plan, "goal-off-mesh", position, goalPosition, permissions, NavMeshPathStatus.PathInvalid);
                return;
            }

            plan.GoalOffMesh = HorizontalDistance(goal, goalPosition) > 0.3f || Mathf.Abs(goal.y - goalPosition.y) > 0.6f;

            if (!NavMesh.CalculatePath(start, goal, areaMask, path) || path.status == NavMeshPathStatus.PathInvalid)
            {
                if (service.IsReconciling)
                {
                    // Tiles are being swapped; not a verdict about the goal.
                    plan.Policy.NoteTransientFailure();
                    return;
                }

                FailPlan(plan, "path-invalid", position, goalPosition, permissions, path.status);
                return;
            }

            var count = path.GetCornersNonAlloc(cornerBuffer);
            if (count >= MaxCorners)
            {
                plan.Corners.AddRange(path.corners);
            }
            else
            {
                for (var index = 0; index < count; index++)
                {
                    plan.Corners.Add(cornerBuffer[index]);
                }
            }

            if (plan.Corners.Count == 0)
            {
                FailPlan(plan, "no-corners", position, goalPosition, permissions, path.status);
                return;
            }

            plan.HasPath = path.status == NavMeshPathStatus.PathComplete;
            plan.HasPartialPath = path.status == NavMeshPathStatus.PathPartial;
            if (plan.HasPartialPath && plan.Corners.Count == 1 && HorizontalDistance(plan.Corners[0], position) < CornerArrivalDistance)
            {
                // Nothing reachable beyond the bot's own feet.
                plan.HasPartialPath = false;
                FailPlan(plan, "partial-at-feet", position, goalPosition, permissions, path.status);
                return;
            }

            if (plan.HasPartialPath && Time.time >= nextPlanFailureLogAt)
            {
                nextPlanFailureLogAt = Time.time + 5f;
                LabLogger.Info($"[BotNav] PLAN_PARTIAL bot={BotName} pos={Format(position)} goal={Format(goalPosition)} reachable={Format(plan.Corners[plan.Corners.Count - 1])} corners={plan.Corners.Count} perms=0x{permissions:X} mask=0x{areaMask:X}");
            }

            IndexLinks(plan);
            InsetCorners(plan);
            // Corners lie on the floor; the door/glass/combat line casts expect capsule height.
            var rootHeight = Vector3.up * NavigationAgentProfile.RootHeight;
            plan.Points.Add(position);
            for (var index = 1; index < plan.Corners.Count; index++)
            {
                plan.Points.Add(plan.Corners[index] + rootHeight);
            }

            if (plan.HasPath && plan.GoalOffMesh)
            {
                plan.Points.Add(goalPosition);
            }

            // The first corner is the sampled start itself; skip it unless it is far (recovery).
            plan.CornerIndex = plan.Corners.Count > 1 && HorizontalDistance(plan.Corners[0], position) <= CornerArrivalDistance ? 1 : 0;
            plan.Policy.NotePlanned(Time.time, service.SurfaceGeneration, failed: false);
            if (newGoal)
            {
                ProgressStamp++;
            }
        }

        private void FailPlan(Plan plan, string reason, Vector3 position, Vector3 goalPosition, ushort permissions, NavMeshPathStatus status)
        {
            plan.Policy.NotePlanned(Time.time, service.SurfaceGeneration, failed: true);
            if (Time.time >= nextPlanFailureLogAt)
            {
                nextPlanFailureLogAt = Time.time + 5f;
                LabLogger.Warn($"[BotNav] PLAN_FAILED bot={BotName} reason={reason} status={status} pos={Format(position)} goal={Format(goalPosition)} perms=0x{permissions:X} surfaceGeneration={service.SurfaceGeneration}");
            }
        }

        private void IndexLinks(Plan plan)
        {
            var registry = service.Links;
            if (registry.Count == 0)
            {
                return;
            }

            var corners = plan.Corners;
            for (var index = 0; index < corners.Count - 1; index++)
            {
                if (registry.TryGetLinkAt(corners[index], out var link, out _)
                    && registry.TryGetLinkAt(corners[index + 1], out var nextLink, out _)
                    && link.Group == nextLink.Group)
                {
                    plan.LinkCornerIndices.Add(index);
                    plan.LinkAtCorner.Add(link);
                }
            }
        }

        // String-pulled corners sit exactly on the eroded polygon boundary, i.e. one radius from
        // the wall; nudge intermediate corners into the turn so the capsule does not scrape.
        private static void InsetCorners(Plan plan)
        {
            var corners = plan.Corners;
            for (var index = 1; index < corners.Count - 1; index++)
            {
                if (IsLinkCorner(plan, index))
                {
                    continue;
                }

                var previous = Vector3.ProjectOnPlane(corners[index - 1] - corners[index], Vector3.up);
                var next = Vector3.ProjectOnPlane(corners[index + 1] - corners[index], Vector3.up);
                if (previous.sqrMagnitude < 1e-4f || next.sqrMagnitude < 1e-4f)
                {
                    continue;
                }

                var bisector = previous.normalized + next.normalized;
                if (bisector.sqrMagnitude < 1e-4f)
                {
                    continue;
                }

                corners[index] += bisector.normalized * CornerInsetMeters;
            }
        }

        private static bool IsLinkCorner(Plan plan, int index)
        {
            foreach (var linkIndex in plan.LinkCornerIndices)
            {
                if (index == linkIndex || index == linkIndex + 1)
                {
                    return true;
                }
            }

            return false;
        }

        private void AdvanceCorners(Plan plan, Vector3 position)
        {
            var corners = plan.Corners;
            while (plan.CornerIndex < corners.Count - 1)
            {
                // Elevator crossing: hold at the link start until the elevator behaviors carried the
                // bot to the far landing, then skip past the link.
                if (plan.LinkCornerIndices.Contains(plan.CornerIndex))
                {
                    var destination = corners[plan.CornerIndex + 1];
                    if (HorizontalDistance(position, destination) <= LinkArrivalDistance && Mathf.Abs(position.y - destination.y) <= CornerVerticalTolerance)
                    {
                        plan.CornerIndex += 2;
                        ProgressStamp++;
                        continue;
                    }

                    return;
                }

                var corner = corners[plan.CornerIndex];
                if (HorizontalDistance(position, corner) <= CornerArrivalDistance)
                {
                    // Reached, or a stair landing / ledge placed straight above or below the bot by
                    // the string pull: either way the next corner is the one to steer at.
                    plan.CornerIndex++;
                    ProgressStamp++;
                    continue;
                }

                // Native collision can stop the capsule a hair short of a corner it already passed
                // laterally; when the bot is nearer the following corner than the corner itself is,
                // move on instead of grinding.
                if (plan.CornerIndex + 1 < corners.Count
                    && !plan.LinkCornerIndices.Contains(plan.CornerIndex + 1)
                    && HorizontalDistance(position, corner) <= 1f
                    && HorizontalDistance(position, corners[plan.CornerIndex + 1]) + 0.25f < HorizontalDistance(corner, corners[plan.CornerIndex + 1]))
                {
                    plan.CornerIndex++;
                    ProgressStamp++;
                    continue;
                }

                return;
            }
        }

        private static bool TrySample(Vector3 position, float radius, float recoveryRadius, int areaMask, out Vector3 sampled, out int hitMask)
        {
            if (NavMesh.SamplePosition(position, out var hit, radius, areaMask))
            {
                sampled = hit.position;
                hitMask = hit.mask;
                return true;
            }

            if (areaMask != NavMesh.AllAreas && NavMesh.SamplePosition(position, out hit, radius, NavMesh.AllAreas))
            {
                sampled = hit.position;
                hitMask = hit.mask;
                return true;
            }

            if (NavMesh.SamplePosition(position, out hit, recoveryRadius, NavMesh.AllAreas))
            {
                sampled = hit.position;
                hitMask = hit.mask;
                return true;
            }

            sampled = default;
            hitMask = 0;
            return false;
        }

        private ushort ResolvePermissions()
        {
            var hub = botPlayer.BotHub?.PlayerHub;
            if (hub == null)
            {
                return 0;
            }

            var permissions = DoorPermissionFlags.None;
            if (hub.roleManager?.CurrentRole is IDoorPermissionProvider roleProvider)
            {
                permissions |= roleProvider.GetPermissions(null);
            }

            var items = hub.inventory?.UserInventory?.Items;
            if (items != null)
            {
                foreach (var item in items.Values)
                {
                    if (item is IDoorPermissionProvider provider)
                    {
                        permissions |= provider.GetPermissions(null);
                    }
                }
            }

            return (ushort)permissions;
        }

        private bool IsBypass()
        {
            var hub = botPlayer.BotHub?.PlayerHub;
            return hub != null && hub.serverRoles != null && hub.serverRoles.BypassMode;
        }

        private void NoteReplan()
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
                LabLogger.Warn($"[BotNav] REPLAN_STORM bot={BotName} replansPerSecond={replansInWindow} pos={Format(botPlayer.PlayerPosition)} goal={Format(current.Goal)} state={DescribeState()} recentGoals=[{Format(recentGoals[0])},{Format(recentGoals[1])},{Format(recentGoals[2])},{Format(recentGoals[3])}] surfaceGeneration={service.SurfaceGeneration}");
            }
        }

        private string BotName => botPlayer.BotHub?.PlayerHub?.nicknameSync?.MyNick ?? "?";

        private static float HorizontalDistance(Vector3 a, Vector3 b)
            => Vector3.Distance(Vector3.ProjectOnPlane(a, Vector3.up), Vector3.ProjectOnPlane(b, Vector3.up));

        private static string Format(Vector3 value) => $"({value.x:F1},{value.y:F1},{value.z:F1})";
    }
}
