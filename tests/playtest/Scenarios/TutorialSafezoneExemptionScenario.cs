using System.Collections.Generic;
using LabApi.Features.Wrappers;
using MapGeneration;
using PlayerRoles;
using PlaytestHarness.Actors;
using PlaytestHarness.Core;

namespace SCPSLBot.PlaytestScenarios.Scenarios;

public sealed class TutorialSafezoneExemptionScenario : Scenario
{
    public override string Name => "scpslbot-tutorial-safezone-exemption";
    public override string Description => "Checks the safezone damage boundary with native Tutorial and a participating control role.";
    public override FidelityRange Supported => FidelityRange.Only(Fidelity.Quick);
    public override string[] Suites => ["tutorial-participation"];

    public override IEnumerator<float> Run(ScenarioContext ctx)
    {
        Actor tutorial = ctx.SpawnActor("tutorial-safezone", RoleTypeId.Tutorial, SpawnSpec.Native(), useMovementProvider: false);
        Actor control = ctx.SpawnActor("participant-safezone", RoleTypeId.ClassD, SpawnSpec.Native(), useMovementProvider: false);
        yield return tutorial.WaitReady();
        yield return control.WaitReady();
        yield return tutorial.GoTo(RoomName.Lcz914);
        yield return control.GoTo(RoomName.Lcz914);
        yield return tutorial.Settle(maxDrop: 3f, timeoutSeconds: 4f);
        yield return control.Settle(maxDrop: 3f, timeoutSeconds: 4f);
        Player admin = Player.Get(tutorial.PlayerId) ?? throw new RequireException("Tutorial wrapper unavailable");
        Player participant = Player.Get(control.PlayerId) ?? throw new RequireException("control wrapper unavailable");
        ctx.Require(admin.Room?.Name == RoomName.Lcz914 && participant.Room?.Name == RoomName.Lcz914,
            "both actors must be inside native SCP-914");
        float tutorialHealth = admin.Health;
        float participantHealth = participant.Health;
        // Quick-only isolated damage-event probe, with a protected control proving the safezone is active.
        admin.Damage(5f, "Tutorial participation test");
        participant.Damage(5f, "Tutorial participation control");
        ctx.Require(admin.Health < tutorialHealth, "Tutorial must receive native damage without safezone protection");
        ctx.Require(participant.Health == participantHealth, "participating control must retain safezone protection");
    }
}
