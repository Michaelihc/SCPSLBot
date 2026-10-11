#nullable enable
using System;
using CommandSystem;
using LabApi.Features.Wrappers;

namespace DuelArena;

[CommandHandler(typeof(ClientCommandHandler))]
public sealed class DuelCommand : ICommand
{
    public string Command => "duel";
    public string[] Aliases => new[] { "1v1" };
    public string Description => "Enter the duel arena and select opponents.";
    public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
    {
        Player? player = Player.Get(sender);
        bool ok = player != null && global::BotRoundControl.BotRoundControlPlugin.Runtime?.Enter(player, player.UserId) == true;
        response = ok ? "已进入决斗区；发起者可射击或按 E 选择对手，其他玩家在观众席等待" : "决斗台暂不可用";
        return ok;
    }
}
[CommandHandler(typeof(ClientCommandHandler))]
public sealed class ChallengeCommand : ICommand
{
    public string Command => "challenge";
    public string[] Aliases => new[] { "duelchallenge" };
    public string Description => "Request a challenge from the viewing deck: .challenge [arena player ID].";
    public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
    {
        response = "用法：.challenge [擂台玩家 ID]";
        int? targetId = null;
        if (arguments.Count > 1) return false;
        if (arguments.Count == 1)
        {
            if (!int.TryParse(arguments.At(0), out int parsed)) return false;
            targetId = parsed;
        }
        Player? player = Player.Get(sender);
        return player != null && global::BotRoundControl.BotRoundControlPlugin.Runtime != null
            && global::BotRoundControl.BotRoundControlPlugin.Runtime.RequestChallenge(player, targetId, out response);
    }
}
[CommandHandler(typeof(ClientCommandHandler))]
public sealed class AcceptCommand : ICommand
{
    public string Command => "accept";
    public string[] Aliases => new[] { "yes", "y", "duelaccept" };
    public string Description => "Accept a pending duel invitation or challenge: .accept [sender ID].";
    public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
    {
        response = "没有可接受的决斗邀请";
        int? requesterId = null;
        if (arguments.Count > 1) { response = "用法：.accept [邀请者或挑战者 ID]"; return false; }
        if (arguments.Count == 1)
        {
            if (!int.TryParse(arguments.At(0), out int parsed)) { response = "用法：.accept [邀请者或挑战者 ID]"; return false; }
            requesterId = parsed;
        }
        Player? player = Player.Get(sender);
        return player != null && global::BotRoundControl.BotRoundControlPlugin.Runtime != null && global::BotRoundControl.BotRoundControlPlugin.Runtime.Accept(player, out response, requesterId);
    }
}
[CommandHandler(typeof(ClientCommandHandler))]
public sealed class WatchCommand : ICommand
{
    public string Command => "spectate";
    public string[] Aliases => new[] { "view", "watch" };
    public string Description => "Toggle protected duel viewing; toggle again to return to Surface.";
    public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
    {
        response = "决斗观众席暂不可用";
        Player? player = Player.Get(sender);
        return player != null && global::BotRoundControl.BotRoundControlPlugin.Runtime != null && global::BotRoundControl.BotRoundControlPlugin.Runtime.Watch(player, out response);
    }
}
[CommandHandler(typeof(ClientCommandHandler))]
public sealed class LeaveCommand : ICommand
{
    public string Command => "leave";
    public string[] Aliases => new[] { "duelleave", "cancelduel" };
    public string Description => "Cancel or forfeit your duel, or leave the viewing deck.";
    public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
    {
        Player? player = Player.Get(sender);
        bool owned = player != null && global::BotRoundControl.BotRoundControlPlugin.Runtime?.Owns(player.ReferenceHub) == true;
        if (owned) global::BotRoundControl.BotRoundControlPlugin.Runtime!.Leave(player!, "已退出决斗");
        response = owned ? "已返回地表" : "你不在决斗台";
        return owned;
    }
}
[CommandHandler(typeof(RemoteAdminCommandHandler))]
public sealed class DuelStatusCommand : ICommand
{
    public string Command => "duel_status";
    public string[] Aliases => Array.Empty<string>();
    public string Description => "Show duel arena readiness and ownership.";
    public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
    {
        if (!sender.CheckPermission(PlayerPermissions.FacilityManagement, out response)) return false;
        response = global::BotRoundControl.BotRoundControlPlugin.Runtime?.Status ?? "DuelArena disabled";
        return global::BotRoundControl.BotRoundControlPlugin.Runtime != null;
    }
}
