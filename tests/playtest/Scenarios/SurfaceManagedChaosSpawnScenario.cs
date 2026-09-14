using CustomPlayerEffects;
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.Handlers;
using LabApi.Features.Extensions;
using LabApi.Features.Wrappers;
using PlayerRoles;
using PlaytestHarness.Core;
using SCPSLBot.PlaytestScenarios.Harness;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace SCPSLBot.PlaytestScenarios.Scenarios;

public sealed class SurfaceManagedChaosSpawnScenario : Scenario
{
    private const RoleTypeId ChaosRole = RoleTypeId.ChaosRifleman;
    private const int AnchorSamples = 128;

    public override string Name => "scpslbot-surface-managed-chaos-spawn";
    public override string[] Aliases => ["bot-ci-spawn"];
    public override string[] Suites => ["scpslbot-lifecycle"];
    public override string Description =>
        "Distinguishes native CI and MTF Surface pads for maintained-bot death/respawn and off/on creation.";
    public override FidelityRange Supported => FidelityRange.Only(Fidelity.Standard);
    public override float TimeoutSeconds => 90f;

    public override IEnumerator<float> Run(ScenarioContext ctx)
    {
        BotStatusSnapshot initial = BotStatusSnapshot.Read();
        string originalMode = initial.Mode;
        List<Vector3> chaosAnchors = SampleNativeAnchors(ctx, ChaosRole, "CI");
        List<Vector3> ntfAnchors = SampleNativeAnchors(ctx, RoleTypeId.NtfPrivate, "MTF");
        List<SpawnObservation> observations = new();

        void ObserveManagedSpawn(PlayerSpawningEventArgs ev)
        {
            if (ev.Player != null
                && ev.Player.IsDummy
                && ev.Player.Nickname.StartsWith(WarmupBotWorld.NicknamePrefix, StringComparison.Ordinal)
                && ev.Role.RoleTypeId == ChaosRole)
            {
                observations.Add(new SpawnObservation(
                    ev.Player.PlayerId,
                    ev.Player.Nickname,
                    ev.SpawnLocation));
            }
        }

        PlayerEvents.Spawning += ObserveManagedSpawn;
        try
        {
            WarmupPopulationRecoveryScenario.RequireCommand(ctx,
                NativeCommandAdapter.RemoteAdmin("bot_warmup standard"), "enable Standard warmup");
            ThrottledCondition readyPopulation = new(IsExactSurfaceChaosPopulation);
            yield return ctx.WaitUntil(readyPopulation.Check, 12f,
                "Surface acceptance population is entirely initialized as Chaos Rifleman");

            Player selected = WarmupBotWorld.Snapshot().First();
            AssertNoSpawnProtection(ctx, WarmupBotWorld.Snapshot(), "ready population");
            observations.Clear();
            WarmupPopulationRecoveryScenario.RequireCommand(ctx,
                NativeCommandAdapter.RemoteAdmin($"kill {selected.PlayerId}"),
                "kill one maintained CI bot through native RA damage");
            yield return ctx.WaitUntil(
                () => observations.Any(entry => entry.PlayerId == selected.PlayerId)
                      && IsAliveChaos(selected.PlayerId),
                12f,
                "death repair emitted and completed a CI bot spawn transaction");

            SpawnObservation respawn = observations.Last(entry => entry.PlayerId == selected.PlayerId);
            AssertCiPad(ctx, respawn, chaosAnchors, ntfAnchors, "death/respawn");
            AssertNoSpawnProtection(ctx, [WarmupBotWorld.FindById(selected.PlayerId)!], "death/respawn");

            WarmupPopulationRecoveryScenario.RequireCommand(ctx,
                NativeCommandAdapter.RemoteAdmin("bot_warmup none"), "disable maintained population");
            yield return ctx.WaitUntil(() => WarmupBotWorld.Snapshot().Length == 0, 5f,
                "off transition removed every maintained bot");

            observations.Clear();
            WarmupPopulationRecoveryScenario.RequireCommand(ctx,
                NativeCommandAdapter.RemoteAdmin("bot_warmup standard"), "re-enable maintained population");
            ThrottledCondition freshPopulation = new(IsExactSurfaceChaosPopulation);
            yield return ctx.WaitUntil(
                () => freshPopulation.Check()
                      && observations.Select(entry => entry.PlayerId).Distinct().Count()
                         == WarmupBotWorld.Snapshot().Length,
                12f,
                "off/on creation captured a final spawning coordinate for every maintained CI bot");

            foreach (SpawnObservation observation in observations
                         .GroupBy(entry => entry.PlayerId)
                         .Select(group => group.Last()))
            {
                AssertCiPad(ctx, observation, chaosAnchors, ntfAnchors, "fresh creation");
            }

            AssertNoSpawnProtection(ctx, WarmupBotWorld.Snapshot(), "fresh creation");
        }
        finally
        {
            PlayerEvents.Spawning -= ObserveManagedSpawn;
            NativeCommandAdapter.RemoteAdmin($"bot_warmup {originalMode.ToLowerInvariant()}");
        }
    }

