using CommandSystem;
using PlayerRoles;
using RemoteAdmin;
using SCPSLBot.Navigation.Mesh;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SCPSLBot.Navigation.Commands
{
    [CommandHandler(typeof(NavCell))]
    internal class NavCellDissolveCommand : ICommand
    {
        public string Command { get; } = "dissolve";

        public string[] Aliases { get; } = new string[] { };

        public string Description { get; } = "Dissolves navigation mesh cell within.";

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

            if (!playerCommandSender.ReferenceHub.IsAlive())
            {
                response = "Command disabled when you are not alive!";
                return false;
            }

            if (!NavigationMeshEditor.Instance.DissolveCell(playerCommandSender.ReferenceHub.transform.position))
            {
                response = $"No cell to be dissolved.";
                return false;
            }

            response = $"Cell dissolved.";
            return true;
        }
    }
}
