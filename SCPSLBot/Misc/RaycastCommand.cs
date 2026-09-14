using CommandSystem;
using CommandSystem.Commands.RemoteAdmin.Stripdown;
using MapGeneration;
using Mirror;
using PlayerRoles;
using RemoteAdmin;
using System;
using UnityEngine;

namespace SCPSLBot.Misc
{
    [CommandHandler(typeof(ClientCommandHandler))]
    internal class RaycastCommand : ICommand
    {
        public string Command => "raycast";

        public string[] Aliases => new string[] { };

        public string Description => "Casts ray among camera direction from camera and prints hit results.";

        public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
        {
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

            var playerCameraReference = playerCommandSender.ReferenceHub.PlayerCameraReference;
            if (!Physics.Raycast(playerCameraReference.position, playerCameraReference.forward, out var hit, 20f))
            {
                response = "Raycast produced no any hit within max distance.";
                return false;
            }

            var collider = hit.collider;
            var layerName = LayerMask.LayerToName(collider.gameObject.layer);
            var tag = collider.gameObject.tag;

            response = $"Got hit with collider of {collider.gameObject} and layer {layerName} and tag {tag}: ";

            var transform = collider.transform;
            do
            {
                response += $"{transform.gameObject.name}, ";
                transform = transform.parent;
            }
            while (transform);

            Debug.Log(response);

            return true;
        }
    }
}
