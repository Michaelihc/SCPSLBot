using CommandSystem;
using PlayerRoles;
using RemoteAdmin;
using SCPSLBot.Navigation.Mesh;
using System;

namespace SCPSLBot.Navigation.Commands
{
    [CommandHandler(typeof(NavCell))]
    internal class NavCellMakeCommand : ICommand
    {
        public string Command { get; } = "make";

        public string[] Aliases { get; } = new string[] { };

        public string Description { get; } = "Makes new navigation mesh cell from selected vertices.";

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

            Cell formCell;
            switch (formType)
            {
                case "room": 
                    formCell = NavigationMeshEditor.Instance.MakeCell(playerCommandSender.ReferenceHub.transform.position);
                    break;
                default:
                    response = "Unrecognized form type argument!";
                    return false;
            }

            if (formCell == null)
            {
                response = $"Failed to create {formType} form cell!";
                return false;
            }

            response = $"Cell at local center position {formCell.CenterPosition} created.";
            return true;
        }
    }
}
