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
    internal class NavCellVertexCreateCommand : ICommand
    {
        public string Command { get; } = "vertex_create";

        public string[] Aliases { get; } = new string[] { };

        public string Description { get; } = "Creates vertex on navigation mesh cell nearest edge.";

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

            if (!NavigationMeshEditor.Instance.CreateVertexOnClosestRoomEdge(playerCommandSender.ReferenceHub.transform.position))
            {
                response = $"No nearby cell.";
                return false;
            }

            response = $"Vertex on cell edge created and added to cell.";
            return true;
        }
    }
}
