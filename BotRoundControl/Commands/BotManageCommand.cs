using CommandSystem;
using SCPSLBot.Warmup;
using System;

namespace SCPSLBot.AI.Commands
{
    [CommandHandler(typeof(RemoteAdminCommandHandler))]
    internal sealed class BotManageCommand : ICommand
    {
        public string Command => "bot_manage";
        public string[] Aliases => Array.Empty<string>();
        public string Description => "Adopt an independent SCPSLBot dummy into the maintained warmup population.";

        public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
        {
            if (!sender.CheckPermission(PlayerPermissions.PlayersManagement, out response))
            {
                return false;
            }

            if (arguments.Count != 1 || !int.TryParse(arguments.At(0), out int playerId) || playerId <= 0)
            {
                response = "Usage: bot_manage <player ID>";
                return false;
            }

            return WarmupManager.Instance.TryManageBot(playerId, out response);
        }
    }
}
