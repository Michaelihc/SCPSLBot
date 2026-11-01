using LabApi.Features.Wrappers;
using MapGeneration;
using PlaytestHarness.Core;
using PlaytestHarness.Probes;
using SCPSLBot.PlaytestScenarios.Harness;
using System.Collections.Generic;
using UnityEngine;

namespace SCPSLBot.PlaytestScenarios.Scenarios;

/// <summary>
/// One ordered bot walks from Heavy Containment to Light Containment and back. The zones share no
/// walkable route, so each leg proves a native ride through a checkpoint elevator.
/// </summary>
public sealed class CheckpointElevatorRideScenario : Scenario
{
    private const float LegSeconds = 200f;

    public override string Name => "scpslbot-checkpoint-elevator-ride";
    public override string Description => "An ordered bot rides a checkpoint elevator from Heavy to Light Containment and back.";
    public override FidelityRange Supported => FidelityRange.Only(Fidelity.Standard);
    public override bool IncludeInRunAll => false;
    public override float TimeoutSeconds => 2f * LegSeconds + 60f;

    public override IEnumerator<float> Run(ScenarioContext ctx)
    {
        Issue2World.RequireReady(ctx);
        yield return ctx.EnsureRoundStarted();
        NativeCommandAdapter.RemoteAdmin("botspike cleanup");
        try
        {
            Issue2World.Command(ctx, "botspike start ordered Scp096 LczToilets");
            yield return ctx.WaitUntil(() => WarmupBotWorld.FindSpike() is { IsReady: true, IsAlive: true }, 10f, "the ordered bot spawned");
            Player bot = WarmupBotWorld.FindSpike() ?? throw new RequireException("the ordered bot vanished after spawning");
            ctx.Arrange("keep the observed bot alive for both legs", () => bot.IsGodModeEnabled = true);
            ctx.Require(bot.Room?.Zone == FacilityZone.HeavyContainment, $"the bot starts in Heavy Containment: {bot.Room?.Name}");

            yield return ctx.WaitUntil(() => bot.Room?.Zone == FacilityZone.LightContainment, LegSeconds, "the bot rode into Light Containment");
            ctx.Info($"CHECKPOINT_RIDE arrived=LCZ pos={PlacementProbes.Format(bot.Position)} room={bot.Room?.Name}");

            Issue2World.Command(ctx, "botspike walk Hcz096");
            yield return ctx.WaitUntil(() => bot.Room?.Zone == FacilityZone.HeavyContainment, LegSeconds, "the bot rode back into Heavy Containment");
            ctx.Info($"CHECKPOINT_RIDE arrived=HCZ pos={PlacementProbes.Format(bot.Position)} room={bot.Room?.Name}");
        }
        finally
        {
            NativeCommandAdapter.RemoteAdmin("botspike cleanup");
        }
    }
}
