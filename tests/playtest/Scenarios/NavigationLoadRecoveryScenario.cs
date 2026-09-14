using PlaytestHarness.Core;
using SCPSLBot.PlaytestScenarios.Harness;
using System.Collections.Generic;

namespace SCPSLBot.PlaytestScenarios.Scenarios;

/// <summary>Isolated startup probe: the external driver locks navmesh.slnmf, then releases it.</summary>
public sealed class NavigationLoadRecoveryScenario : Scenario
{
    public override string Name => "scpslbot-navigation-load-recovery";
    public override string[] Suites => ["scpslbot-navigation-recovery"];
    public override string Description => "ISOLATED STARTUP: observes failed navigation, same-map recovery, grounded population and death/respawn.";
    public override FidelityRange Supported => FidelityRange.Only(Fidelity.Standard);
    public override bool IncludeInRunAll => false;
    public override float TimeoutSeconds => 120f;

    public override IEnumerator<float> Run(ScenarioContext ctx)
    {
        BotStatusSnapshot blocked = BotStatusSnapshot.Read();
        ctx.Require(blocked.NetworkReady && !blocked.NavReady,
            "native server is ready while the injected navigation load failure blocks population");
        ctx.Require(blocked.Values["nav_error"] != "none" && blocked.Live == 0 && blocked.Desired > 0,
            "navigation failure is visible and the desired population remains unspawned");
        ctx.Info($"navigation blocked status: {blocked.Raw}");

        ThrottledCondition recovered = new(() =>
        {
            BotStatusSnapshot status = BotStatusSnapshot.Read();
            return status.NavReady && status.Live == status.Desired && status.Live > 0;
        });
        yield return ctx.WaitUntil(recovered.Check, 75f, "navigation and bots recover after external file lock release");

        BotStatusSnapshot ready = BotStatusSnapshot.Read();
        ctx.Require(ready.NavGeneration == blocked.NavGeneration
            && ready.NavReadyGeneration == blocked.NavGeneration,
            "recovery published readiness for the same map without a round restart");
        ctx.Require(ready.Values["nav_error"] == "none", "successful recovery cleared the navigation error");
        WarmupBotWorld.AssertExactInitializedPopulation(ctx, ready);
        ctx.Info($"navigation recovered status: {ready.Raw}");

        // Reuse native RA role-cancellation/death probes and floor/settling assertions after recovery.
        using IEnumerator<float> deathRecovery = new WarmupRoleDeathRecoveryScenario().Run(ctx);
        while (deathRecovery.MoveNext())
        {
            yield return deathRecovery.Current;
        }
    }
}
