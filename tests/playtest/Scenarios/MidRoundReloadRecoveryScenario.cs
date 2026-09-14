using PlaytestHarness.Core;
using SCPSLBot.PlaytestScenarios.Harness;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SCPSLBot.PlaytestScenarios.Scenarios;

/// <summary>
/// Optional disruptive acceptance. Production deliberately excludes SCPSLPluginExtensions; this
/// scenario skips before mutation unless that test-only console reload driver is deployed.
/// </summary>
public sealed class MidRoundReloadRecoveryScenario : Scenario
{
    public override string Name => "scpslbot-midround-reload-recovery";
    public override string[] Aliases => ["bot-reload-recovery"];
    public override string[] Suites => ["scpslbot-reload"];
    public override string Description => "OPTIONAL DRIVER: reloads SCPSLBot mid-round and requires current native readiness plus exact population without restart.";
    public override FidelityRange Supported => FidelityRange.Only(Fidelity.Standard);
    public override bool IncludeInRunAll => false;
    public override float TimeoutSeconds => 60f;

    public override IEnumerator<float> Run(ScenarioContext ctx)
    {
        if (!NativeCommandAdapter.IsGameConsoleCommandRegistered("plugin_direload"))
        {
            ctx.Skip("plugin_direload is intentionally absent from production; deploy the test-only reload driver for this disruptive acceptance");
        }

        BotStatusSnapshot initial = BotStatusSnapshot.Read();
        string originalMode = initial.Mode;
        try
        {
            WarmupPopulationRecoveryScenario.RequireCommand(ctx,
                NativeCommandAdapter.RemoteAdmin("bot_warmup standard"), "enable Standard before mid-round reload");
            ThrottledCondition initialPopulation = new(IsExactAndReady);
            yield return ctx.WaitUntil(initialPopulation.Check, 12f, "population ready before plugin reload");
            int desired = BotStatusSnapshot.Read().Desired;
            int[] oldIds = WarmupBotWorld.Snapshot().Select(player => player.PlayerId).ToArray();

            NativeCommandResult reload = NativeCommandAdapter.GameConsole("plugin_direload SCPSLBot");
            ctx.Require(reload.Response.IndexOf("Done", StringComparison.OrdinalIgnoreCase) >= 0,
                $"test-only native console reload completed: {reload}");
            ThrottledCondition recoveredPopulation = new(() => IsExactAt(desired));
            yield return ctx.WaitUntil(recoveredPopulation.Check, 12f,
                "reloaded plugin derived readiness from current native state and rebuilt exact population");

            BotStatusSnapshot recovered = BotStatusSnapshot.Read();
            WarmupBotWorld.AssertExactInitializedPopulation(ctx, recovered);
            int[] newIds = WarmupBotWorld.Snapshot().Select(player => player.PlayerId).ToArray();
            ctx.Require(!oldIds.Intersect(newIds).Any(),
                "disable/reload removed the old owned dummy set before publishing replacements");
            ctx.Info($"mid-round reload recovered without round restart: {recovered.Raw}");
        }
        finally
        {
            NativeCommandAdapter.RemoteAdmin($"bot_warmup {originalMode.ToLowerInvariant()}");
        }
    }

    private static bool IsExactAndReady()
    {
        try
        {
            BotStatusSnapshot status = BotStatusSnapshot.Read();
            return IsExactAt(status.Desired);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsExactAt(int desired)
    {
        try
        {
            BotStatusSnapshot status = BotStatusSnapshot.Read();
            return status.Desired == desired
                   && status.NetworkReady
                   && status.NavReady
                   && status.NavGeneration == status.NavReadyGeneration
                   && status.Owned == desired
                   && status.Live == desired
                   && status.AiRunnerRunning
                   && WarmupBotWorld.Snapshot().Length == desired;
        }
        catch
        {
            return false;
        }
    }
}
