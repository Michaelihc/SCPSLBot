using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.Handlers;
using LabApi.Features.Wrappers;
using PlayerRoles;
using PlaytestHarness.Core;
using SCPSLBot.PlaytestScenarios.Harness;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace SCPSLBot.PlaytestScenarios.Scenarios;

/// <summary>
/// Proves an ordered bot does not attack. An armed autonomous spike first has to land a real hit on a
/// hostile bot, so a spike that never fights cannot pass. It then gets an order a keycard-less bot
/// cannot route, and a reachable one, with the hostile kept in its line of sight throughout.
/// </summary>
public sealed class BotOrderPassivityScenario : Scenario
{
    private const string UnreachableRoom = "Hcz049";
    private const string ReachableRoom = "LczToilets";
    private const float HoldIntervalSeconds = 1f;
    private const float TargetDistance = 3f;
    private const int MinimumExposures = 5;

    public override string Name => "scpslbot-order-passivity";
    public override string Description => "An armed hostile spike stops attacking after a rejected order and walks an accepted order past a visible enemy.";
    public override FidelityRange Supported => FidelityRange.Only(Fidelity.Standard);
    public override bool IncludeInRunAll => false;
    public override float TimeoutSeconds => 420f;

    public override IEnumerator<float> Run(ScenarioContext ctx)
    {
        Issue2World.RequireReady(ctx);
        yield return ctx.EnsureRoundStarted();

        Player? spike = null;
        Player? target = null;
        int hits = 0;
        void OnHurting(PlayerHurtingEventArgs ev)
        {
            if (spike == null || target == null
                || !ReferenceEquals(ev.Player?.ReferenceHub, target.ReferenceHub)
                || !ReferenceEquals(ev.Attacker?.ReferenceHub, spike.ReferenceHub))
            {
                return;
            }

            hits++;
            ev.IsAllowed = false; // The count is the oracle; the target has to outlive every phase.
        }

        NativeCommandAdapter.RemoteAdmin("botspike cleanup");
        PlayerEvents.Hurting += OnHurting;
        try
        {
            Issue2World.Command(ctx, "botspike start autonomous");
            yield return ctx.WaitUntil(() => WarmupBotWorld.FindSpike() is { IsReady: true, IsAlive: true }, 10f,
                "autonomous spike reached a live native role");
            yield return ctx.Wait(2.5f); // Drain the delayed requested-role retries.
            spike = WarmupBotWorld.FindSpike();
            ctx.Require(spike is { Role: RoleTypeId.ClassD }, "spike kept its keycard-less ClassD role");

            Dictionary<string, string> added = CommandFields.ParseWhitespace(Issue2World.Command(ctx, "bot_add").Response);
            ctx.Require(added.TryGetValue("player_id", out string id) && int.TryParse(id, out int targetId),
                "bot_add reports the hostile bot's player_id");
            targetId = int.Parse(added["player_id"]);
            Issue2World.Command(ctx, $"forcerole {targetId} Scientist");
            yield return ctx.WaitUntil(() => WarmupBotWorld.FindById(targetId)?.Role == RoleTypeId.Scientist, 5f,
                "hostile bot reached its Scientist role");
            target = WarmupBotWorld.FindById(targetId);

            ctx.Arrange("arm the spike so a hostile target is a real engagement", () =>
            {
                spike!.AddItem(ItemType.GunCOM15);
                spike.SetAmmo(ItemType.Ammo9x19, 90);
            });

            float deadline = Time.realtimeSinceStartup + 40f;
            while (hits == 0 && Time.realtimeSinceStartup < deadline)
            {
                KeepTargetInSight(ctx, spike!, target!, Vector3.zero);
                yield return ctx.Wait(HoldIntervalSeconds);
            }

            ctx.Require(hits > 0, "armed autonomous spike is hostile: it landed a hit on the visible Scientist");
            ctx.Info($"ORDER_PASSIVITY baseline hits={hits}");

            // A rejected order.
            Issue2World.Command(ctx, $"botspike walk {UnreachableRoom}");
            yield return ctx.WaitUntil(RouteStopped, 10f, "the unreachable order reached a terminal result");
            string rejected = Status(ctx);
            ctx.Info($"ORDER_PASSIVITY rejected status: {rejected}");
            yield return ctx.Wait(0.5f); // A shot already resolved is not caused by the order.
            hits = 0;
            Vector3 heldAt = spike!.Position;
            int exposures = 0;
            for (int tick = 0; tick < 10; tick++)
            {
                if (KeepTargetInSight(ctx, spike, target!, Vector3.zero))
                {
                    exposures++;
                }

                yield return ctx.Wait(HoldIntervalSeconds);
            }

            ctx.Info($"ORDER_PASSIVITY rejected hits={hits} exposures={exposures} moved={Vector3.Distance(heldAt, spike.Position):0.##}m");
            ctx.Require(exposures >= MinimumExposures, $"hostile stayed visible after the rejected order (exposures={exposures})");
            ctx.Require(hits == 0, $"spike stopped attacking after the rejected order (hits={hits}, status: {rejected})");
            ctx.Require(Vector3.Distance(heldAt, spike.Position) < 1.5f, $"spike holds position after the rejected order: {rejected}");
            ctx.Require(Fields(rejected).TryGetValue("order", out string rejectedOrder) && rejectedOrder == "FailedNoPath",
                $"the rejected order is visible to the caller: {rejected}");
            ctx.Require(rejected.Contains("keycard"), $"the failure names the keycard cause: {rejected}");

            // An accepted order, walked past the same hostile.
            Vector3 start = spike.Position;
            Vector3 previous = start;
            Issue2World.Command(ctx, $"botspike walk {ReachableRoom}");
            hits = 0;
            exposures = 0;
            deadline = Time.realtimeSinceStartup + 240f;
            while (!RouteStopped() && Time.realtimeSinceStartup < deadline)
            {
                if (KeepTargetInSight(ctx, spike, target!, spike.Position - previous))
                {
                    exposures++;
                }

                previous = spike.Position;
                yield return ctx.Wait(HoldIntervalSeconds);
            }

            string walked = Status(ctx);
            ctx.Info($"ORDER_PASSIVITY walked hits={hits} exposures={exposures} status: {walked}");
            ctx.Require(Fields(walked).TryGetValue("order", out string walkedOrder) && walkedOrder == "Completed",
                $"accepted order completed despite the visible hostile: {walked}");
            ctx.Require(exposures >= MinimumExposures, $"hostile stayed visible during the walk (exposures={exposures})");
            ctx.Require(hits == 0, $"spike did not attack during the accepted order (hits={hits})");
            ctx.Require(Vector3.Distance(start, spike.Position) > 1f, "spike physically walked");

            // A finished order stays passive.
            exposures = 0;
            for (int tick = 0; tick < 6; tick++)
            {
                if (KeepTargetInSight(ctx, spike, target!, Vector3.zero))
                {
                    exposures++;
                }

                yield return ctx.Wait(HoldIntervalSeconds);
            }

            ctx.Require(exposures >= MinimumExposures, $"hostile stayed visible after arrival (exposures={exposures})");
            ctx.Require(hits == 0, $"spike stayed passive after arrival (hits={hits})");
        }
        finally
        {
            PlayerEvents.Hurting -= OnHurting;
            if (target is { IsDestroyed: false })
            {
                NativeCommandAdapter.RemoteAdmin($"dummies destroy {target.PlayerId}");
            }

            NativeCommandAdapter.RemoteAdmin("botspike cleanup");
        }
    }

