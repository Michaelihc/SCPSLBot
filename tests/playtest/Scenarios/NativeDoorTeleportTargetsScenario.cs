using CommandSystem.Commands.RemoteAdmin.Doors;
using Interactables.Interobjects.DoorUtils;
using MapGeneration;
using PlayerRoles;
using PlayerRoles.FirstPersonControl;
using PlaytestHarness.Actors;
using PlaytestHarness.Core;
using SCPSLBot.PlaytestScenarios.Harness;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace SCPSLBot.PlaytestScenarios.Scenarios;

/// <summary>
/// Probes every native named-door destination through the real RA doortp command. The production SSS
/// room list uses this same registry and safety resolver but deliberately rejects test dummies.
/// </summary>
public sealed class NativeDoorTeleportTargetsScenario : Scenario
{
    public override string Name => "scpslbot-native-door-teleports";
    public override string[] Aliases => ["native-door-teleports", "door-room-teleports"];
    public override string[] Suites => ["scpslbot-spatial"];
    public override string Description => "ISOLATED PORT: sends a dummy through every RA-resolvable named door and checks room, position, and ground.";
    public override FidelityRange Supported => FidelityRange.Only(Fidelity.Standard);
    public override bool IncludeInRunAll => false;
    public override float TimeoutSeconds => 180f;

    public override IEnumerator<float> Run(ScenarioContext ctx)
    {
        Actor actor = ctx.SpawnActor(
            "native-door-teleport",
            RoleTypeId.NtfPrivate,
            SpawnSpec.Native(),
            useMovementProvider: false);
        yield return actor.WaitReady();

        KeyValuePair<string, DoorNametagExtension>[] doors = DoorNametagExtension.NamedDoors
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Key)
                && entry.Value != null
                && entry.Value.TargetDoor != null
                && entry.Value.transform != null)
            .OrderBy(entry => entry.Key, System.StringComparer.Ordinal)
            .ToArray();
        ctx.Require(doors.Length > 0, "native RA named-door registry is populated");
        ctx.Require(doors.Length <= 254,
            $"all {doors.Length} native RA door destinations fit the SSS dropdown protocol");
        IReadOnlyList<Actor> actors = new[] { actor };

        foreach (KeyValuePair<string, DoorNametagExtension> entry in doors)
        {
            Vector3 expected = DoorTPCommand.EnsurePositionSafety(entry.Value.transform);
            ctx.ExpectFeatureTransitions(
                $"native RA door teleport {entry.Key}",
                actors,
                new FeatureTransitionSpec(
                    2f,
                    positions: new[]
                    {
                        new PositionTransitionExpectation(
                            entry.Key,
                            position => Vector3.Distance(position, expected) < 0.75f),
                    }));
            NativeCommandResult command = NativeCommandAdapter.RemoteAdmin(
                $"doortp {actor.PlayerId} {entry.Key}");
            WarmupPopulationRecoveryScenario.RequireCommand(ctx, command, $"RA doortp {entry.Key}");

            yield return ctx.WaitUntil(
                () => Vector3.Distance(actor.Position, expected) < 0.75f,
                2f,
                $"{entry.Key} reached the native RA-safe destination");
            ctx.Require(RoomUtils.TryGetRoom(actor.Position, out RoomIdentifier room) && room != null,
                $"{entry.Key} resolved to a generated room at {actor.Position}");
            ctx.Require(Physics.Raycast(
                    actor.Position + Vector3.up * 0.1f,
                    Vector3.down,
                    3.5f,
                    FpcStateProcessor.Mask,
                    QueryTriggerInteraction.Ignore),
                $"{entry.Key} has collision ground beneath the RA-safe destination");
        }
    }
}
