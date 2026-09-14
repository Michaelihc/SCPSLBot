using CommandSystem;
using PlayerRoles;
using RemoteAdmin;
using SCPSLBot.Navigation.Mesh;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;

namespace SCPSLBot.Navigation.Commands
{
    [CommandHandler(typeof(NavCell))]
    internal class NavCellEdgeSliceCommand : ICommand
    {
        public string Command { get; } = "edge_slice";

        public string[] Aliases { get; } = new string[] { };

        public string Description { get; } = "Slices closest cell edge at direction from current position.";

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

            if (!NavigationMeshEditor.Instance.SliceClosestRoomCellEdge(playerCommandSender.ReferenceHub.transform.position, playerCommandSender.ReferenceHub.transform.forward))
            {
                response = $"No nearby cell edge.";
                return false;
            }

            response = $"Vertex on cell edge at direction created and added to cell.";
            return true;
        }
    }
}