    /// <summary>Places the hostile in the spike's line of sight, behind its travel so it cannot block the route.</summary>
    private static bool KeepTargetInSight(ScenarioContext ctx, Player spike, Player target, Vector3 travel)
    {
        if (!target.IsAlive)
        {
            throw new RequireException("the hostile target died despite the hit counter");
        }

        Vector3 behind = travel.sqrMagnitude > 0.01f ? -travel.normalized : Vector3.forward;
        IEnumerable<Vector3> directions = Enumerable.Range(0, 8)
            .Select(step => Quaternion.Euler(0f, step * 45f, 0f) * Vector3.forward)
            .OrderByDescending(direction => Vector3.Dot(direction, behind));
        foreach (Vector3 direction in directions)
        {
            Vector3 probe = spike.Position + direction * TargetDistance;
            if (Issue2World.TryStanding(target, probe, out Vector3 point) && Issue2World.Visible(spike.Position, point))
            {
                ctx.Arrange("hold the hostile in the spike's line of sight", () => Issue2World.Place(ctx, target, point));
                return true;
            }
        }

        return false;
    }

    private static bool RouteStopped()
    {
        NativeCommandResult result = NativeCommandAdapter.RemoteAdmin("botspike status");
        return result.Success && Fields(result.Response).TryGetValue("routeRunning", out string running)
            && string.Equals(running, "False", StringComparison.OrdinalIgnoreCase);
    }

    private static string Status(ScenarioContext ctx) => Issue2World.Command(ctx, "botspike status").Response;

    private static Dictionary<string, string> Fields(string response) => CommandFields.ParseWhitespace(response);
}
