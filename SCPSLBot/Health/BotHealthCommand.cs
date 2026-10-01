using CommandSystem;
using System;

namespace SCPSLBot.Health;

[CommandHandler(typeof(GameConsoleCommandHandler))]
[CommandHandler(typeof(RemoteAdminCommandHandler))]
internal sealed class BotHealthCommand : ICommand
{
    public string Command => "bot_health";
    public string[] Aliases => Array.Empty<string>();
    public string Description => "Show network registry recovery and identity diagnostics.";

    public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
    {
        if (!sender.CheckPermission(PlayerPermissions.FacilityManagement, out response))
            return false;
        response = LabApiPlugin.Instance?.NetworkHealth?.Status ?? "Network registry monitoring is disabled.";
        return true;
    }
}
