using System.Collections.Generic;
using CustomPlayerEffects;
using LabApi.Features.Extensions;
using LabApi.Features.Wrappers;
using MapGeneration;
using PlayerRoles;
using PlaytestHarness.Actors;
using PlaytestHarness.Core;
using UnityEngine;

namespace SCPSLBot.PlaytestScenarios.Scenarios;

public sealed class TutorialNativeSpawnScenario : Scenario
{
    public override string Name => "scpslbot-tutorial-native-spawn";
    public override string Description => "Probes native Tutorial tower placement and the configured native protection baseline.";
    public override string[] Suites => ["tutorial-participation"];
    public override FidelityRange Supported => new(Fidelity.Quick, Fidelity.Standard);

    public override IEnumerator<float> Run(ScenarioContext ctx)
    {
        ctx.Require(RoleTypeId.Tutorial.TryGetRandomSpawnPoint(out Vector3 tower, out _),
            "native Tutorial spawnpoint is unavailable");
        ctx.Require(RoleTypeId.NtfPrivate.TryGetRandomSpawnPoint(out Vector3 ntf, out _),
            "native NTF spawnpoint is unavailable");
        ctx.Require(Vector3.Distance(tower, ntf) > 10f, "native Tutorial and NTF anchors must be distinct");

        Actor actor = ctx.SpawnActor("native-tutorial", RoleTypeId.Tutorial, SpawnSpec.Native(), useMovementProvider: false);
        yield return actor.WaitReady();
        Player player = Player.Get(actor.PlayerId) ?? throw new RequireException("Tutorial wrapper unavailable");
        ctx.Require(player.Role == RoleTypeId.Tutorial, "Tutorial role must remain unchanged");
        ctx.Require(player.Zone == FacilityZone.Surface, "native Tutorial tower must resolve to Surface");
        ctx.Require(Vector3.Distance(player.Position, tower) < 5f,
            $"Tutorial moved from native tower: actual={player.Position}, native={tower}");
        ctx.Require(SpawnProtected.ProtectedTeams.Contains(player.Team) == false,
            "this baseline scenario requires native config to exclude Tutorial's team");
        ctx.Require(!player.ReferenceHub.playerEffectsController.GetEffect<SpawnProtected>().IsEnabled,
            "warmup must not invent native protection for Tutorial");
        yield return actor.Settle(maxDrop: 3f, timeoutSeconds: 4f);
        yield return actor.Soak(2f);
        ctx.Require(Vector3.Distance(player.Position, tower) < 5f, "Tutorial must stay at the tower after delayed placement windows");
        // The harness actor probes native geometry. It is intentionally a dummy; authenticated
        // player event routing, SSS rejection and role re-entry still require a connected client.
    }
}
