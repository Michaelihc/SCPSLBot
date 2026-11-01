using Interactables.Interobjects;
using Interactables.Interobjects.DoorUtils;
using LabApi.Features.Wrappers;
using MapGeneration;
using PlayerRoles;
using PlayerRoles.FirstPersonControl;
using PlaytestHarness.Actors;
using PlaytestHarness.Core;
using PlaytestHarness.Probes;
using SCPSLBot.PlaytestScenarios.Harness;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using CheckpointDoor = Interactables.Interobjects.CheckpointDoor;

namespace SCPSLBot.PlaytestScenarios.Scenarios;

/// <summary>Native walking through checkpoint gates, independent of the runtime planning survey.</summary>
public sealed class Issue2CheckpointScenario : Scenario
{
    public override string Name => "scpslbot-issue2-checkpoints";
    public override string[] Suites => ["scpslbot-issue2"];
    public override string Description => "Walks each HCZ/EZ checkpoint lane in both directions with open gates and with an O5 card; observes a denied-card control.";
    public override FidelityRange Supported => FidelityRange.Only(Fidelity.Standard);
    public override bool IncludeInRunAll => false;
    public override float TimeoutSeconds => 900f;

    public override IEnumerator<float> Run(ScenarioContext ctx)
    {
        Issue2World.RequireAuthored(ctx);
        var checkpoints = DoorVariant.AllDoors.OfType<CheckpointDoor>()
            .Where(d => d.Rooms.Any(r => r != null && r.Name.ToString() == "HczCheckpointToEntranceZone"))
            .OrderBy(d => d.transform.position.x).ThenBy(d => d.transform.position.z).ToArray();
        ctx.Require(checkpoints.Length > 0, "map contains HCZ/EZ checkpoint controllers");
        using var pair = new Issue2BotPair(ctx);
        yield return ctx.WaitUntil(pair.CanBind, 12f, "both native SCPSLBot dummies are ready");
        pair.Bind();
        pair.SetRoles(RoleTypeId.Scientist, RoleTypeId.Scientist);
        yield return ctx.WaitUntil(() => pair.Bot.Role == RoleTypeId.Scientist && pair.Target.Role == RoleTypeId.Scientist,
            5f, "checkpoint fixtures have their selected human roles");
        yield return ctx.Wait(1f);

        int tested = 0, failed = 0, skipped = 0, floorControls = 0, failedControls = 0, nativeCrossings = 0;
        foreach (var checkpoint in checkpoints)
        {
            bool original = checkpoint.TargetState;
            float originalOpenTime = checkpoint.SequenceCtrl.OpenLoopTime;
            try
            {
                ctx.Info($"ISSUE2 CHECKPOINT name={checkpoint.name} pos={PlacementProbes.Format(checkpoint.transform.position)} leaves={checkpoint.SubDoors.Length} locks={checkpoint.ActiveLocks} permissions={checkpoint.RequiredPermissions.RequiredPermissions}");
                foreach (int direction in new[] { 1, -1 })
                {
                    var leaf = checkpoint.SubDoors[0];
                    var forward = Vector3.ProjectOnPlane(leaf.transform.forward, Vector3.up).normalized * direction;
                    if (!Issue2World.TryStanding(pair.Bot, leaf.transform.position - forward * 3.5f, out var start)
                        || !Issue2World.TryStanding(pair.Target, start - forward * 3f, out var goal)
                        || Issue2World.CorridorBlockers(start, goal) != "none")
                    {
                        skipped++;
                        ctx.Info($"ISSUE2 CASE checkpoint={checkpoint.name} direction={direction} mode=floor verdict=SETUP_SKIP reason=no-clear-floor-corridor");
                        continue;
                    }
                    ctx.Require(Issue2World.TryStanding(pair.Target, leaf.transform.position + forward * 3.5f, out var farSide),
                        "native checkpoint control has a clear far-side landing");
                    float rootHeight = PlacementProbes.ExpectedRootHeight(((IFpcRole)pair.Bot.RoleBase).FpcModule.CharacterControllerSettings);
                    pair.SetRoles(RoleTypeId.Spectator, RoleTypeId.Spectator);
                    ctx.Arrange("keep the checkpoint open for the native physical control", () =>
                    {
                        checkpoint.SequenceCtrl.OpenLoopTime = 60f;
                        checkpoint.SequenceCtrl.RemainingTime = 0f;
                        checkpoint.CurSequence = CheckpointDoor.SequenceState.Idle;
                        checkpoint.NetworkTargetState = true;
                        checkpoint.ToggleAllDoors(true);
                    });
                    yield return ctx.Wait(1.6f);
                    Actor walker = ctx.SpawnActor("issue2-native-checkpoint-walk", RoleTypeId.Scientist, SpawnSpec.At(start), useMovementProvider: false);
                    yield return walker.WaitReady();
                    var nativeWalk = Actor.NewJumpCourseResult(allowJump: false);
                    yield return walker.TraverseJumpCourse(new[] { start - Vector3.up * rootHeight, farSide - Vector3.up * rootHeight }, nativeWalk,
                        allowJump: false, arrivalRadii: new[] { 0.6f, 0.6f });
                    bool nativeArrived = nativeWalk.CompletedCourse && Issue2World.HorizontalDistance(walker.Position, farSide) <= 0.6f
                        && Mathf.Abs(walker.Position.y - farSide.y) <= 0.25f;
                    ctx.Info($"ISSUE2 NATIVE_CROSSING checkpoint={checkpoint.name} direction={direction} start={PlacementProbes.Format(start)} goal={PlacementProbes.Format(farSide)} landing={PlacementProbes.Format(walker.Position)} verifiedLanding={nativeArrived} {nativeWalk.Summary()}");
                    if (nativeArrived) nativeCrossings++;
                    using (ctx.BeginArrangement("remove the native checkpoint control before bot follow"))
                        yield return walker.ChangeRole(RoleTypeId.Spectator);
                    pair.SetRoles(RoleTypeId.Scientist, RoleTypeId.Scientist);
                    yield return ctx.Wait(0.7f);
                    pair.Place(start, goal);
                    yield return ctx.Wait(0.7f);
                    Issue2World.RequireStanding(ctx, pair.Bot);
                    Issue2World.RequireStanding(ctx, pair.Target);
                    string label = $"checkpoint={checkpoint.name} direction={direction} mode=floor";
                    ctx.Info($"ISSUE2 BEGIN {label} start={PlacementProbes.Format(pair.Bot.Position)} goal={PlacementProbes.Format(pair.Target.Position)} corridor=none");
                    pair.Follow();
                    var walk = new Issue2Walk(pair.Bot, pair.Target.Position);
                    float deadline = Time.realtimeSinceStartup + 10f;
                    while (!walk.Arrived && Time.realtimeSinceStartup < deadline)
                    {
                        walk.Sample(ctx, label);
                        yield return ctx.Wait(0.2f);
                    }
                    walk.Sample(ctx, label);
                    pair.ClearFollow();
                    floorControls++;
                    if (!walk.Arrived || !walk.WorldOk) failedControls++;
                    ctx.Info($"ISSUE2 CASE {label} verdict={(walk.Arrived && walk.WorldOk ? "PASS" : "FAIL")} {walk.Describe()} targetNow={PlacementProbes.Format(pair.Target.Position)}");
                }
                foreach (var leaf in checkpoint.SubDoors)
                foreach (int direction in new[] { 1, -1 })
                foreach (string mode in new[] { "open", "closed-card", "closed-denied" })
                {
                    pair.ClearFollow();
                    ctx.Arrange("select checkpoint door state and keycard before walking", () =>
                    {
                        pair.Bot.ClearInventory();
                        if (mode == "closed-card") pair.Bot.CurrentItem = pair.Bot.AddItem(ItemType.KeycardO5);
                        checkpoint.SequenceCtrl.OpenLoopTime = mode == "open" ? 60f : originalOpenTime;
                        checkpoint.SequenceCtrl.RemainingTime = 0f;
                        checkpoint.CurSequence = CheckpointDoor.SequenceState.Idle;
                        checkpoint.NetworkTargetState = mode == "open";
                        checkpoint.ToggleAllDoors(mode == "open");
                    });
                    yield return ctx.Wait(1.6f);
                    var forward = Vector3.ProjectOnPlane(leaf.transform.forward, Vector3.up).normalized * direction;
                    if (!Issue2World.TryStanding(pair.Bot, leaf.transform.position - forward * 3.5f, out var start)
                        || !Issue2World.TryStanding(pair.Target, leaf.transform.position + forward * 3.5f, out var goal))
                    {
                        skipped++;
                        ctx.Info($"ISSUE2 CASE checkpoint={checkpoint.name} leaf={leaf.name} direction={direction} mode={mode} verdict=SETUP_SKIP reason=no-clear-grounded-anchor");
                        continue;
                    }
                    pair.Place(start, goal);
                    yield return ctx.Wait(0.7f);
                    Issue2World.RequireStanding(ctx, pair.Bot);
                    Issue2World.RequireStanding(ctx, pair.Target);
                    string label = $"checkpoint={checkpoint.name} leaf={leaf.name} direction={direction} mode={mode}";
                    ctx.Info($"ISSUE2 BEGIN {label} start={PlacementProbes.Format(pair.Bot.Position)} goal={PlacementProbes.Format(pair.Target.Position)} leafOpen={leaf.IsConsideredOpen()} locks={leaf.ActiveLocks} perms={leaf.RequiredPermissions.RequiredPermissions} corridor={Issue2World.CorridorBlockers(start, goal)}");
                    pair.Follow();
                    var walk = new Issue2Walk(pair.Bot, pair.Target.Position);
                    float deadline = Time.realtimeSinceStartup + (mode == "closed-denied" ? 8f : 16f);
                    while (!walk.Arrived && Time.realtimeSinceStartup < deadline)
                    {
                        walk.Sample(ctx, label);
                        yield return ctx.Wait(0.2f);
                    }
                    walk.Sample(ctx, label);
                    pair.ClearFollow();
                    tested++;
                    bool expected = mode == "closed-denied" ? !walk.Arrived : walk.Arrived;
                    if (!expected || !walk.WorldOk) failed++;
                    ctx.Info($"ISSUE2 CASE {label} verdict={(expected && walk.WorldOk ? "PASS" : "FAIL")} {walk.Describe()} targetNow={PlacementProbes.Format(pair.Target.Position)} leafOpen={leaf.IsConsideredOpen()} controllerSequence={checkpoint.CurSequence}");
                }
            }
            finally
            {
                pair.ClearFollow();
                checkpoint.SequenceCtrl.OpenLoopTime = originalOpenTime;
                checkpoint.NetworkTargetState = original;
                checkpoint.ToggleAllDoors(original);
            }
        }
        ctx.Info($"ISSUE2 SUMMARY checkpoints tested={tested} failed={failed} skipped={skipped} floorControls={floorControls} failedControls={failedControls} nativeCrossings={nativeCrossings}");
        ctx.Require(floorControls > 0 && failedControls == 0, $"same-floor native follow controls arrived (tested={floorControls}, failed={failedControls})");
        ctx.Require(tested > 0, "at least one checkpoint lane was exercised natively");
        ctx.Require(failed == 0, $"all checkpoint walk/card controls behaved correctly (failed={failed}, skipped={skipped})");
    }
}

