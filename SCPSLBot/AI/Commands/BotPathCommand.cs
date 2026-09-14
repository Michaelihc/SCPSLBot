using CommandSystem;
using RemoteAdmin;
using System;

namespace SCPSLBot.AI.Commands
{
    [CommandHandler(typeof(RemoteAdminCommandHandler))]
    internal class BotPathCommand : ICommand
    {
        public string Command => "bot_path";

        public string[] Aliases => new[] { "bot_follow" };

        public string Description => "Toggles bots pathing to your position.";

        public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
        {
            if (!sender.CheckPermission(PlayerPermissions.PlayersManagement, out response))
            {
                return false;
            }

            if (sender is not PlayerCommandSender playerCommandSender)
            {
                response = "You must be in-game to use this command!";
                return false;
            }

            var enabled = BotManager.Instance.TogglePathToTarget(playerCommandSender.ReferenceHub);
            response = enabled
                ? "Bots are now pathing to your position, updated once per second."
                : "Bots stopped pathing to your position.";
            return true;
        }
    }
}
