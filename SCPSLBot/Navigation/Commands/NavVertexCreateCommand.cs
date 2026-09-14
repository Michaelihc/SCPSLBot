using CommandSystem;
using PlayerRoles;
using RemoteAdmin;
using SCPSLBot.Navigation.Mesh;
using System;

namespace SCPSLBot.Navigation.Commands
{
    [CommandHandler(typeof(NavVertex))]
    internal class NavVertexCreateCommand : ICommand
    {
        public string Command { get; } = "create";

        public string[] Aliases { get; } = new string[] { };

        public string Description { get; } = "Creates navigation mesh vertex at current position.";

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

            var formType = "room";
            if (arguments.Count > 0)
            {
                formType = arguments[0];
            }

            var playerPosition = playerCommandSender.ReferenceHub.transform.position;

            Vertex formVertex;
            switch (formType)
            {
                case "room":
                    formVertex = NavigationMeshEditor.Instance.CreateVertex(playerPosition);
                    break;
                default:
                    response = "Unrecognized form type argument!";
                    return false;
            }

            if (formVertex == null)
            {
                response = $"Failed to create {formType} form vertex.";
                return false;
            }

            response = $"Vertex at local position {formVertex.Position} created.";
            return true;
        }
    }
}
