using LabApi.Features.Wrappers;
using PlaytestHarness.Core;
using SCPSLBot.PlaytestScenarios.Harness;
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace SCPSLBot.PlaytestScenarios.Scenarios;

public sealed class BotMovementGroundingScenario : Scenario
{
    public override string Name => "scpslbot-native-walk-grounding";
    public override string[] Aliases => ["bot-walk-ground"];
    public override string[] Suites => ["scpslbot-lifecycle"];
    public override string Description => "Drives the real botspike RA route and asserts walking, no teleport, no ground misses, and dense world probes.";
    public override FidelityRange Supported => FidelityRange.Only(Fidelity.Standard);
    public override float TimeoutSeconds => 300f;

    public override IEnumerator<float> Run(ScenarioContext ctx)
    {
        NativeCommandAdapter.RemoteAdmin("botspike cleanup");
        try
        {
            WarmupPopulationRecoveryScenario.RequireCommand(ctx,
                NativeCommandAdapter.RemoteAdmin("botspike start"), "spawn native-role BotOrders spike");
            yield return ctx.WaitUntil(() => WarmupBotWorld.FindSpike() is { IsReady: true, IsAlive: true }, 10f,
                "BotOrders spike reached a live native role");

            Player spike = WarmupBotWorld.FindSpike()
                ?? throw new RequireException("BotOrders Spike was not visible in the public player world");
            Vector3 start = spike.Position;
            WarmupBotWorld.ProbeGroundAndSurroundings(ctx, spike);

            WarmupPopulationRecoveryScenario.RequireCommand(ctx,
                NativeCommandAdapter.RemoteAdmin("botspike walk preset"),
                "walk the real LczToilets -> Lcz173 preset");
            ThrottledCondition routeStopped = new(RouteStopped, 0.5f);
            yield return ctx.WaitUntil(routeStopped.Check, 250f,
                "native walking route reached a terminal result");

            NativeCommandResult statusCommand = NativeCommandAdapter.RemoteAdmin("botspike status");
            WarmupPopulationRecoveryScenario.RequireCommand(ctx, statusCommand, "read botspike terminal status");
            Dictionary<string, string> status = CommandFields.ParseWhitespace(statusCommand.Response);
            ctx.Require(Get(status, "order") == "Completed",
                $"walking route terminal order is Completed: {statusCommand.Response}");
            ctx.Require(Get(status, "active") == "False" && Get(status, "routeRunning") == "False",
                $"walking route is no longer active: {statusCommand.Response}");
            ctx.Require(Get(status, "teleport") == "False",
                $"walking route detected no teleport: {statusCommand.Response}");
            ctx.Require(GetInt(status, "groundMisses") == 0,
                $"walking route recorded zero ground misses: {statusCommand.Response}");
            ctx.Require(GetFloat(status, "maxTick") <= 1.5f,
                $"walking route stayed within per-tick displacement budget: {statusCommand.Response}");

            spike = WarmupBotWorld.FindSpike()
                ?? throw new RequireException("BotOrders Spike vanished before final world probes");
            ctx.Require(Vector3.Distance(start, spike.Position) > 1f,
                $"spike physically walked away from native spawn ({Vector3.Distance(start, spike.Position):0.##}m)");
            WarmupBotWorld.ProbeGroundAndSurroundings(ctx, spike);
            Vector3 beforeSettle = spike.Position;
            yield return ctx.Wait(1.25f);
            spike = WarmupBotWorld.FindSpike()
                ?? throw new RequireException("BotOrders Spike vanished during settle probe");
            ctx.Require(spike.Position.y > -100f && PlaytestHarness.Probes.PlacementProbes.Ground(spike.Position, 3.5f).Ok,
                $"walked spike remained grounded after settle window ({beforeSettle.y:0.##}->{spike.Position.y:0.##})");
            ctx.Info($"botspike final status: {statusCommand.Response}");
        }
        finally
        {
            NativeCommandAdapter.RemoteAdmin("botspike cleanup");
        }
    }

    private static bool RouteStopped()
    {
        NativeCommandResult result = NativeCommandAdapter.RemoteAdmin("botspike status");
        if (!result.Success)
        {
            return false;
        }

        Dictionary<string, string> status = CommandFields.ParseWhitespace(result.Response);
        return string.Equals(Get(status, "routeRunning"), "False", StringComparison.OrdinalIgnoreCase)
               && status.ContainsKey("order");
    }

    private static string Get(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out string value) ? value : string.Empty;

    private static int GetInt(IReadOnlyDictionary<string, string> values, string key) =>
        int.TryParse(Get(values, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : throw new RequireException($"botspike field '{key}' is not an integer: '{Get(values, key)}'");

    private static float GetFloat(IReadOnlyDictionary<string, string> values, string key) =>
        float.TryParse(Get(values, key), NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
            ? value
            : throw new RequireException($"botspike field '{key}' is not a float: '{Get(values, key)}'");
}
