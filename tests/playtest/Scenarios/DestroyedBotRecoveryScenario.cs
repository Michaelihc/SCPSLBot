using LabApi.Features.Wrappers;
using PlaytestHarness.Core;
using SCPSLBot.PlaytestScenarios.Harness;
using System.Collections.Generic;
using System.Linq;

namespace SCPSLBot.PlaytestScenarios.Scenarios;

public sealed class DestroyedBotRecoveryScenario : Scenario
{
    public override string Name => "scpslbot-destroyed-bot-recovery";
    public override string[] Suites => ["scpslbot-lifecycle"];
    public override string Description => "Native RA dummy destruction must not poison population tracking; verifies replacement, diagnostics and grounding.";
    public override FidelityRange Supported => FidelityRange.Only(Fidelity.Standard);
    public override float TimeoutSeconds => 100f;

    public override IEnumerator<float> Run(ScenarioContext ctx)
    {
        BotStatusSnapshot initial = BotStatusSnapshot.Read();
        yield return ctx.WaitUntil(() => WorldReady(initial.Desired), 12f, "initial maintained population is alive");
        initial = BotStatusSnapshot.Read();
        ctx.Require(initial.Independent == 0, "isolated acceptance has no independent bots");
        WarmupBotWorld.AssertExactInitializedPopulation(ctx, initial);

        for (int cycle = 0; cycle < 4; cycle++)
        {
            Player[] victims = cycle == 3 ? WarmupBotWorld.Snapshot() : [WarmupBotWorld.Snapshot().First()];
            ReferenceHub[] oldHubs = victims.Select(player => player.ReferenceHub).ToArray();
            uint[] oldNetIds = oldHubs.Select(hub => hub.netId).ToArray();
            string targets = string.Join(".", victims.Select(player => player.PlayerId));
            WarmupPopulationRecoveryScenario.RequireCommand(ctx,
                NativeCommandAdapter.RemoteAdmin($"dummies destroy {targets}"),
                $"destroy maintained native dummies cycle={cycle} count={victims.Length}");
            yield return ctx.WaitUntil(() => oldHubs.All(hub => hub == null), 3f,
                "native Unity objects are destroyed, not merely dead or Spectator");
            ctx.Require(oldNetIds.All(id => !ReferenceHub.TryGetHubNetID(id, out _)),
                "destroyed identities are absent from native network registration");

            // Let the autonomous reconciler run before querying diagnostics (which also prunes).
            double deadline = ctx.ElapsedSeconds + 12;
            while (!WorldReady(initial.Desired) && ctx.ElapsedSeconds < deadline)
                yield return ctx.Wait(0.1f);
            bool recoveredWithoutDiagnostics = WorldReady(initial.Desired);
            NativeCommandResult diagnostic = NativeCommandAdapter.RemoteAdmin("bot_status");
            ctx.Info($"post-destroy diagnostic cycle={cycle}: {diagnostic.CombinedText}");
            ctx.Require(diagnostic.Success, "bot_status remains usable after native destruction");
            ctx.Require(recoveredWithoutDiagnostics, "population automatically replaced destroyed dummies before diagnostics ran");
            BotStatusSnapshot recovered = BotStatusSnapshot.Read();
            ctx.Require(recovered.Desired == initial.Desired && recovered.NavGeneration == initial.NavGeneration,
                "recovery preserves configured count and map generation without a mode toggle or restart");
            ctx.Require(recovered.Values["reconcile_fault"] == "none", "reconciler has no current fault");
            ctx.Require(WarmupBotWorld.Snapshot().All(player => !oldHubs.Any(hub => ReferenceEquals(hub, player.ReferenceHub))),
                "replacement dummies have new managed identities, including when numeric player IDs are recycled");
            WarmupBotWorld.AssertExactInitializedPopulation(ctx, recovered);
            var positions = WarmupBotWorld.CapturePositions();
            yield return ctx.Wait(1.25f);
            WarmupBotWorld.AssertNoFallsAfterSettling(ctx, positions);
            ctx.Info($"destroy recovery cycle={cycle}: {recovered.Raw}");
        }
    }

    private static bool WorldReady(int desired)
    {
        // Observe world recovery without bot_status pruning the population on our behalf.
        Player[] bots = WarmupBotWorld.Snapshot();
        return desired > 0 && bots.Length == desired && bots.All(player => player.IsReady && player.IsAlive);
    }
}
