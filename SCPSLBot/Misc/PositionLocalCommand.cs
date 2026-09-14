using CommandSystem;
using MapGeneration;
using Mirror;
using PlayerRoles;
using RemoteAdmin;
using System;
using UnityEngine;

namespace SCPSLBot.Misc
{
    [CommandHandler(typeof(RemoteAdminCommandHandler))]
    internal class PositionLocalCommand : ICommand
    {
        public string Command => "position_local";

        public string[] Aliases => new string[] { };

        public string Description => "Returns calling player relative position to room player currenty in.";

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

            if (!playerCommandSender.ReferenceHub.IsAlive())
            {
                response = "Command disabled when you are not alive!";
                return false;
            }

            var playerPos = playerCommandSender.ReferenceHub.transform.position;
            
            if (!RoomUtils.TryGetRoom(playerPos, out var room))
            {
                response = "Could not determine room player within.";
                return false;
            }

            var relPlayerPos = room.transform.InverseTransformPoint(playerPos);
            var roomForm = room.gameObject.name;

            response = $"Relative player {playerCommandSender.Nickname} position to room of kind {roomForm}: {relPlayerPos}";
            Debug.Log(response);

            return true;
        }
    }
}
