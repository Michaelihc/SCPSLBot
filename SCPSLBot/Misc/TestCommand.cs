using CommandSystem;
using Mirror;
using System;

namespace SCPSLBot.Misc
{
    [CommandHandler(typeof(RemoteAdminCommandHandler))]
    internal class TestCommand : ICommand
    {
        public string Command => "plugin_test";

        public string[] Aliases => new string[] { };

        public string Description => "Test command";

        public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
        {
            if (!sender.CheckPermission(PlayerPermissions.GameplayData, out response))
            {
                return false;
            }

            response = "Success response";
            return true;
        }
    }
}
