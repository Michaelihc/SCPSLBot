using CommandSystem;
using Interactables.Interobjects;
using Interactables.Interobjects.DoorUtils;
using MapGeneration;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SCPSLBot.PlaytestScenarios.Harness;

/// <summary>Isolated setup: make ordinary LCZ crossings require a card, without driving bot actions.</summary>
[CommandHandler(typeof(GameConsoleCommandHandler))]
public sealed class PursuitKeycardFixture : ICommand
{
    private static readonly Dictionary<DoorVariant, DoorPermissionsPolicy> Original = new();
    public string Command => "pursuitkeycards";
    public string[] Aliases => Array.Empty<string>();
    public string Description => "Test-only LCZ keycard fixture: enable / restore.";

    public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
    {
        if (!sender.CheckPermission(PlayerPermissions.FacilityManagement, out response)) return false;
        if (arguments.Count != 1 || (arguments.At(0) != "enable" && arguments.At(0) != "restore"))
        {
            response = "Usage: pursuitkeycards enable|restore";
            return false;
        }
        Restore();
        if (arguments.At(0) == "enable")
        {
            foreach (var door in DoorVariant.AllDoors)
            {
                if (door is not BasicDoor || door is BasicNonInteractableDoor
                    || door.RequiredPermissions.RequiredPermissions != DoorPermissionFlags.None
                    || door.Rooms == null || !door.Rooms.Any(room => room.Zone == FacilityZone.LightContainment)) continue;
                Original.Add(door, door.RequiredPermissions);
                door.RequiredPermissions = new DoorPermissionsPolicy(DoorPermissionFlags.ContainmentLevelOne | DoorPermissionFlags.ScpOverride);
            }
            response = $"PURSUIT_KEYCARDS enabled={Original.Count}";
            return Original.Count > 0;
        }
        response = "PURSUIT_KEYCARDS restored";
        return true;
    }

    internal static void Restore()
    {
        foreach (var pair in Original)
            if (pair.Key != null) pair.Key.RequiredPermissions = pair.Value;
        Original.Clear();
    }
}