/// <summary>Separates native platform reachability from SCP-049's real combat pursuit.</summary>
public sealed class Issue2ElevatedPursuitScenario : Scenario
{
    public override string Name => "scpslbot-issue2-greenhouse";
    public override string[] Suites => ["scpslbot-issue2"];
    public override string Description => "Probes real LCZ Plants supports, checks native SCP-049 jumps, and compares floor/elevated pursuit of an eligible held bot target.";
    public override FidelityRange Supported => FidelityRange.Only(Fidelity.Standard);
    public override bool IncludeInRunAll => false;
    public override float TimeoutSeconds => 600f;

    public override IEnumerator<float> Run(ScenarioContext ctx)
    {
        Issue2World.RequireAuthored(ctx);
        RoomIdentifier room = RoomIdentifier.AllRoomIdentifiers.FirstOrDefault(r => r != null && r.Name == RoomName.LczGreenhouse)
            ?? throw new RequireException("LCZ Plants room is absent");
        using var pair = new Issue2BotPair(ctx);
        yield return ctx.WaitUntil(pair.CanBind, 12f, "both eligible SCPSLBot dummies are ready");
        pair.Bind();
        yield return ctx.Wait(2f); // Drain the native botspike spawn's delayed ClassD role assignments.
        pair.SetRoles(RoleTypeId.Scp049, RoleTypeId.Scientist);
        yield return ctx.WaitUntil(() => pair.Bot.Role == RoleTypeId.Scp049 && pair.Target.Role == RoleTypeId.Scientist,
            5f, "pursuer and held human target have their selected roles");
        yield return ctx.Wait(1f);
        var supports = Issue2World.ScanSupports(room, pair.Bot);
        ctx.Require(supports.Count > 0, "greenhouse exposed grounded, capsule-clear support points");
        float floorY = supports.Min(p => p.y);
        var floors = supports.Where(p => p.y < floorY + 0.1f).ToArray();
        var elevated = supports.Where(p => p.y > floorY + 0.35f && p.y < floorY + 1.6f)
            .OrderByDescending(p => p.y).ThenBy(p => Issue2World.HorizontalDistance(p, room.transform.position))
            .Aggregate(new List<Vector3>(), (list, point) =>
            {
                if (!list.Any(p => Mathf.Abs(p.y - point.y) < 0.15f && Issue2World.HorizontalDistance(p, point) < 3f)) list.Add(point);
                return list;
            }).Take(12).ToArray();
        ctx.Info($"ISSUE2 SUPPORTS room={room.gameObject.name} origin={PlacementProbes.Format(room.transform.position)} floorY={floorY:F3} samples={supports.Count} elevatedPositions={elevated.Length}");
        foreach (var top in elevated)
            ctx.Info($"ISSUE2 SUPPORT point={PlacementProbes.Format(top)} local={PlacementProbes.Format(room.transform.InverseTransformPoint(top))} rise={top.y - floorY:F3}");
        ctx.Require(floors.Length > 1, "greenhouse has floor controls");
        ctx.Require(elevated.Length > 0, "greenhouse contains an elevated support with capsule clearance");

        int failed = 0, tested = 0, reachable = 0, skipped = 0;
        foreach (var top in elevated)
        {
            var approaches = floors.Where(p => Issue2World.HorizontalDistance(p, top) >= 1.5f && Issue2World.HorizontalDistance(p, top) <= 5f
                    && Issue2World.Visible(p, top)).OrderBy(p => Issue2World.HorizontalDistance(p, top)).ToArray();
            var start = approaches.FirstOrDefault(Issue2World.OnAuthoredMesh);
            if (start == default) start = approaches.FirstOrDefault();
            if (start == default)
            {
                skipped++;
                ctx.Info($"ISSUE2 CASE elevated={PlacementProbes.Format(top)} verdict=SETUP_SKIP reason=no-visible-floor-approach");
                continue;
            }
            var controls = floors.Where(p => Issue2World.HorizontalDistance(p, start) >= 2f && Issue2World.HorizontalDistance(p, start) <= 4f
                    && Issue2World.ClearFloorRoute(start, p, pair.Bot)).OrderBy(p => Issue2World.HorizontalDistance(p, start)).ToArray();
            var floorGoal = controls.FirstOrDefault(Issue2World.OnAuthoredMesh);
            if (floorGoal == default) floorGoal = controls.FirstOrDefault();
            if (floorGoal == default)
            {
                skipped++;
                ctx.Info($"ISSUE2 CASE elevated={PlacementProbes.Format(top)} verdict=SETUP_SKIP reason=no-clear-separated-floor-control");
                continue;
            }
            ctx.Info($"ISSUE2 APPROACH start={PlacementProbes.Format(start)} startOnMesh={Issue2World.OnAuthoredMesh(start)} floor={PlacementProbes.Format(floorGoal)} floorOnMesh={Issue2World.OnAuthoredMesh(floorGoal)} elevated={PlacementProbes.Format(top)} elevatedOnMesh={Issue2World.OnAuthoredMesh(top)}");

            // A plain native dummy is deliberately used for the physical control. It has no bot
            // movement provider, and therefore cannot borrow the subject's navigation implementation.
            pair.SetRoles(RoleTypeId.Spectator, RoleTypeId.Spectator);
            yield return ctx.Wait(0.5f);
            Actor jumper = ctx.SpawnActor("issue2-native-049-jump", RoleTypeId.Scp049, SpawnSpec.At(start), useMovementProvider: false);
            yield return jumper.WaitReady();
            var nativeJump = Actor.NewJumpCourseResult();
            var rootHeight = PlacementProbes.ExpectedRootHeight(((IFpcRole)WarmupBotWorld.FindById(jumper.PlayerId)!.RoleBase).FpcModule.CharacterControllerSettings);
            yield return jumper.TraverseJumpCourse(new[] { start - Vector3.up * rootHeight, top - Vector3.up * rootHeight }, nativeJump,
                arrivalRadii: new[] { 0.6f, 0.6f });
            // The native grounded flag accounts for capsule support at a platform edge, where a
            // centre-only downward ray can miss the ledge despite a real supported landing.
            bool nativeGrounded = WarmupBotWorld.FindById(jumper.PlayerId)!.ReferenceHub.IsGrounded();
            bool nativeReachable = nativeJump.CompletedCourse && Mathf.Abs(jumper.Position.y - top.y) <= 0.25f
                && Issue2World.HorizontalDistance(jumper.Position, top) <= 0.6f && nativeGrounded;
            ctx.Info($"ISSUE2 NATIVE_JUMP top={PlacementProbes.Format(top)} landing={PlacementProbes.Format(jumper.Position)} nativeGrounded={nativeGrounded} verifiedLanding={nativeReachable} {nativeJump.Summary()}");
            if (nativeReachable) reachable++;
            using (ctx.BeginArrangement("remove the physical reachability probe before combat pursuit"))
                yield return jumper.ChangeRole(RoleTypeId.Spectator);
            pair.SetRoles(RoleTypeId.Scp049, RoleTypeId.Scientist);
            yield return ctx.WaitUntil(() => pair.Bot.Role == RoleTypeId.Scp049 && pair.Target.Role == RoleTypeId.Scientist,
                5f, "pursuit fixtures restored after native jump control");
            yield return ctx.Wait(0.7f);

            foreach (string mode in new[] { "floor", "elevated" })
            {
                var goal = mode == "floor" ? floorGoal : top;
                pair.Place(start, goal);
                Issue2World.RequireStanding(ctx, pair.Bot);
                Issue2World.RequireStanding(ctx, pair.Target);
                ctx.Require(Issue2World.Visible(pair.Bot.Position, pair.Target.Position), "held hostile target is visible before pursuit");
                string label = $"greenhouse mode={mode} rise={goal.y - start.y:F3} target={PlacementProbes.Format(goal)}";
                ctx.Info($"ISSUE2 BEGIN {label} bot={pair.Bot.PlayerId} target={pair.Target.PlayerId} start={PlacementProbes.Format(pair.Bot.Position)} goal={PlacementProbes.Format(pair.Target.Position)} probe={NativeCommandAdapter.RemoteAdmin($"nav probe {Issue2World.Arguments(goal)}").Response}");
                var walk = new Issue2Walk(pair.Bot, pair.Target.Position, requireLanding: mode == "elevated");
                float deadline = Time.realtimeSinceStartup + 20f;
                while (!walk.Arrived && Time.realtimeSinceStartup < deadline)
                {
                    walk.Sample(ctx, label);
                    yield return ctx.Wait(0.2f);
                }
                walk.Sample(ctx, label);
                tested++;
                bool reachabilityVerified = mode == "floor" || nativeReachable;
                if ((reachabilityVerified && !walk.Arrived) || !walk.WorldOk) failed++;
                string verdict = !walk.WorldOk ? "FAIL" : !reachabilityVerified ? "UNVERIFIED_REACHABILITY" : walk.Arrived ? "PASS" : "FAIL";
                ctx.Info($"ISSUE2 CASE {label} verdict={verdict} nativeJumpReachable={nativeReachable} {walk.Describe()} targetNow={PlacementProbes.Format(pair.Target.Position)}");
            }
        }
        ctx.Info($"ISSUE2 SUMMARY greenhouse tested={tested} failed={failed} nativeReachablePositions={reachable} setupSkippedPositions={skipped}");
        ctx.Require(tested > 0, "at least one floor/elevated pursuit pair was exercised");
        ctx.Require(failed == 0, $"SCP-049 physically reached each visible target (failed={failed}; native-reachable levels={reachable})");
        if (reachable == 0) ctx.Skip("No elevated landing was cleared by the native jump control; elevated pursuit reachability remains unverified.");
    }
}

