using LabApi.Features.Wrappers;
using PlaytestHarness.Core;
using SCPSLBot.PlaytestScenarios.Harness;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace SCPSLBot.PlaytestScenarios.Scenarios;

/// <summary>
/// Drives the real `botspike survey` RA command: one spike bot walks natively across every
/// door-less room connector (clutter passages and open hallways) of the generated map in both
/// directions. The verdict is read back through `botspike survey_status`; per-case detail lives in
/// the server log as [BotSurvey] lines. Fails when any traversal stalls, times out, has no path,
/// or when a generated room has no navigation cells at all.
/// </summary>
public class ConnectorTraversalSurveyScenario : Scenario
{
    public override string Name => "scpslbot-connector-survey";
    public override string[] Aliases => ["bot-connector-survey"];
    public override string[] Suites => ["scpslbot-navigation-survey"];
    public override string Description => "Walks a native bot across every door-less clutter/open connector in both directions and asserts zero stuck cases.";
    public override FidelityRange Supported => FidelityRange.Only(Fidelity.Standard);
    public override bool IncludeInRunAll => false;
    public override float TimeoutSeconds => 1500f;

    protected virtual string SurveyScope => "clutter";
    protected virtual string SurveyDirections => "both";

    public override IEnumerator<float> Run(ScenarioContext ctx)
    {
        BotStatusSnapshot initial = BotStatusSnapshot.Read();
        string originalMode = initial.Mode;
        ctx.Require(initial.NavReady, $"navigation is ready before the survey: {initial.Raw}");

        WarmupPopulationRecoveryScenario.RequireCommand(ctx,
            NativeCommandAdapter.RemoteAdmin("bot_warmup none"), "disable warmup population during the survey");
        ThrottledCondition populationOff = new(() => BotStatusSnapshot.Read().Owned == 0, 0.5f);
        yield return ctx.WaitUntil(populationOff.Check, 15f, "warmup population despawned");

        NativeCommandAdapter.RemoteAdmin("botspike cleanup");
        try
        {
            WarmupPopulationRecoveryScenario.RequireCommand(ctx,
                NativeCommandAdapter.RemoteAdmin("botspike start"), "spawn native-role BotOrders spike");
            yield return ctx.WaitUntil(() => WarmupBotWorld.FindSpike() is { IsReady: true, IsAlive: true }, 10f,
                "BotOrders spike reached a live native role");
            yield return ctx.Wait(1f);

            WarmupPopulationRecoveryScenario.RequireCommand(ctx,
                NativeCommandAdapter.RemoteAdmin($"botspike survey {SurveyScope} {SurveyDirections}"),
                $"start the {SurveyScope} connector survey");

            float nextInfoAt = 0f;
            ThrottledCondition surveyStopped = new(() =>
            {
                NativeCommandResult result = NativeCommandAdapter.RemoteAdmin("botspike survey_status");
                if (!result.Success)
                {
                    return false;
                }

                Dictionary<string, string> fields = CommandFields.ParseWhitespace(result.Response);
                if (UnityEngine.Time.realtimeSinceStartup >= nextInfoAt)
                {
                    nextInfoAt = UnityEngine.Time.realtimeSinceStartup + 15f;
                    ctx.Info($"survey progress: done={Get(fields, "done")}/{Get(fields, "cases")} passed={Get(fields, "passed")} failed={Get(fields, "failed")} current={Get(fields, "current")} last={Get(fields, "last")}");
                }

                return string.Equals(Get(fields, "running"), "False", StringComparison.OrdinalIgnoreCase)
                       && fields.ContainsKey("cases");
            }, 1f);
            yield return ctx.WaitUntil(surveyStopped.Check, TimeoutSeconds - 120f, "connector survey reached a terminal state");

            NativeCommandResult status = NativeCommandAdapter.RemoteAdmin("botspike survey_status");
            WarmupPopulationRecoveryScenario.RequireCommand(ctx, status, "read the survey status");
            Dictionary<string, string> summary = CommandFields.ParseWhitespace(status.Response);
            ctx.Info($"survey final status: {status.Response}");

            int cases = GetInt(summary, "cases");
            int failed = GetInt(summary, "failed");
            int skipped = GetInt(summary, "skipped");
            int passed = GetInt(summary, "passed");
            int noMeshRooms = GetInt(summary, "noMeshRooms");
            ctx.Require(cases > 0, $"the generated map exposed at least one survey case (cases={cases})");
            ctx.Require(noMeshRooms == 0,
                $"every generated room has navigation cells (rooms without mesh: {Get(summary, "noMeshForms")})");
            ctx.Require(failed == 0,
                $"no connector traversal stalled, timed out, or lacked a path (failed={failed} failures={Get(summary, "failures")})");
            ctx.Require(passed + skipped == cases && passed > 0,
                $"survey accounted for every case (passed={passed} skipped={skipped} cases={cases})");

            Player spike = WarmupBotWorld.FindSpike()
                ?? throw new RequireException("BotOrders Spike vanished before the final world probe");
            WarmupBotWorld.ProbeGroundAndSurroundings(ctx, spike);
        }
        finally
        {
            NativeCommandAdapter.RemoteAdmin("botspike cleanup");
            NativeCommandAdapter.RemoteAdmin($"bot_warmup {originalMode}");
        }
    }

    private static string Get(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out string value) ? value : string.Empty;

    private static int GetInt(IReadOnlyDictionary<string, string> values, string key) =>
        int.TryParse(Get(values, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : throw new RequireException($"survey field '{key}' is not an integer: '{Get(values, key)}'");
}

/// <summary>Same survey over plain non-keycard doors, forward direction only (long).</summary>
public sealed class DoorTraversalSurveyScenario : ConnectorTraversalSurveyScenario
{
    public override string Name => "scpslbot-door-survey";
    public override string[] Aliases => ["bot-door-survey"];
    public override string Description => "Walks a native bot through every plain door of the generated map and asserts zero stuck cases.";
    protected override string SurveyScope => "doors";
    protected override string SurveyDirections => "forward";
}
