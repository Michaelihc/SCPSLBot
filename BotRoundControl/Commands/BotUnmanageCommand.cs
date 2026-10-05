using CommandSystem;
using SCPSLBot.Warmup;
using System;

namespace SCPSLBot.AI.Commands
{
    [CommandHandler(typeof(RemoteAdminCommandHandler))]
    internal sealed class BotUnmanageCommand : ICommand
    {
        public string Command => "bot_unmanage";
        public string[] Aliases => Array.Empty<string>();
        public string Description => "Release an SCPSLBot dummy from maintained warmup population control.";

        public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
        {
            if (!sender.CheckPermission(PlayerPermissions.PlayersManagement, out response))
            {
                return false;
            }

            if (arguments.Count != 1 || !int.TryParse(arguments.At(0), out int playerId) || playerId <= 0)
            {
                response = "Usage: bot_unmanage <player ID>";
                return false;
            }

            return WarmupManager.Instance.TryUnmanageBot(playerId, out response);
        }
    }
}
