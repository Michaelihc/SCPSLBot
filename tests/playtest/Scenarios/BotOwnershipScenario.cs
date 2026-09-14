using LabApi.Features.Wrappers;
using PlayerRoles;
using PlaytestHarness.Core;
using SCPSLBot.PlaytestScenarios.Harness;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SCPSLBot.PlaytestScenarios.Scenarios;

/// <summary>
/// Exercises bot ownership only through real RA commands and public world/status observations.
/// The isolated server is restored to its original warmup mode and population after a passing run.
/// </summary>
public sealed class BotOwnershipScenario : Scenario
{
    public override string Name => "scpslbot-bot-ownership";
    public override string[] Aliases => ["bot-ownership", "bot-add-independent"];
    public override string[] Suites => ["scpslbot-mutating"];
    public override string Description => "ISOLATED PORT: proves bot_add is independent and bot_manage/bot_unmanage transfer population ownership.";
    public override FidelityRange Supported => FidelityRange.Only(Fidelity.Standard);
    public override bool IncludeInRunAll => false;
    public override float TimeoutSeconds => 120f;

    public override IEnumerator<float> Run(ScenarioContext ctx)
    {
        BotStatusSnapshot initial = BotStatusSnapshot.Read();
        string originalMode = initial.Mode;
        int manualPlayerId = 0;
        ReferenceHub? manualHub = null;

        try
        {
            WarmupPopulationRecoveryScenario.RequireCommand(ctx,
                NativeCommandAdapter.RemoteAdmin("bot_warmup standard"), "enable Standard warmup");
            ThrottledCondition ready = new(() =>
            {
                BotStatusSnapshot status = BotStatusSnapshot.Read();
                return status.Owned == status.Desired && status.Live == status.Desired;
            });
            yield return ctx.WaitUntil(ready.Check, 12f, "initial maintained population ready");

            BotStatusSnapshot baseline = BotStatusSnapshot.Read();
            NativeCommandResult add = NativeCommandAdapter.RemoteAdmin("bot_add");
            WarmupPopulationRecoveryScenario.RequireCommand(ctx, add, "spawn an independent bot");
            Dictionary<string, string> addFields = CommandFields.ParseWhitespace(add.Response);
            ctx.Require(addFields.TryGetValue("player_id", out string playerIdText)
                        && int.TryParse(playerIdText, out manualPlayerId),
                $"bot_add returned a parseable player_id: {add.Response}");

            ThrottledCondition independentReady = new(() =>
            {
                BotStatusSnapshot status = BotStatusSnapshot.Read();
                Player? bot = WarmupBotWorld.FindById(manualPlayerId);
                return bot != null && bot.IsAlive
                    && status.Owned == baseline.Owned
                    && status.Tracked == baseline.Tracked + 1
                    && status.Independent == baseline.Independent + 1;
            });
            yield return ctx.WaitUntil(independentReady.Check, 8f,
                "bot_add created a live bot without changing maintained ownership");
            manualHub = WarmupBotWorld.FindById(manualPlayerId)?.ReferenceHub;
            ctx.Require(manualHub != null, "independent bot exposed its live ReferenceHub");

            WarmupPopulationRecoveryScenario.RequireCommand(ctx,
                NativeCommandAdapter.RemoteAdmin($"forcerole {manualPlayerId} ClassD"),
                "change the independent bot role through native RA");
            yield return ctx.WaitUntil(
                () => WarmupBotWorld.FindById(manualPlayerId)?.Role == RoleTypeId.ClassD,
                4f,
                "independent bot reached the RA-selected ClassD role");
            yield return ctx.Wait(1f);
            ctx.Require(WarmupBotWorld.FindById(manualPlayerId)?.Role == RoleTypeId.ClassD,
                "independent bot retained its RA-selected role across multiple population reconciles");

            WarmupPopulationRecoveryScenario.RequireCommand(ctx,
                NativeCommandAdapter.RemoteAdmin($"bot_manage {manualPlayerId}"),
                "adopt the independent bot into the maintained population");
            ThrottledCondition managedRepair = new(() =>
            {
                BotStatusSnapshot status = BotStatusSnapshot.Read();
                Player? bot = WarmupBotWorld.FindById(manualPlayerId);
                return bot != null && bot.IsAlive && bot.Role != RoleTypeId.ClassD
                    && status.Owned == status.Desired
                    && status.Live == status.Desired;
            });
            yield return ctx.WaitUntil(managedRepair.Check, 12f,
                "managed bot adopted a maintained role and the population remained exact");

            WarmupPopulationRecoveryScenario.RequireCommand(ctx,
                NativeCommandAdapter.RemoteAdmin($"forcerole {manualPlayerId} ClassD"),
                "try to override the managed bot role through native RA");
            yield return ctx.WaitUntil(
                () => WarmupBotWorld.FindById(manualPlayerId) is { IsAlive: true } bot
                    && bot.Role != RoleTypeId.ClassD,
                5f,
                "population management repaired the managed bot's RA role override");

            WarmupPopulationRecoveryScenario.RequireCommand(ctx,
                NativeCommandAdapter.RemoteAdmin($"bot_unmanage {manualPlayerId}"),
                "release the bot from maintained population control");
            ThrottledCondition replacementReady = new(() =>
            {
                BotStatusSnapshot status = BotStatusSnapshot.Read();
                return WarmupBotWorld.FindById(manualPlayerId) is { IsAlive: true }
                    && status.Owned == status.Desired
                    && status.Live == status.Desired
                    && status.Independent == baseline.Independent + 1;
            });
            yield return ctx.WaitUntil(replacementReady.Check, 12f,
                "released bot survived while the controller restored its maintained population");

            WarmupPopulationRecoveryScenario.RequireCommand(ctx,
                NativeCommandAdapter.RemoteAdmin($"forcerole {manualPlayerId} FacilityGuard"),
                "change the released bot role through native RA");
            yield return ctx.WaitUntil(
                () => WarmupBotWorld.FindById(manualPlayerId)?.Role == RoleTypeId.FacilityGuard,
                4f,
                "released bot reached the RA-selected FacilityGuard role");
            yield return ctx.Wait(1f);
            ctx.Require(WarmupBotWorld.FindById(manualPlayerId)?.Role == RoleTypeId.FacilityGuard,
                "released bot retained its RA-selected role across multiple population reconciles");

            WarmupPopulationRecoveryScenario.RequireCommand(ctx,
                NativeCommandAdapter.RemoteAdmin($"bot_manage {manualPlayerId}"),
                "re-adopt the released bot for deterministic cleanup");
            WarmupPopulationRecoveryScenario.RequireCommand(ctx,
                NativeCommandAdapter.RemoteAdmin("bot_warmup none"),
                "disable maintained population for cleanup");
            yield return ctx.WaitUntil(
                () =>
                {
                    Player? bot = WarmupBotWorld.FindById(manualPlayerId);
                    return bot == null || bot.IsDestroyed || !ReferenceHub.AllHubs.Contains(bot.ReferenceHub);
                },
                5f,
                "cleanup despawned the re-adopted bot");
            yield return ctx.WaitUntil(
                () => manualHub == null || !Player.List.Any(candidate =>
                    candidate != null && ReferenceEquals(candidate.ReferenceHub, manualHub)),
                2f,
                "LabAPI did not retain a wrapper for the destroyed bot hub");
        }
        finally
        {
            NativeCommandAdapter.RemoteAdmin($"bot_warmup {originalMode.ToLowerInvariant()}");
        }
    }
}
