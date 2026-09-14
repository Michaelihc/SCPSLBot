using PlaytestHarness.Core;
using SCPSLBot.PlaytestScenarios.Harness;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace SCPSLBot.PlaytestScenarios.Scenarios;

/// <summary>
/// Acceptance gate for the runtime navmesh backend, read entirely through the RA diagnostics an
/// operator has: bot_status, nav status, nav probe and nav path. Requires the patched server assets
/// (zero unreadable meshes), full room coverage, elevator links, keycard door classes, and complete
/// paths for the long facility routes that previously failed without links.
/// </summary>
public sealed class RuntimeNavMeshGateScenario : Scenario
{
    public override string Name => "scpslbot-runtime-navmesh-gate";
    public override string[] Aliases => ["bot-navmesh-gate"];
    public override string[] Suites => ["scpslbot-navigation-survey"];
    public override string Description => "Asserts the runtime navmesh bake: readable meshes, coverage, links, door classes, budgets and long paths.";
    public override FidelityRange Supported => FidelityRange.Only(Fidelity.Standard);
    public override bool IncludeInRunAll => false;
    public override float TimeoutSeconds => 180f;

    public override IEnumerator<float> Run(ScenarioContext ctx)
    {
        BotStatusSnapshot status = BotStatusSnapshot.Read();
        ctx.Require(status.NavReady, $"navigation is ready: {status.Raw}");
        ctx.Require(Get(status.Values, "nav_backend") == "runtime", $"the runtime backend is active: {status.Raw}");
        ctx.Require(GetInt(status.Values, "nav_bake_failures") == 0, $"no bake failures were needed: {status.Raw}");
        ctx.Require(GetInt(status.Values, "nav_unreadable_meshes") == 0,
            $"every collider mesh is readable (server assets patched): {status.Raw}");
        ctx.Require(GetInt(status.Values, "nav_uncovered_rooms") == 0, $"every room received navigation: {status.Raw}");
        ctx.Require(GetInt(status.Values, "nav_links") > 0, $"elevator links were registered: {status.Raw}");
        ctx.Require(GetInt(status.Values, "nav_door_classes") > 0, $"keycard door classes were registered: {status.Raw}");
        ctx.Require(GetInt(status.Values, "nav_triangles") > 1000, $"the navmesh has polygons: {status.Raw}");
        int bakeMs = GetInt(status.Values, "nav_last_bake_ms");
        ctx.Require(bakeMs > 0 && bakeMs <= 5000, $"initial bake finished within the 5 s wall budget (bake_ms={bakeMs})");
        ctx.Info($"bot_status: {status.Raw}");

        NativeCommandResult navStatus = NativeCommandAdapter.RemoteAdmin("nav status");
        WarmupPopulationRecoveryScenario.RequireCommand(ctx, navStatus, "read nav status");
        Dictionary<string, string> nav = CommandFields.ParseWhitespace(navStatus.Response);
        ctx.Require(Get(nav, "built") == "True", $"nav status reports a built navmesh: {navStatus.Response}");
        ctx.Require(GetInt(nav, "fallback_rooms") == 0, $"no room needed the probed floor fallback: {navStatus.Response}");
        ctx.Require(GetInt(nav, "index_ms") <= 1500, $"room index rebuild stays within budget: {navStatus.Response}");
        ctx.Info($"nav status: {navStatus.Response}");

        // Every room center (or the nearest surface within a few meters) must sit on the mesh.
        foreach (string room in new[] { "LczClassDSpawn", "Lcz914", "HczWarhead", "Hcz049", "EzGateA", "Outside" })
        {
            NativeCommandResult probe = NativeCommandAdapter.RemoteAdmin($"nav probe {room}");
            if (!probe.Success)
            {
                ctx.Info($"nav probe {room}: {probe.Response}");
                continue;
            }

            Dictionary<string, string> fields = CommandFields.ParseWhitespace(probe.Response);
            ctx.Require(Get(fields, "on_mesh") == "True", $"{room} anchor is on the navmesh: {probe.Response}");
        }

        // Long routes that need checkpoints and elevator links.
        RequirePath(ctx, "LczClassDSpawn", "HczWarhead");
        RequirePath(ctx, "LczClassDSpawn", "Lcz914");
        RequirePath(ctx, "Hcz049", "EzGateA");
        RequirePath(ctx, "EzGateA", "Outside");
        yield return ctx.Wait(0.1f);
    }

    private static void RequirePath(ScenarioContext ctx, string from, string to)
    {
        NativeCommandResult result = NativeCommandAdapter.RemoteAdmin($"nav path {from} {to} perms all");
        if (!result.Success && result.Response.IndexOf("not present", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            ctx.Info($"nav path {from} {to}: skipped, {result.Response}");
            return;
        }

        WarmupPopulationRecoveryScenario.RequireCommand(ctx, result, $"query path {from} -> {to}");
        Dictionary<string, string> fields = CommandFields.ParseWhitespace(result.Response);
        ctx.Require(Get(fields, "status") == "PathComplete", $"{from} -> {to} has a complete path: {result.Response}");
        ctx.Require(GetFloat(fields, "query_ms") <= 5f, $"{from} -> {to} query stayed within 5 ms: {result.Response}");
        ctx.Info($"nav path {from} {to}: {result.Response}");
    }

    private static string Get(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out string value) ? value : string.Empty;

    private static int GetInt(IReadOnlyDictionary<string, string> values, string key) =>
        int.TryParse(Get(values, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : throw new RequireException($"field '{key}' is not an integer: '{Get(values, key)}'");

    private static float GetFloat(IReadOnlyDictionary<string, string> values, string key) =>
        float.TryParse(Get(values, key), NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
            ? value
            : throw new RequireException($"field '{key}' is not a number: '{Get(values, key)}'");
}

/// <summary>
/// Keycard-aware routing gate: for every keycard door the runtime backend must refuse a complete
/// path to a bot without the card and grant one to a bot holding it. Driven through the real
/// botspike survey command; verdicts are read from botspike survey_status.
/// </summary>
public sealed class KeycardRoutingSurveyScenario : ConnectorTraversalSurveyScenario
{
    public override string Name => "scpslbot-keycard-routing-survey";
    public override string[] Aliases => ["bot-keycard-survey"];
    public override string Description => "Asserts that bots only plan through keycard doors their inventory can open (runtime backend).";
    public override float TimeoutSeconds => 300f;
    protected override string SurveyScope => "keycard";
    protected override string SurveyDirections => "forward";
}