internal sealed class Issue2BotPair : IDisposable
{
    private readonly ScenarioContext ctx;
    private readonly int botId;
    private bool following;
    public Player Bot { get; private set; } = null!;
    public Player Target { get; private set; } = null!;
    public Issue2BotPair(ScenarioContext ctx)
    {
        this.ctx = ctx;
        Issue2World.Command(ctx, "botspike cleanup");
        Issue2World.Command(ctx, "botspike start");
        botId = Issue2World.AddBot(ctx);
    }
    public bool CanBind() => WarmupBotWorld.FindById(botId) is { IsReady: true, IsAlive: true }
        && WarmupBotWorld.FindSpike() is { IsReady: true, IsAlive: true };
    public void Bind()
    {
        Bot = WarmupBotWorld.FindById(botId)!;
        Target = WarmupBotWorld.FindSpike()!;
        ctx.Info($"ISSUE2 ACTORS bot={Bot.PlayerId}/{Bot.Nickname} target={Target.PlayerId}/{Target.Nickname} targetOwnedBy=SCPSLBot held=True");
    }
    public void SetRoles(RoleTypeId botRole, RoleTypeId targetRole)
    {
        ClearFollow();
        Issue2World.Command(ctx, $"forcerole {Bot.PlayerId} {botRole}");
        Issue2World.Command(ctx, $"forcerole {Target.PlayerId} {targetRole}");
        ctx.Arrange("keep navigation fixtures alive throughout the observation", () =>
        {
            Bot.IsGodModeEnabled = true;
            Target.IsGodModeEnabled = true;
        });
    }
    public void Place(Vector3 start, Vector3 goal) => ctx.Arrange("validated fast-travel to slice start and stationary goal", () =>
    {
        Issue2World.Place(ctx, Bot, start);
        Issue2World.Place(ctx, Target, goal);
    });
    public void Follow()
    {
        var result = NativeCommandAdapter.RemoteAdminAsPlayer("bot_follow", Target.ReferenceHub);
        ctx.Require(result.Success, $"native bot_follow command succeeded: {result}");
        following = true;
    }
    public void ClearFollow()
    {
        if (!following) return;
        NativeCommandAdapter.RemoteAdminAsPlayer("bot_follow", Target.ReferenceHub);
        following = false;
    }
    public void Dispose()
    {
        ClearFollow();
        if (Bot != null && !Bot.IsDestroyed) NativeCommandAdapter.RemoteAdmin($"dummies destroy {Bot.PlayerId}");
        NativeCommandAdapter.RemoteAdmin("botspike cleanup");
    }
}

