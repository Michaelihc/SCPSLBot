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
        response = ok ? "已进入决斗台，请射击或按 E 选择对手" : "决斗台暂不可用，或正被其他玩家使用";
        return ok;
    }
}
[CommandHandler(typeof(ClientCommandHandler))]
public sealed class AcceptCommand : ICommand
{
    public string Command => "accept";
    public string[] Aliases => new[] { "yes", "y", "duelaccept" };
    public string Description => "Accept a pending duel invitation; the first acceptance wins.";
    public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
    {
        response = "没有可接受的决斗邀请";
        Player? player = Player.Get(sender);
        return player != null && global::BotRoundControl.BotRoundControlPlugin.Runtime != null && global::BotRoundControl.BotRoundControlPlugin.Runtime.Accept(player, out response);
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
        response = "当前没有可观战的决斗";
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