    private static List<Vector3> SampleNativeAnchors(
        ScenarioContext ctx,
        RoleTypeId role,
        string label)
    {
        List<Vector3> positions = new();
        for (int index = 0; index < AnchorSamples; index++)
        {
            if (role.TryGetRandomSpawnPoint(out Vector3 position, out _))
            {
                positions.Add(position);
            }
        }

        ctx.Require(positions.Count > 0, $"native {label} Surface spawn produced reference samples");
        return positions;
    }

    private static bool IsExactSurfaceChaosPopulation()
    {
        try
        {
            BotStatusSnapshot status = BotStatusSnapshot.Read();
            Player[] bots = WarmupBotWorld.Snapshot();
            return string.Equals(status.Mode, "Standard", StringComparison.OrdinalIgnoreCase)
                   && status.Desired > 0
                   && status.Owned == status.Desired
                   && status.Live == status.Desired
                   && bots.Length == status.Desired
                   && bots.All(player => player.IsAlive && player.Role == ChaosRole);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsAliveChaos(int playerId)
    {
        Player? player = WarmupBotWorld.FindById(playerId);
        return player != null && !player.IsDestroyed && player.IsAlive && player.Role == ChaosRole;
    }

    private static void AssertCiPad(
        ScenarioContext ctx,
        SpawnObservation observation,
        IReadOnlyList<Vector3> chaosAnchors,
        IReadOnlyList<Vector3> ntfAnchors,
        string phase)
    {
        float ciDistance = chaosAnchors.Min(anchor => HorizontalDistance(anchor, observation.Position));
        float mtfDistance = ntfAnchors.Min(anchor => HorizontalDistance(anchor, observation.Position));
        ctx.Require(ciDistance < 25f && ciDistance + 15f < mtfDistance,
            $"{phase} {observation.Nickname} spawned on CI pad: "
            + $"ci={ciDistance:0.##}m mtf={mtfDistance:0.##}m position={observation.Position}");
    }

    private static float HorizontalDistance(Vector3 left, Vector3 right)
    {
        left.y = 0f;
        right.y = 0f;
        return Vector3.Distance(left, right);
    }

    private static void AssertNoSpawnProtection(
        ScenarioContext ctx,
        IEnumerable<Player> bots,
        string phase)
    {
        foreach (Player bot in bots)
        {
            ctx.Require(bot.GetEffect<SpawnProtected>()?.IsEnabled != true,
                $"{phase} managed bot {bot.Nickname} has no native SpawnProtected effect");
        }
    }

    private readonly struct SpawnObservation
    {
        public SpawnObservation(int playerId, string nickname, Vector3 position)
        {
            PlayerId = playerId;
            Nickname = nickname;
            Position = position;
        }

        public int PlayerId { get; }
        public string Nickname { get; }
        public Vector3 Position { get; }
    }
}