internal sealed class Issue2Walk
{
    private readonly Player bot;
    private readonly Vector3 goal;
    private readonly Vector3 start;
    private Vector3 previous;
    private float nextLog;
    private float maxStep;
    private float maxRise;
    private readonly bool requireLanding;
    public bool Arrived => Issue2World.HorizontalDistance(bot.Position, goal) <= 1.4f
        && Mathf.Abs(bot.Position.y - goal.y) <= (requireLanding ? 0.25f : 0.45f)
        && (!requireLanding || bot.ReferenceHub.IsGrounded());
    public bool WorldOk { get; private set; } = true;
    public Issue2Walk(Player bot, Vector3 goal, bool requireLanding = false)
    { this.bot = bot; this.goal = goal; this.requireLanding = requireLanding; start = previous = bot.Position; }
    public void Sample(ScenarioContext ctx, string label)
    {
        var point = bot.Position;
        maxStep = Mathf.Max(maxStep, Vector3.Distance(point, previous));
        maxRise = Mathf.Max(maxRise, point.y - start.y);
        WorldOk &= bot.IsAlive && PlacementProbes.Ground(point, 3f).Ok && maxStep < 2.5f;
        previous = point;
        if (Time.realtimeSinceStartup < nextLog) return;
        nextLog = Time.realtimeSinceStartup + 1f;
        ctx.Info($"ISSUE2 TRACE {label} {Describe()}");
    }
    public string Describe() => $"arrived={Arrived} worldOk={WorldOk} nativeGrounded={bot.ReferenceHub.IsGrounded()} requireLanding={requireLanding} pos={PlacementProbes.Format(bot.Position)} horizontalGap={Issue2World.HorizontalDistance(bot.Position, goal):F2} verticalGap={Mathf.Abs(bot.Position.y - goal.y):F2} displacement={Vector3.Distance(start, bot.Position):F2} maxRise={maxRise:F2} maxSampleStep={maxStep:F2}";
}

