using CommandSystem;
using SCPSLBot.Warmup;
using System;

namespace SCPSLBot.AI.Commands
{
    [CommandHandler(typeof(RemoteAdminCommandHandler))]
    internal class BotAddCommand : ICommand
    {
        public string Command => "bot_add";

        public string[] Aliases => new string[] { };

        public string Description => "Bot add";

        public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
        {
            if (!sender.CheckPermission(PlayerPermissions.PlayersManagement, out response))
            {
                return false;
            }

            return WarmupManager.Instance.TryAddMaintainedBot(out response);
        }
    }
}
