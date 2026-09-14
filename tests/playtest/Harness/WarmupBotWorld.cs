using LabApi.Features.Wrappers;
using PlayerRoles.FirstPersonControl;
using PlaytestHarness.Core;
using PlaytestHarness.Probes;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace SCPSLBot.PlaytestScenarios.Harness;

internal static class WarmupBotWorld
{
    public const string NicknamePrefix = "SCPSL Warmup Bot ";
    public const string SpikeNickname = "BotOrders Spike";

    private static readonly Vector3[] GroundOffsets =
    {
        Vector3.zero,
        new(0.06f, 0f, 0f), new(-0.06f, 0f, 0f), new(0f, 0f, 0.06f), new(0f, 0f, -0.06f),
        new(0.12f, 0f, 0f), new(-0.12f, 0f, 0f), new(0f, 0f, 0.12f), new(0f, 0f, -0.12f),
        new(0.09f, 0f, 0.09f), new(-0.09f, 0f, 0.09f),
        new(0.09f, 0f, -0.09f), new(-0.09f, 0f, -0.09f),
    };

    public static Player[] Snapshot() => Player.ReadyList
        .Where(player => player != null
            && !player.IsDestroyed
            && player.IsDummy
            && player.Nickname.StartsWith(NicknamePrefix, StringComparison.Ordinal))
        .OrderBy(player => player.PlayerId)
        .ToArray();

    public static Player? FindById(int playerId) =>
        Player.ReadyList.FirstOrDefault(player => player != null && player.PlayerId == playerId);

    public static Player? FindSpike() => Player.ReadyList.FirstOrDefault(player => player != null
        && !player.IsDestroyed
        && player.IsDummy
        && string.Equals(player.Nickname, SpikeNickname, StringComparison.Ordinal));

    public static void AssertExactInitializedPopulation(ScenarioContext ctx, BotStatusSnapshot status)
    {
        Player[] bots = Snapshot();
        ctx.Require(status.Owned == status.Desired,
            $"owned population equals desired ({status.Owned}/{status.Desired})");
        ctx.Require(status.Live == status.Desired,
            $"initialized live population equals desired ({status.Live}/{status.Desired})");
        ctx.Require(bots.Length == status.Desired,
            $"world exposes exactly desired warmup dummies ({bots.Length}/{status.Desired})");
        ctx.Require(status.Tracked >= status.Owned,
            $"tracked population covers all warmup-owned bots ({status.Tracked}>={status.Owned})");
        ctx.Require(status.NetworkReady, "native network readiness is true");
        ctx.Require(status.NavReady, "current-map navigation readiness is true");
        ctx.Require(status.NavGeneration == status.NavReadyGeneration,
            $"nav generation is current ({status.NavGeneration}/{status.NavReadyGeneration})");
        ctx.Require(status.AiRunnerRunning, "global AI runner reports running");
        ctx.Require(status.AiHeartbeat != "never", "global AI runner has emitted a heartbeat");

        foreach (Player bot in bots)
        {
            ctx.Require(bot.IsReady, $"{bot.Nickname} is network-ready");
            ctx.Require(bot.IsAlive, $"{bot.Nickname} is alive");
            ctx.Require(bot.Role == status.DesiredRole,
                $"{bot.Nickname} has desired role {status.DesiredRole} (actual {bot.Role})");
            ctx.Require(bot.RoleBase is FpcStandardRoleBase,
                $"{bot.Nickname} owns a live native FPC role graph");
            ctx.Require(ReferenceHub.AllHubs.Contains(bot.ReferenceHub),
                $"{bot.Nickname} remains in the native hub world");
            ctx.Require(ReferenceHub.TryGetHubNetID(bot.ReferenceHub.netId, out ReferenceHub registered)
                        && registered == bot.ReferenceHub,
                $"{bot.Nickname} has an authoritative native network registration");
            ProbeGroundAndSurroundings(ctx, bot);
        }
    }

    public static Dictionary<int, Vector3> CapturePositions() =>
        Snapshot().ToDictionary(player => player.PlayerId, player => player.Position);

    public static void AssertNoFallsAfterSettling(ScenarioContext ctx, IReadOnlyDictionary<int, Vector3> before)
    {
        foreach (Player bot in Snapshot())
        {
            ctx.Require(before.TryGetValue(bot.PlayerId, out Vector3 start),
                $"{bot.Nickname} identity survived the settle window");
            ctx.Require(bot.Position.y > -100f && start.y > -100f,
                $"{bot.Nickname} did not fall into the void ({start.y:0.##}->{bot.Position.y:0.##})");
            ctx.Require(PlacementProbes.Ground(bot.Position, 3.5f).Ok,
                $"{bot.Nickname} still has walkable ground after settling/moving");
            ctx.Info($"world settle bot={bot.Nickname} id={bot.PlayerId} start={PlacementProbes.Format(start)} "
                     + $"end={PlacementProbes.Format(bot.Position)} moved={Vector3.Distance(start, bot.Position):0.###}m");
        }
    }

    public static void ProbeGroundAndSurroundings(ScenarioContext ctx, Player bot)
    {
        int groundHits = 0;
        foreach (Vector3 offset in GroundOffsets)
        {
            if (PlacementProbes.Ground(bot.Position + offset, 3.5f).Ok)
            {
                groundHits++;
            }
        }

        ctx.Require(groundHits == GroundOffsets.Length,
            $"{bot.Nickname} passed {groundHits}/{GroundOffsets.Length} tightly-spaced downward ground raycasts");

        int surroundingHits = 0;
        Vector3 eye = bot.Position + Vector3.up * 0.5f;
        for (int index = 0; index < 16; index++)
        {
            float angle = index * Mathf.PI * 2f / 16f;
            Vector3 direction = new(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
            RaycastHit[] hits = Physics.RaycastAll(eye, direction, 50f,
                FpcStateProcessor.Mask, QueryTriggerInteraction.Ignore);
            if (hits.Any(hit => hit.collider != null
                && hit.collider.GetComponentInParent<ReferenceHub>() == null
                && hit.collider.GetComponentInParent<HitboxIdentity>() == null))
            {
                surroundingHits++;
            }
        }

        ctx.Info($"world probes bot={bot.Nickname} id={bot.PlayerId} ground={groundHits}/{GroundOffsets.Length} "
                 + $"surrounding={surroundingHits}/16 position={PlacementProbes.Format(bot.Position)}");
    }
}