internal static class Issue2World
{
    public static void RequireAuthored(ScenarioContext ctx)
    {
        var status = RequireReady(ctx);
        var nav = Command(ctx, "nav status");
        ctx.Require(nav.Response.Contains("active_backend=authored"), $"Authored backend is active: {nav.Response}");
        ctx.Info($"ISSUE2 BASELINE {status} nav={nav.Response}");
    }
    public static int AddBot(ScenarioContext ctx)
    {
        NativeCommandResult result = Command(ctx, "bot_add");
        var match = System.Text.RegularExpressions.Regex.Match(result.Response, @"player_id=(\d+)");
        ctx.Require(match.Success, $"bot_add reports the new bot's player_id: {result.Response}");
        return int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    }
    // Standalone AI reports no management fields, so this reads only what SCPSLBot itself owns.
    public static string RequireReady(ScenarioContext ctx)
    {
        string status = Command(ctx, "bot_status").Response;
        ctx.Require(status.Contains("nav_ready=True") && status.Contains("ai_runner_running=True"),
            $"current-map navigation and AI runner are ready: {status}");
        return status;
    }
    public static NativeCommandResult Command(ScenarioContext ctx, string query)
    {
        var result = NativeCommandAdapter.RemoteAdmin(query);
        ctx.Require(result.Success, $"native command {query}: {result}");
        return result;
    }
    public static string Arguments(Vector3 p) => string.Format(CultureInfo.InvariantCulture, "{0:F3} {1:F3} {2:F3}", p.x, p.y, p.z);
    public static float HorizontalDistance(Vector3 a, Vector3 b) => Vector3.Distance(Vector3.ProjectOnPlane(a, Vector3.up), Vector3.ProjectOnPlane(b, Vector3.up));
    public static bool OnAuthoredMesh(Vector3 point)
    {
        var result = NativeCommandAdapter.RemoteAdmin($"nav probe {Arguments(point)}");
        return result.Success && result.Response.Contains("on_mesh=True");
    }
    public static bool ClearFloorRoute(Vector3 start, Vector3 goal, Player actor)
    {
        var settings = ((IFpcRole)actor.RoleBase).FpcModule.CharacterControllerSettings;
        int steps = Mathf.CeilToInt(HorizontalDistance(start, goal) / 0.2f);
        for (int i = 0; i <= steps; i++)
        {
            var point = Vector3.Lerp(start, goal, (float)i / steps);
            if (!PlacementProbes.Capsule(point, settings, actor.ReferenceHub).Ok
                || !PlacementProbes.TryFindGround(point, 2f, out var floor, out _)
                || Mathf.Abs(point.y - floor - PlacementProbes.ExpectedRootHeight(settings)) > 0.1f) return false;
        }
        return true;
    }
    public static bool Visible(Vector3 a, Vector3 b) => !Physics.Linecast(a + Vector3.up * 0.6f, b + Vector3.up * 0.6f,
        LayerMask.GetMask("Default", "Door", "InteractableNoPlayerCollision", "Glass"), QueryTriggerInteraction.Ignore);
    public static bool TryStanding(Player actor, Vector3 probe, out Vector3 point)
    {
        var settings = ((IFpcRole)actor.RoleBase).FpcModule.CharacterControllerSettings;
        foreach (var offset in new[] { Vector3.zero, Vector3.right * 0.5f, Vector3.left * 0.5f, Vector3.forward * 0.5f, Vector3.back * 0.5f })
        {
            if (!PlacementProbes.TryFindGround(probe + offset + Vector3.up * 2f, 5f, out var y, out _)) continue;
            point = new Vector3(probe.x + offset.x, y + PlacementProbes.ExpectedRootHeight(settings), probe.z + offset.z);
            if (PlacementProbes.ValidateTeleportTarget(point, settings, 2f, actor.ReferenceHub).Ok) return true;
        }
        point = default;
        return false;
    }
    public static void Place(ScenarioContext ctx, Player actor, Vector3 point)
    {
        var settings = ((IFpcRole)actor.RoleBase).FpcModule.CharacterControllerSettings;
        var probe = PlacementProbes.ValidateTeleportTarget(point, settings, 2f, actor.ReferenceHub);
        ctx.Require(probe.Ok, $"fixture placement for {actor.PlayerId}: {probe}");
        actor.Position = point;
    }
    public static void RequireStanding(ScenarioContext ctx, Player actor)
    {
        ctx.Require(actor.IsAlive && PlacementProbes.Ground(actor.Position, 2f).Ok, $"fixture {actor.PlayerId} is alive and grounded at {PlacementProbes.Format(actor.Position)}");
    }
    public static List<Vector3> ScanSupports(RoomIdentifier room, Player actor)
    {
        var settings = ((IFpcRole)actor.RoleBase).FpcModule.CharacterControllerSettings;
        var result = new List<Vector3>();
        for (float x = -7f; x <= 7f; x += 0.5f)
        for (float z = -7f; z <= 7f; z += 0.5f)
        {
            var probe = room.transform.TransformPoint(new Vector3(x, 3f, z));
            if (!PlacementProbes.TryFindGround(probe, 5f, out var y, out _)) continue;
            var point = new Vector3(probe.x, y + PlacementProbes.ExpectedRootHeight(settings), probe.z);
            if (!RoomUtils.TryGetRoom(point, out var found) || found != room) continue;
            if (PlacementProbes.ValidateTeleportTarget(point, settings, 2f, actor.ReferenceHub).Ok) result.Add(point);
        }
        return result;
    }
    public static string CorridorBlockers(Vector3 start, Vector3 goal) => string.Join(",", Physics.Linecast(start, goal, out var hit,
        FpcStateProcessor.Mask, QueryTriggerInteraction.Ignore) ? new[] { hit.collider.name } : new[] { "none" });
}
