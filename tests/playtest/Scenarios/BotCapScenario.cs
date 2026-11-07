using LabApi.Features.Wrappers;
using PlayerRoles;
using PlaytestHarness.Core;
using SCPSLBot.PlaytestScenarios.Harness;
using System.Collections.Generic;
using System.Linq;

namespace SCPSLBot.PlaytestScenarios.Scenarios;

public abstract class BotCapScenarioBase : Scenario
{
    protected abstract int RoundBots { get; }
    protected abstract int Cap { get; }
    public override string Description => "Configured round bots and native bot_add obey the configured cap.";
    public override FidelityRange Supported => FidelityRange.Only(Fidelity.Standard);
    public override bool IncludeInRunAll => false;

    public override IEnumerator<float> Run(ScenarioContext ctx)
    {
        yield return ctx.WaitUntil(() => NormalRoundWorld.Bots().Length == RoundBots, 10f,
            $"{RoundBots} configured round spectators exist");
        yield return ctx.Wait(1f);
        ctx.Require(NormalRoundWorld.Tracked() == RoundBots
            && NormalRoundWorld.Bots().All(bot => bot.Role == RoleTypeId.Spectator),
            "native spectator world state matches the configured round population");

        for (int count = RoundBots; count < Cap; count++)
        {
            NativeCommandResult added = NativeCommandAdapter.RemoteAdmin("bot_add");
            ctx.Require(added.Success, $"bot_add succeeds below cap {Cap}: {added.Response}");
            Dictionary<string, string> fields = CommandFields.ParseWhitespace(added.Response);
            ctx.Require(fields.TryGetValue("player_id", out string playerIdText)
                && int.TryParse(playerIdText.TrimEnd('.'), out _), "bot_add identifies its created player");
            int playerId = int.Parse(fields["player_id"].TrimEnd('.'));
            yield return ctx.WaitUntil(() => WarmupBotWorld.FindById(playerId)?.IsAlive == true,
                5f, "bot_add created a live native player");
            Player bot = WarmupBotWorld.FindById(playerId)!;
            ctx.Require(bot.IsDummy && bot.Role == RoleTypeId.ChaosRifleman,
                "created bot is a native Chaos Rifleman dummy");
            WarmupBotWorld.ProbeGroundAndSurroundings(ctx, bot);
            ctx.Require(NativeCommandAdapter.RemoteAdmin($"forcerole {playerId} Spectator").Success,
                "return manual bot to spectator");
        }

        for (int attempt = 0; attempt < 2; attempt++)
        {
            NativeCommandResult rejected = NativeCommandAdapter.RemoteAdmin("bot_add");
            ctx.Require(!rejected.Success && rejected.Response.Contains($"cap reached ({Cap})"),
                $"bot_add reports configured cap {Cap}: {rejected.Response}");
            yield return ctx.Wait(0.5f);
            ctx.Require(NormalRoundWorld.Tracked() == Cap
                && Player.ReadyList.Count(player => player.IsDummy && !player.IsDestroyed) == Cap,
                "rejected addition leaves the native dummy population unchanged");
        }
    }
}

public sealed class RaisedBotCapScenario : BotCapScenarioBase
{
    public override string Name => "scpslbot-raised-bot-cap";
    protected override int RoundBots => 11;
    protected override int Cap => 12;
}

public sealed class ClampedBotCapScenario : BotCapScenarioBase
{
    public override string Name => "scpslbot-clamped-bot-cap";
    protected override int RoundBots => 2;
    protected override int Cap => 2;
}

public sealed class DisabledBotCapScenario : BotCapScenarioBase
{
    public override string Name => "scpslbot-disabled-bot-cap";
    protected override int RoundBots => 0;
    protected override int Cap => 0;
}
