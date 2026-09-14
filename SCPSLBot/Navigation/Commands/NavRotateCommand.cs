using CommandSystem;
using MapGeneration;
using RemoteAdmin;
using SCPSLBot.Navigation.Mesh;
using System;
using System.Numerics;
using UnityEngine;

namespace SCPSLBot.Navigation.Commands
{
    [CommandHandler(typeof(Nav))]
    internal class NavRotateCommand : ICommand
    {
        public string Command { get; } = "mesh_rotate";

        public string[] Aliases { get; } = new string[] { };

        public string Description { get; } = "Rotates mesh.";

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

            var playerPosition = playerCommandSender.ReferenceHub.transform.position;
            RoomUtils.TryGetRoom(playerPosition, out var room);
            var roomForm = NavigationMesh.GetForm(room.gameObject);
            var mesh = NavigationMesh.GetMesh(roomForm);

            var rotation = Quaternion.AngleAxis(90, Vector3.up);
            foreach (var vertex in mesh.Vertices)
            {
                var rotatedPosition = rotation * vertex.Position;

                mesh.MoveVertex(vertex, rotatedPosition);
            }

            response = $"Mesh of form {roomForm} rotated";
            return true;
        }
    }
}
