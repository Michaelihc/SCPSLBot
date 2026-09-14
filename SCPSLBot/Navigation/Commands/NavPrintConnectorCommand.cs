using CommandSystem;
using RemoteAdmin;
using SCPSLBot.Navigation.Mesh;
using System;

namespace SCPSLBot.Navigation.Commands
{
    [CommandHandler(typeof(Nav))]
    internal class NavPrintConnectorCommand : ICommand
    {
        public string Command { get; } = "print_connector";

        public string[] Aliases { get; } = new string[] { };

        public string Description { get; } = "Prints closest room connector.";

        public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
        {
            if (!sender.CheckPermission(PlayerPermissions.GameplayData, out response))
            {
                return false;
            }

            if (sender is not PlayerCommandSender playerCommandSender)
            {
                response = "You must be in-game to use this command!";
                return false;
            }

            var playerPosition = playerCommandSender.ReferenceHub.transform.position;
            var connector = NavigationMeshEditor.GetClosestConnector(playerPosition, out var direction, out var orientation, out var room);
            var connectorForm = NavigationMesh.GetForm(connector);
            var roomForm = NavigationMesh.GetForm(room.gameObject);

            response = $"Closest connector {connectorForm} with orientation {orientation} at direction {direction} from room {roomForm}";
            return true;
        }
    }
}
