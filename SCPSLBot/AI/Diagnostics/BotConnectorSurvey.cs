using Interactables.Interobjects;
using Interactables.Interobjects.DoorUtils;
using LabLogger = LabApi.Features.Console.Logger;
using MapGeneration;
using MapGeneration.RoomConnectors;
using MEC;
using PlayerRoles;
using PlayerRoles.FirstPersonControl;
using SCPSLBot.Navigation;
using SCPSLBot.Navigation.Policy;
using UnityEngine.AI;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace SCPSLBot.AI.Diagnostics
{
    /// <summary>
    /// Diagnostic traversal survey. Teleports one spike bot to a navmesh cell on one side of every
    /// selected room connector (clutter passages, open hallways, plain doors) and issues a native
    /// movement order to a cell on the other side. Only setup uses teleportation; every traversal
    /// is real native FPC walking. Each case logs a grep-friendly [BotSurvey] line and the summary
    /// aggregates verdicts per connector type so stuck cases are reproducible per map seed.
    /// </summary>
    internal static class BotConnectorSurvey
    {
        public enum Scope
        {
            Clutter,
            Doors,
            All,
            /// <summary>Runtime backend only: asserts keycard-aware routing with path queries, no walking.</summary>
            Keycard,
        }

        private const float ProbeDistance = 3.5f;
        private const float MaxCellDistanceFromProbe = 6.5f;
        private const float MaxCellHeightDifference = 2.5f;
        private const float SettleSeconds = 0.9f;
        private const float CaseTimeoutSeconds = 25f;

        private sealed class SurveyCase
        {
            public string ConnectorType;
            public string ConnectorName;
            public RoomIdentifier From;
            public RoomIdentifier To;
            public Vector3 Center;
            public Vector3 Forward;
            public string Direction;
        }

        private static ReferenceHub bot;
        private static CoroutineHandle handle;
        private static bool running;
        private static Scope scope;
        private static bool bothDirections;
        private static int totalCases;
        private static int doneCases;
        private static int passedCases;
        private static int failedCases;
        private static int skippedCases;
        private static string currentCase = "none";
        private static string lastResult = "none";
        private static readonly Dictionary<string, (int Passed, int Total)> resultsByType = new(StringComparer.Ordinal);
        private static readonly List<string> roomsWithoutMesh = new();
        private static readonly List<string> failures = new();

        public static bool IsRunning => running;

        public static bool Start(ReferenceHub spikeBot, Scope requestedScope, bool both, out string response)
        {
            if (spikeBot == null || !BotManager.Instance.BotPlayers.ContainsKey(spikeBot))
            {
                response = "No spike bot. Run botspike start first.";
                return false;
            }

            if (!NavigationSystem.Instance.IsReadyForCurrentMap)
            {
                response = "Navigation is not ready for the current map.";
                return false;
            }

            Stop();
            bot = spikeBot;
            scope = requestedScope;
            bothDirections = both;
            totalCases = doneCases = passedCases = failedCases = skippedCases = 0;
            currentCase = "none";
            lastResult = "none";
            resultsByType.Clear();
            roomsWithoutMesh.Clear();
            failures.Clear();

            if (requestedScope == Scope.Keycard)
            {
                if (NavigationSystem.Instance.Backend?.Name != "runtime")
                {
                    response = "The keycard survey needs the runtime navigation backend.";
                    return false;
                }

                running = true;
                handle = Timing.RunCoroutine(RunKeycardCases());
                response = "Started keycard routing survey. Watch [BotSurvey] KEYCARD lines or botspike survey_status.";
                return true;
            }

            var cases = BuildCases();
            totalCases = cases.Count;
            LogRoomCoverage();
            running = true;
            handle = Timing.RunCoroutine(RunCases(cases));
            response = $"Started {requestedScope} survey with {cases.Count} cases ({(both ? "both directions" : "forward only")}). Watch [BotSurvey] log lines or botspike survey_status.";
            return true;
        }

        public static void Stop()
        {
            if (running)
            {
                Timing.KillCoroutines(handle);
                running = false;
                if (bot != null)
                {
                    BotManager.Instance.StopOrder(bot, "survey-stopped");
                }
            }
        }

        public static string Status()
        {
            var byType = string.Join(",", resultsByType
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}:{pair.Value.Passed}/{pair.Value.Total}"));
            return $"running={running} scope={scope} both={bothDirections} cases={totalCases} done={doneCases} passed={passedCases} failed={failedCases} skipped={skippedCases} "
                   + $"noMeshRooms={roomsWithoutMesh.Count} noMeshForms={(roomsWithoutMesh.Count == 0 ? "none" : string.Join(",", roomsWithoutMesh.Distinct()))} "
                   + $"byType={(byType.Length == 0 ? "none" : byType)} current={currentCase} last={lastResult} "
                   + $"failures={(failures.Count == 0 ? "none" : string.Join(",", failures))}";
        }

        private static void LogRoomCoverage()
        {
            var backend = NavigationSystem.Instance.Backend;
            foreach (var room in RoomIdentifier.AllRoomIdentifiers)
            {
                if (room == null)
                {
                    continue;
                }

                var form = NavigationForms.Normalize(room.gameObject.name);
                if (!backend.RoomHasNavigation(room) && room.Name != RoomName.Pocket)
                {
                    roomsWithoutMesh.Add(form);
                    LabLogger.Warn($"[BotSurvey] ROOM_NO_MESH form={form} name={room.Name} zone={room.Zone} pos={Format(room.transform.position)}");
                }
            }

            LabLogger.Info($"[BotSurvey] ROOMS total={RoomIdentifier.AllRoomIdentifiers.Count} withoutMesh={roomsWithoutMesh.Count}");
        }

        private static List<SurveyCase> BuildCases()
        {
            var cases = new List<SurveyCase>();
            if (scope is Scope.Clutter or Scope.All)
            {
                var connectors = UnityEngine.Object.FindObjectsByType<SpawnableRoomConnector>(FindObjectsSortMode.None)
                    .Where(connector => connector != null && connector.GetComponentInChildren<DoorVariant>() == null)
                    .OrderBy(connector => connector.transform.position.x)
                    .ThenBy(connector => connector.transform.position.z);
                foreach (var connector in connectors)
                {
                    var rooms = ResolveRooms(connector.gameObject, connector.transform);
                    if (rooms == null)
                    {
                        LabLogger.Warn($"[BotSurvey] CONNECTOR_UNRESOLVED type={connector.SpawnData.ConnectorType} pos={Format(connector.transform.position)}");
                        continue;
                    }

                    AddCases(cases, connector.SpawnData.ConnectorType.ToString(), connector.name, connector.transform, rooms.Value.Forward, rooms.Value.Back);
                }
            }

            if (scope is Scope.Doors or Scope.All)
            {
                foreach (var door in DoorVariant.AllDoors.OrderBy(d => d.transform.position.x).ThenBy(d => d.transform.position.z))
                {
                    if (door == null || door is ElevatorDoor || door is DummyDoor || door is BasicNonInteractableDoor
                        || door.Rooms == null || door.Rooms.Length != 2
                        || door.RequiredPermissions.RequiredPermissions != DoorPermissionFlags.None)
                    {
                        continue;
                    }

                    var rooms = ResolveRooms(door.gameObject, door.transform);
                    if (rooms == null)
                    {
                        continue;
                    }

                    AddCases(cases, $"Door:{door.GetType().Name}", door.name, door.transform, rooms.Value.Forward, rooms.Value.Back);
                }
            }

            return cases;
        }

        private static void AddCases(List<SurveyCase> cases, string type, string name, Transform transform, RoomIdentifier forwardRoom, RoomIdentifier backRoom)
        {
            var center = transform.position + Vector3.up;
            var forward = Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized;
            cases.Add(new SurveyCase
            {
                ConnectorType = type,
                ConnectorName = name,
                From = backRoom,
                To = forwardRoom,
                Center = center,
                Forward = forward,
                Direction = "fwd",
            });

            if (bothDirections)
            {
                cases.Add(new SurveyCase
                {
                    ConnectorType = type,
                    ConnectorName = name,
                    From = forwardRoom,
                    To = backRoom,
                    Center = center,
                    Forward = -forward,
                    Direction = "back",
                });
            }
        }

        // Resolves the room on each side of a connector by position, so the survey does not depend
        // on native room registration order. Forward = the room reached along transform.forward.
        private static (RoomIdentifier Forward, RoomIdentifier Back)? ResolveRooms(GameObject connector, Transform transform)
        {
            var center = transform.position + Vector3.up;
            var forward = Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized;
            RoomIdentifier forwardRoom = null;
            RoomIdentifier backRoom = null;
            for (var distance = 1.5f; distance <= 4.5f && (forwardRoom == null || backRoom == null); distance += 1.5f)
            {
                if (forwardRoom == null && RoomUtils.TryGetRoom(center + forward * distance, out var f) && f != null)
                {
                    forwardRoom = f;
                }

                if (backRoom == null && RoomUtils.TryGetRoom(center - forward * distance, out var b) && b != null)
                {
                    backRoom = b;
                }
            }

            if (forwardRoom == null || backRoom == null || forwardRoom == backRoom)
            {
                return null;
            }

            return (forwardRoom, backRoom);
        }

        private static IEnumerator<float> RunCases(List<SurveyCase> cases)
        {
            var started = Time.time;
            LabLogger.Info($"[BotSurvey] START scope={scope} both={bothDirections} cases={cases.Count} bot={BotName()} controller={DescribeController()}");

            for (var index = 0; index < cases.Count; index++)
            {
                if (bot == null || !BotManager.Instance.BotPlayers.ContainsKey(bot))
                {
                    LabLogger.Error("[BotSurvey] ABORT reason=bot-lost");
                    break;
                }

                var surveyCase = cases[index];
                currentCase = $"{index + 1}/{cases.Count}:{surveyCase.ConnectorType}:{surveyCase.Direction}";
                var fromForm = NavigationForms.Normalize(surveyCase.From.gameObject.name);
                var toForm = NavigationForms.Normalize(surveyCase.To.gameObject.name);
                var prefix = $"[BotSurvey] CASE index={index + 1}/{cases.Count} connector={surveyCase.ConnectorType} name={surveyCase.ConnectorName} from={fromForm} to={toForm} dir={surveyCase.Direction} center={Format(surveyCase.Center)}";

                var startProbe = surveyCase.Center - surveyCase.Forward * ProbeDistance;
                var goalProbe = surveyCase.Center + surveyCase.Forward * ProbeDistance;
                if (!TryGetNearestCellCenter(surveyCase.From, startProbe, out var start))
                {
                    RecordSkip(surveyCase, $"{prefix} verdict=SKIP reason=no-start-cell probe={Format(startProbe)}");
                    continue;
                }

                // Setup only: place the bot on the start cell and let native gravity settle it.
                yield return Timing.WaitUntilDone(Timing.RunCoroutine(EnsureAlive()));
                if (!TryTeleport(start))
                {
                    RecordSkip(surveyCase, $"{prefix} verdict=SKIP reason=teleport-failed start={Format(start)}");
                    continue;
                }

                yield return Timing.WaitForSeconds(SettleSeconds);
                var settled = bot.transform.position;
                if (Vector3.Distance(Vector3.ProjectOnPlane(settled, Vector3.up), Vector3.ProjectOnPlane(start, Vector3.up)) > 1.5f || settled.y < start.y - 2.5f || settled.y > start.y + 3f)
                {
                    RecordSkip(surveyCase, $"{prefix} verdict=SKIP reason=bad-start-settle start={Format(start)} settled={Format(settled)}");
                    continue;
                }

                // The goal is the far-side sample nearest the probe that this (cardless) bot can
                // reach from where it stands; a door that only leads into card-restricted space is
                // not a walking case.
                if (!NavigationSystem.Instance.Backend.TryGetReachableRoomSample(surveyCase.To, settled, goalProbe, MaxCellDistanceFromProbe, MaxCellHeightDifference, BotManager.Instance.BotPlayers.TryGetValue(bot, out var spikeHub) ? spikeHub.FpcPlayer.Navigator.AreaMask : ~0, out var goal))
                {
                    RecordSkip(surveyCase, $"{prefix} verdict=SKIP reason=goal-unreachable-with-bot-permissions probe={Format(goalProbe)} start={Format(settled)}");
                    continue;
                }

                var accepted = BotManager.Instance.IssueMoveOrder(bot, goal, BotOrderKind.MoveTo, $"survey:{surveyCase.ConnectorType}:{surveyCase.Direction}");
                if (!accepted)
                {
                    RecordFailure(surveyCase, "OFF_MESH", $"{prefix} verdict=OFF_MESH start={Format(settled)} goal={Format(goal)}");
                    continue;
                }

                var caseStarted = Time.time;
                var timedOut = false;
                while (bot != null && BotManager.Instance.TryGetOrderStatus(bot, out var active) && active.IsActive)
                {
                    if (Time.time - caseStarted >= CaseTimeoutSeconds)
                    {
                        timedOut = true;
                        BotManager.Instance.StopOrder(bot, "survey-timeout");
                        break;
                    }

                    if (bot.roleManager?.CurrentRole is not FpcStandardRoleBase)
                    {
                        BotManager.Instance.StopOrder(bot, "survey-died");
                        break;
                    }

                    yield return Timing.WaitForSeconds(0.25f);
                }

                if (bot == null || !BotManager.Instance.TryGetOrderStatus(bot, out var status))
                {
                    RecordFailure(surveyCase, "LOST", $"{prefix} verdict=LOST");
                    continue;
                }

                var elapsed = Time.time - caseStarted;
                var details = $"elapsed={elapsed:F1} stalls={status.StallCount} remaining={status.DistanceRemaining:F2} maxTick={status.MaxTickDistance:F3} groundMisses={status.GroundProbeMisses} blocker={status.LastBlocker} start={Format(settled)} goal={Format(goal)} end={Format(bot.transform.position)} endRoom={status.Room}";
                if (status.CurrentOrder != BotOrderKind.Completed)
                {
                    details += $" sweep=[{DescribeSweep(surveyCase.Center - Vector3.up, surveyCase.Forward)}]";
                }
                if (bot.roleManager?.CurrentRole is not FpcStandardRoleBase)
                {
                    RecordFailure(surveyCase, "DIED", $"{prefix} verdict=DIED {details}");
                }
                else if (timedOut)
                {
                    RecordFailure(surveyCase, "TIMEOUT", $"{prefix} verdict=TIMEOUT {details}");
                }
                else if (status.CurrentOrder == BotOrderKind.Completed)
                {
                    RecordPass(surveyCase, $"{prefix} verdict=PASS {details}");
                }
                else
                {
                    RecordFailure(surveyCase, status.CurrentOrder.ToString(), $"{prefix} verdict={status.CurrentOrder} reason={status.FailureReason} {details}");
                }

                yield return Timing.WaitForSeconds(0.25f);
            }

            running = false;
            currentCase = "none";
            var byType = string.Join(" ", resultsByType
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}={pair.Value.Passed}/{pair.Value.Total}"));
            LabLogger.Info($"[BotSurvey] SUMMARY scope={scope} cases={totalCases} passed={passedCases} failed={failedCases} skipped={skippedCases} noMeshRooms={roomsWithoutMesh.Count} elapsed={Time.time - started:F1} byType=[{byType}] failures=[{string.Join(" ", failures)}]");
        }

        // For every keycard door on the map: a bot without the card must not receive a complete
        // path across the doorway, while the required permissions must yield one. Both queries use
        // the real navmesh area masks, exactly as the bot navigator builds them.
        private static IEnumerator<float> RunKeycardCases()
        {
            var runtime = NavigationSystem.Instance.Runtime;
            var doors = DoorVariant.AllDoors
                .Where(d => d != null && d is not ElevatorDoor && d.RequiredPermissions.RequiredPermissions != DoorPermissionFlags.None)
                .OrderBy(d => d.transform.position.x).ThenBy(d => d.transform.position.z)
                .ToList();
            totalCases = doors.Count;
            LabLogger.Info($"[BotSurvey] START scope=Keycard cases={doors.Count} doorClasses={runtime.Areas.ClassCount} modifierBoxes={runtime.ModifierBoxes}");
            var path = new NavMeshPath();
            var index = 0;
            foreach (var door in doors)
            {
                index++;
                currentCase = $"{index}/{doors.Count}:Keycard";
                var policy = door.RequiredPermissions;
                var center = door.transform.position + Vector3.up * 0.5f;
                var forward = Vector3.ProjectOnPlane(door.transform.forward, Vector3.up).normalized;
                var prefix = $"[BotSurvey] KEYCARD index={index}/{doors.Count} door={door.name} type={door.GetType().Name} perms={policy.RequiredPermissions}{(policy.RequireAll ? "/all" : "/any")} pos={Format(door.transform.position)}";
                var doorPlane = new Plane(forward, center);
                if (!TryGetKeycardAnchor(doorPlane, center, forward, out var a)
                    || !TryGetKeycardAnchor(doorPlane, center, -forward, out var b))
                {
                    RecordSkip(null, $"{prefix} verdict=SKIP reason=no-anchor");
                    continue;
                }

                var plane = new Plane(forward, center);
                if (plane.GetSide(a.position) == plane.GetSide(b.position))
                {
                    RecordSkip(null, $"{prefix} verdict=SKIP reason=anchors-same-side a={Format(a.position)} b={Format(b.position)}");
                    continue;
                }

                var deniedMask = runtime.Areas.BuildAreaMask(0);
                var grantedMask = runtime.Areas.BuildAreaMask((ushort)policy.RequiredPermissions);
                var deniedComplete = NavMesh.CalculatePath(a.position, b.position, deniedMask, path) && path.status == NavMeshPathStatus.PathComplete;
                var deniedLength = PathLength(path);
                var grantedComplete = NavMesh.CalculatePath(a.position, b.position, grantedMask, path) && path.status == NavMeshPathStatus.PathComplete;
                var grantedLength = PathLength(path);
                var grantedEndGap = path.corners.Length > 0 ? Vector3.Distance(path.corners[path.corners.Length - 1], b.position) : -1f;
                var allComplete = NavMesh.CalculatePath(a.position, b.position, NavMesh.AllAreas, path) && path.status == NavMeshPathStatus.PathComplete;
                var details = $"denied={(deniedComplete ? "complete" : "blocked")}/{deniedLength:F1}m granted={(grantedComplete ? "complete" : "blocked")}/{grantedLength:F1}m grantedEndGap={grantedEndGap:F2} allAreas={(allComplete ? "complete" : "blocked")} a={Format(a.position)} b={Format(b.position)} blockers=[{DescribeBlockers(door)}] profile=[{DescribeProfile(door)}] sweep=[{DescribeSweep(door)}]";

                // A denied path may still exist around the door through other rooms; it must then be
                // clearly longer than the direct crossing, which proves the doorway itself is closed.
                var directLength = Vector3.Distance(a.position, b.position);
                var deniedRoutesThroughDoor = deniedComplete && deniedLength < directLength * 1.5f + 2f;
                if (!grantedComplete && !allComplete)
                {
                    // Not a routing verdict: the surface itself does not connect the two anchors here.
                    RecordSkip(null, $"{prefix} verdict=SKIP reason=surface-gap {details}");
                }
                else if (!grantedComplete)
                {
                    RecordFailure(null, "GRANTED_BLOCKED", $"{prefix} verdict=GRANTED_BLOCKED {details}", "Keycard");
                }
                else if (deniedRoutesThroughDoor)
                {
                    RecordFailure(null, "DENIED_PASSED", $"{prefix} verdict=DENIED_PASSED {details}", "Keycard");
                }
                else
                {
                    RecordPass(null, $"{prefix} verdict=PASS {details}", "Keycard");
                }

                if (index % 10 == 0)
                {
                    yield return Timing.WaitForOneFrame;
                }
            }

            running = false;
            currentCase = "none";
            LabLogger.Info($"[BotSurvey] SUMMARY scope=Keycard cases={totalCases} passed={passedCases} failed={failedCases} skipped={skippedCases} noMeshRooms=0 elapsed=0.0 byType=[Keycard={passedCases}/{passedCases + failedCases}] failures=[{string.Join(" ", failures)}]");
        }

        // Colliders that would keep the builder from walking through this doorway: anything in
        // the leaf volume that is not on the excluded Door layer (diagnostics only).
        private static string DescribeBlockers(DoorVariant door)
        {
            var doorLayer = LayerMask.NameToLayer("Door");
            var hits = Physics.OverlapBox(door.transform.position + Vector3.up * 1.2f, new Vector3(1.4f, 1.2f, 0.6f), door.transform.rotation, ~0, QueryTriggerInteraction.Ignore);
            var names = new List<string>();
            foreach (var hit in hits)
            {
                if (hit == null || hit.gameObject.layer == doorLayer)
                {
                    continue;
                }

                var underDoor = hit.transform.IsChildOf(door.transform) ? "door:" : string.Empty;
                names.Add($"{underDoor}{hit.name}/{LayerMask.LayerToName(hit.gameObject.layer)}/{hit.GetType().Name}");
                if (names.Count >= 8)
                {
                    break;
                }
            }

            return string.Join(",", names);
        }

        // Colliders the builder's source mask sees inside a standing capsule at each 0.5 m step
        // along the door axis (-1.5 m .. +1.5 m). "-" = clear.
        private static string DescribeSweep(DoorVariant door) => DescribeSweep(door.transform.position, door.transform.forward);

        private static string DescribeSweep(Vector3 origin, Vector3 axis)
        {
            var mask = SCPSLBot.Navigation.Runtime.NavMeshSourceCollector.SourceLayerMask;
            var radius = NavigationAgentProfile.Radius;
            var parts = new List<string>();
            var forward = Vector3.ProjectOnPlane(axis, Vector3.up).normalized;
            for (var offset = -1.5f; offset <= 1.5f + 1e-3f; offset += 0.5f)
            {
                var foot = origin + forward * offset;
                if (Physics.Raycast(foot + Vector3.up * 1.5f, Vector3.down, out var hit, 4f, mask, QueryTriggerInteraction.Ignore))
                {
                    foot = hit.point;
                }

                var hits = Physics.OverlapCapsule(foot + Vector3.up * (0.35f + radius), foot + Vector3.up * (1.8f - radius), radius, mask, QueryTriggerInteraction.Ignore);
                var names = new List<string>();
                foreach (var candidate in hits)
                {
                    if (candidate != null && names.Count < 3)
                    {
                        var size = candidate.bounds.size;
                        names.Add($"{candidate.transform.root.name}:{candidate.name}/{LayerMask.LayerToName(candidate.gameObject.layer)}/{candidate.GetType().Name}({size.x:F1}x{size.y:F1}x{size.z:F1}@y{candidate.bounds.min.y - foot.y:+0.0;-0.0}..{candidate.bounds.max.y - foot.y:+0.0;-0.0})");
                    }
                }

                parts.Add($"{offset:+0.0;-0.0}:{(names.Count == 0 ? "-" : string.Join("+", names))}");
            }

            return string.Join(" ", parts);
        }

        // Navmesh area under each 0.5 m step along the door axis (-3 m .. +3 m); "x" = no surface.
        private static string DescribeProfile(DoorVariant door)
        {
            var center = door.transform.position + Vector3.up * 0.2f;
            var forward = Vector3.ProjectOnPlane(door.transform.forward, Vector3.up).normalized;
            var parts = new List<string>();
            for (var offset = -3f; offset <= 3f + 1e-3f; offset += 0.5f)
            {
                var probe = center + forward * offset;
                if (NavMesh.SamplePosition(probe, out var hit, 0.6f, NavMesh.AllAreas))
                {
                    var area = 0;
                    while (area < 31 && (hit.mask & (1 << area)) == 0)
                    {
                        area++;
                    }

                    parts.Add($"{area}@{hit.position.y - center.y + 0.2f:+0.00;-0.00}");
                }
                else
                {
                    parts.Add("x");
                }
            }

            return string.Join(" ", parts);
        }

        // The floor sample of the room on that side nearest the 3.5 m probe and on the correct side
        // of the door plane (the raw point can land between the bars of a checkpoint gate or in a
        // cage; a door inside one room needs a sample from each side, not the same one twice).
        private static bool TryGetKeycardAnchor(Plane doorPlane, Vector3 center, Vector3 direction, out NavMeshHit hit)
        {
            hit = default;
            RoomIdentifier room = null;
            for (var distance = 1.5f; distance <= 4.5f && room == null; distance += 1.5f)
            {
                if (RoomUtils.TryGetRoom(center + direction * distance, out var candidate) && candidate != null)
                {
                    room = candidate;
                }
            }

            var probe = center + direction * ProbeDistance;
            var wantedSide = doorPlane.GetSide(probe);
            if (room != null)
            {
                var best = float.PositiveInfinity;
                var found = false;
                var chosen = probe;
                foreach (var sample in NavigationSystem.Instance.Backend.GetRoomSamples(room))
                {
                    if (doorPlane.GetSide(sample) != wantedSide || Mathf.Abs(sample.y - probe.y) > MaxCellHeightDifference)
                    {
                        continue;
                    }

                    var distance = Vector3.Distance(Vector3.ProjectOnPlane(sample, Vector3.up), Vector3.ProjectOnPlane(probe, Vector3.up));
                    if (distance < best && distance <= MaxCellDistanceFromProbe)
                    {
                        best = distance;
                        chosen = sample;
                        found = true;
                    }
                }

                if (found)
                {
                    probe = chosen;
                }
            }

            return NavMesh.SamplePosition(probe, out hit, 3f, NavMesh.AllAreas) && doorPlane.GetSide(hit.position) == wantedSide;
        }

        private static float PathLength(NavMeshPath path)
        {
            var length = 0f;
            var corners = path.corners;
            for (var i = 1; i < corners.Length; i++)
            {
                length += Vector3.Distance(corners[i - 1], corners[i]);
            }

            return length;
        }

        private static IEnumerator<float> EnsureAlive()
        {
            if (bot == null || bot.roleManager?.CurrentRole is FpcStandardRoleBase)
            {
                yield break;
            }

            bot.roleManager?.ServerSetRole(RoleTypeId.ClassD, RoleChangeReason.RemoteAdmin);
            var waited = 0f;
            while (bot != null && bot.roleManager?.CurrentRole is not FpcStandardRoleBase && waited < 5f)
            {
                yield return Timing.WaitForSeconds(0.25f);
                waited += 0.25f;
            }

            // Native spawn placement may have moved the bot; the caller re-teleports afterwards.
            yield return Timing.WaitForSeconds(0.5f);
        }

        private static bool TryTeleport(Vector3 position)
        {
            if (bot?.roleManager?.CurrentRole is not IFpcRole fpcRole)
            {
                return false;
            }

            // Samples may be floor points (runtime backend) or capsule-height cell centers
            // (authored); place the capsule center one root height above the actual floor.
            var mask = FpcStateProcessor.Mask & ~LayerMask.GetMask("Player", "Hitbox", "Door");
            var floor = Physics.Raycast(position + Vector3.up * 1.5f, Vector3.down, out var hit, 4f, mask, QueryTriggerInteraction.Ignore)
                ? hit.point
                : position;
            fpcRole.FpcModule.ServerOverridePosition(floor + Vector3.up * (NavigationAgentProfile.RootHeight + 0.05f));
            return true;
        }

        // Rooms with elevator destinations have navigation on other floors (049 upper area,
        // warhead level); a connector case must start and end on the connector's floor.
        private static bool TryGetNearestCellCenter(RoomIdentifier room, Vector3 probe, out Vector3 center)
        {
            center = default;
            return room != null
                   && NavigationSystem.Instance.Backend.TryGetConnectedRoomSample(room, probe, MaxCellDistanceFromProbe, MaxCellHeightDifference, out center);
        }

        private static void RecordPass(SurveyCase surveyCase, string line, string type = null)
        {
            doneCases++;
            passedCases++;
            Count(type ?? surveyCase.ConnectorType, passed: true);
            lastResult = "PASS";
            LabLogger.Info(line);
        }

        private static void RecordFailure(SurveyCase surveyCase, string verdict, string line, string type = null)
        {
            doneCases++;
            failedCases++;
            Count(type ?? surveyCase.ConnectorType, passed: false);
            lastResult = verdict;
            failures.Add($"{type ?? surveyCase.ConnectorType}/{surveyCase?.Direction ?? "path"}/{verdict}");
            LabLogger.Warn(line);
        }

        private static void RecordSkip(SurveyCase surveyCase, string line)
        {
            doneCases++;
            skippedCases++;
            lastResult = "SKIP";
            LabLogger.Warn(line);
        }

        private static void Count(string type, bool passed)
        {
            resultsByType.TryGetValue(type, out var counts);
            resultsByType[type] = (counts.Passed + (passed ? 1 : 0), counts.Total + 1);
        }

        private static string DescribeController()
        {
            if (bot?.roleManager?.CurrentRole is not IFpcRole fpcRole)
            {
                return "none";
            }

            var settings = fpcRole.FpcModule.CharacterControllerSettings;
            var controller = fpcRole.FpcModule.CharController;
            return $"radius={settings.Radius:F2}/height={settings.Height:F2}/step={settings.StepOffset:F2}/liveStep={controller.stepOffset:F2}/jump={fpcRole.FpcModule.JumpSpeed:F2}";
        }

        private static string BotName() => bot?.nicknameSync?.MyNick ?? "none";
        private static string Format(Vector3 value) => $"({value.x:F2},{value.y:F2},{value.z:F2})";
    }
}
