using System;
using System.Text;
using CommandSystem;
using MapGeneration;

namespace SCPSLBot.AI.Commands;

/// <summary>One line per managed bot: role, room, active behavior, combat, locomotion and path state.</summary>
[CommandHandler(typeof(RemoteAdminCommandHandler))]
internal sealed class BotDebugCommand : ICommand
{
    public string Command => "bot_debug";
    public string[] Aliases => Array.Empty<string>();
    public string Description => "List every bot's role, room, active behavior and navigation state.";

    public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
    {
        if (!sender.CheckPermission(PlayerPermissions.FacilityManagement, out response)) return false;

        var builder = new StringBuilder();
        foreach (var (hub, botHub) in BotManager.Instance.BotPlayers)
        {
            if (hub == null || botHub?.FpcPlayer == null)
            {
                continue;
            }

            var bot = botHub.FpcPlayer;
            var role = hub.roleManager?.CurrentRole?.RoleTypeId.ToString() ?? "none";
            var position = hub.transform.position;
            var room = RoomUtils.TryGetRoom(position, out var currentRoom) && currentRoom != null ? currentRoom.Name.ToString() : "none";
            var navigator = bot.Navigator;
            var path = navigator == null ? "none" : navigator.HasPath ? $"path/{navigator.PathNodeCount}" : navigator.HasPartialPath ? $"partial/{navigator.PathNodeCount}" : "nopath";
            var combat = bot.Combat.DiagnosticHasTarget ? $"{bot.Combat.DiagnosticState}->{bot.Combat.DiagnosticTarget}{(bot.Combat.DiagnosticHasLineOfSight ? "(los)" : "(memory)")}" : "none";
            builder.Append('\n')
                .Append(hub.PlayerId).Append(' ').Append(hub.nicknameSync?.MyNick).Append(" role=").Append(role)
                .Append(" room=").Append(room)
                .Append($" pos=({position.x:F1},{position.y:F1},{position.z:F1})")
                .Append(" behavior=").Append(bot.Brain?.Active?.Name ?? "none")
                .Append(bot.Brain?.Active is FirstPersonControl.Brain.IDescribedBehavior { Detail: { } detail } ? ":" + detail : string.Empty)
                .Append(" combat=").Append(combat)
                .Append(" loco=").Append(bot.Locomotion?.DiagnosticState ?? "-")
                .Append(" nav=").Append(path)
                .Append(navigator != null && (navigator.HasPath || navigator.HasPartialPath) ? $" wp=({navigator.CurrentWaypoint.x:F1},{navigator.CurrentWaypoint.y:F1},{navigator.CurrentWaypoint.z:F1})" : string.Empty)
                .Append($" stuck={bot.StuckRecovery.StuckSeconds:F1}s")
                .Append(" lease=").Append(bot.ItemLease.IsHeld);
        }

        response = builder.Length == 0 ? "No bots." : "BOT_DEBUG" + builder;
        return true;
    }
}
