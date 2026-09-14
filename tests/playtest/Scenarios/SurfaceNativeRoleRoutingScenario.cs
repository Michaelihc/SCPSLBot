using LabApi.Features.Extensions;
using LabApi.Features.Wrappers;
using MapGeneration;
using PlayerRoles;
using PlaytestHarness.Actors;
using PlaytestHarness.Core;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace SCPSLBot.PlaytestScenarios.Scenarios;

/// <summary>
/// Exercises the same native ItemUsage role/spawn transition used by item-driven transformations
/// without depending on a seasonally registered role. The observable under test is the public
/// role/spawn path.
/// </summary>
public sealed class SurfaceNativeRoleRoutingScenario : Scenario
{
    public override string Name => "scpslbot-surface-native-role-routing";
    public override string[] Aliases => ["surface-role-routing"];
    public override string[] Suites => ["scpslbot-lifecycle"];
    public override string Description => "Proves native item-driven arbitrary roles leave Surface while facility-origin transformations preserve exact position.";
    public override FidelityRange Supported => FidelityRange.Only(Fidelity.Standard);

    public override IEnumerator<float> Run(ScenarioContext ctx)
    {
        Actor actor = ctx.SpawnActor(
            "surface-native-role",
            RoleTypeId.NtfPrivate,
            SpawnSpec.Native(),
            useMovementProvider: false);
        yield return actor.WaitReady();

        ctx.Require(RoleTypeId.NtfPrivate.TryGetRandomSpawnPoint(out Vector3 surfaceSpawn, out _),
            "native NTF Surface spawnpoint is unavailable");
        yield return actor.GoTo(surfaceSpawn);
        yield return actor.Settle(maxDrop: 3f, timeoutSeconds: 4f);
        Player player = Player.Get(actor.PlayerId)
            ?? throw new RequireException("Surface role-routing actor has no LabAPI wrapper");
        ctx.Require(player.Zone == FacilityZone.Surface, $"native NTF anchor is not Surface: {player.Position}");
        if (player.IsDummy)
        {
            ctx.Skip(
                "Surface role routing is intentionally authenticated-player-only; the native dummy/raycast Surface anchor passed, but final role routing requires a connected real client");
        }

        ExpectNativeTransition(
            ctx,
            actor,
            RoleTypeId.ChaosRifleman,
            "Surface human transformation routed to HCZ/EZ",
            FacilityZone.HeavyContainment,
            FacilityZone.Entrance);
        player.ReferenceHub.roleManager.ServerSetRole(
            RoleTypeId.ChaosRifleman,
            RoleChangeReason.ItemUsage,
            RoleSpawnFlags.All);
        yield return ctx.Wait(0.75f);
        ctx.Require(player.Role == RoleTypeId.ChaosRifleman,
            $"native item-driven role did not become ChaosRifleman (actual {player.Role})");
        ctx.Require(player.Zone is FacilityZone.HeavyContainment or FacilityZone.Entrance,
            $"arbitrary native role remained/reappeared on Surface: zone={player.Zone} pos={player.Position}");
        yield return actor.Settle(maxDrop: 3f, timeoutSeconds: 4f);

        yield return actor.GoTo(surfaceSpawn);
        yield return actor.Settle(maxDrop: 3f, timeoutSeconds: 4f);
        ctx.Require(player.Zone == FacilityZone.Surface, $"second native Surface anchor is invalid: {player.Position}");
        ExpectNativeTransition(
            ctx,
            actor,
            RoleTypeId.Scp173,
            "Surface SCP transformation routed to LCZ",
            FacilityZone.LightContainment);
        player.ReferenceHub.roleManager.ServerSetRole(
            RoleTypeId.Scp173,
            RoleChangeReason.ItemUsage,
            RoleSpawnFlags.All);
        yield return ctx.Wait(0.75f);
        ctx.Require(player.Role == RoleTypeId.Scp173,
            $"native item-driven SCP role did not become Scp173 (actual {player.Role})");
        ctx.Require(player.Zone == FacilityZone.LightContainment,
            $"native SCP role did not route from Surface to LCZ: zone={player.Zone} pos={player.Position}");
        yield return actor.Settle(maxDrop: 3f, timeoutSeconds: 4f);

        ctx.Require(RoleTypeId.Scp939.TryGetRandomSpawnPoint(out Vector3 facilitySpawn, out _),
            "native HCZ/EZ anchor is unavailable");
        yield return actor.GoTo(facilitySpawn);
        yield return actor.Settle(maxDrop: 3f, timeoutSeconds: 4f);
        Vector3 before = player.Position;
        ExpectNativeTransition(
            ctx,
            actor,
            RoleTypeId.ChaosRifleman,
            "facility transformation preserves position");
        player.ReferenceHub.roleManager.ServerSetRole(
            RoleTypeId.ChaosRifleman,
            RoleChangeReason.ItemUsage,
            RoleSpawnFlags.All);
        yield return ctx.Wait(0.75f);
        ctx.Require(player.Role == RoleTypeId.ChaosRifleman,
            $"facility native role did not become ChaosRifleman (actual {player.Role})");
        ctx.Require(Vector3.Distance(before, player.Position) < 0.5f,
            $"facility-origin native role transformation moved {Vector3.Distance(before, player.Position):0.###}m");
        yield return actor.Settle(maxDrop: 3f, timeoutSeconds: 4f);
    }

    private static void ExpectNativeTransition(
        ScenarioContext ctx,
        Actor actor,
        RoleTypeId role,
        string label,
        params FacilityZone[] destinationZones)
    {
        IReadOnlyList<PositionTransitionExpectation> positions = destinationZones.Length == 0
            ? Array.Empty<PositionTransitionExpectation>()
            : new[]
            {
                new PositionTransitionExpectation(label, position =>
                    RoomUtils.TryGetRoom(position, out RoomIdentifier room)
                    && room != null
                    && Array.IndexOf(destinationZones, room.Zone) >= 0),
            };
        ctx.ExpectFeatureTransitions(
            label,
            new[] { actor },
            new FeatureTransitionSpec(
                3f,
                roles: new[] { new RoleTransitionExpectation(label, role) },
                positions: positions));
    }
}
