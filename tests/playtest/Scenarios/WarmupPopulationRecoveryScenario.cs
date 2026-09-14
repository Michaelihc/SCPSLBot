using PlaytestHarness.Core;
using SCPSLBot.PlaytestScenarios.Harness;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace SCPSLBot.PlaytestScenarios.Scenarios;

public sealed class WarmupPopulationRecoveryScenario : Scenario
{
    public override string Name => "scpslbot-population-recovery";
    public override string[] Aliases => ["bot-population"];
    public override string[] Suites => ["scpslbot-lifecycle"];
    public override string Description => "Proves diagnostics permissions, exact <=12s Standard population, off/on recovery, and world grounding.";
    public override FidelityRange Supported => FidelityRange.Only(Fidelity.Standard);
    public override float TimeoutSeconds => 90f;

    public override IEnumerator<float> Run(ScenarioContext ctx)
    {
        BotStatusSnapshot initial = BotStatusSnapshot.Read();
        string originalMode = initial.Mode;
        ctx.Require(initial.Desired > 0 && initial.Desired <= 10,
            $"acceptance profile has a non-zero bounded desired population (actual {initial.Desired})");

        NativeCommandResult denied = NativeCommandAdapter.RemoteAdmin("bot_status", fullPermissions: false);
        ctx.Require(!denied.Success, "zero-permission RA sender cannot read bot_status");
        ctx.Require(denied.CombinedText.IndexOf("permission", StringComparison.OrdinalIgnoreCase) >= 0,
            $"bot_status denial names the permission boundary: {denied.CombinedText}");

        try
        {
            RequireCommand(ctx, NativeCommandAdapter.RemoteAdmin("bot_warmup standard"), "enable Standard warmup");
            ThrottledCondition initialPopulation = new(PopulationIsExactAndReady);
            yield return ctx.WaitUntil(initialPopulation.Check, 12f,
                "Standard warmup reached exact initialized live target within 12 seconds");

            BotStatusSnapshot ready = BotStatusSnapshot.Read();
            WarmupBotWorld.AssertExactInitializedPopulation(ctx, ready);
            Dictionary<int, Vector3> before = WarmupBotWorld.CapturePositions();
            yield return ctx.Wait(1.25f);
            WarmupBotWorld.AssertNoFallsAfterSettling(ctx, before);

            RequireCommand(ctx, NativeCommandAdapter.RemoteAdmin("bot_warmup none"), "disable warmup");
            ThrottledCondition offPopulation = new(PopulationIsFullyOff);
            yield return ctx.WaitUntil(offPopulation.Check, 5f,
                "None mode despawned every warmup-owned bot");
            BotStatusSnapshot off = BotStatusSnapshot.Read();
            ctx.Require(string.Equals(off.Mode, "None", StringComparison.OrdinalIgnoreCase),
                $"diagnostics report None after disable (actual {off.Mode})");
            ctx.Require(off.Owned == 0 && off.Live == 0 && WarmupBotWorld.Snapshot().Length == 0,
                "None mode exposes no owned/live/world warmup bots");

            RequireCommand(ctx, NativeCommandAdapter.RemoteAdmin("bot_warmup standard"), "re-enable Standard warmup");
            ThrottledCondition recoveredPopulation = new(PopulationIsExactAndReady);
            yield return ctx.WaitUntil(recoveredPopulation.Check, 12f,
                "Standard warmup recovered exact target after off/on churn");
            BotStatusSnapshot recovered = BotStatusSnapshot.Read();
            WarmupBotWorld.AssertExactInitializedPopulation(ctx, recovered);

            yield return ctx.Wait(0.5f);
            BotStatusSnapshot heartbeat = BotStatusSnapshot.Read();
            ctx.Require(heartbeat.AiRunnerRunning && heartbeat.AiHeartbeat != "never",
                "AI runner remains supervised after population churn");
            ctx.Info($"population recovery final status: {heartbeat.Raw}");
        }
        finally
        {
            NativeCommandAdapter.RemoteAdmin($"bot_warmup {originalMode.ToLowerInvariant()}");
        }
    }

    private static bool PopulationIsExactAndReady()
    {
        try
        {
            BotStatusSnapshot status = BotStatusSnapshot.Read();
            return string.Equals(status.Mode, "Standard", StringComparison.OrdinalIgnoreCase)
                   && status.NetworkReady
                   && status.NavReady
                   && status.NavGeneration == status.NavReadyGeneration
                   && status.Desired > 0
                   && status.Owned == status.Desired
                   && status.Live == status.Desired
                   && status.AiRunnerRunning
                   && WarmupBotWorld.Snapshot().Length == status.Desired
                   && WarmupBotWorld.Snapshot().All(player => player.IsReady
                       && player.IsAlive
                       && player.Role == status.DesiredRole);
        }
        catch
        {
            return false;
        }
    }

    private static bool PopulationIsFullyOff()
    {
        try
        {
            BotStatusSnapshot status = BotStatusSnapshot.Read();
            return string.Equals(status.Mode, "None", StringComparison.OrdinalIgnoreCase)
                   && status.Owned == 0
                   && status.Live == 0
                   && WarmupBotWorld.Snapshot().Length == 0;
        }
        catch
        {
            return false;
        }
    }

    internal static void RequireCommand(ScenarioContext ctx, NativeCommandResult result, string action) =>
        ctx.Require(result.Success, $"RA command succeeded for {action}: {result}");
}
