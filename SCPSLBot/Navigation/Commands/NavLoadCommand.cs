using CommandSystem;
using PlayerRoles;
using RemoteAdmin;
using SCPSLBot.Navigation;
using SCPSLBot.Navigation.Mesh;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SCPSLBot.Navigation.Commands
{
    [CommandHandler(typeof(Nav))]
    internal class NavLoadCommand : ICommand
    {
        public string Command { get; } = "load";

        public string[] Aliases { get; } = new string[] { };

        public string Description { get; } = "Re-loads navigation mesh from storage.";

        public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
        {
            if (!sender.CheckPermission(PlayerPermissions.ServerConfigs, out response))
            {
                return false;
            }

            if (sender is not PlayerCommandSender playerCommandSender)
            {
                response = "You must be in-game to use this command!";
                return false;
            }

            NavigationMesh.ResetMeshes();

            NavigationSystem.Instance.LoadConnectMeshes();

            response = $"Navigation mesh re-loaded.";
            return true;
        }
    }
}
