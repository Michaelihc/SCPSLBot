#nullable enable

using CommandSystem.Commands.RemoteAdmin.Doors;
using Interactables.Interobjects.DoorUtils;
using MapGeneration;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace SCPSLBot.Warmup.Controls.Panel;

/// <summary>
/// Exposes the same named-door targets and collision-safe destination calculation as native RA
/// <c>doortp</c>. Room-first labels include the door tag so multiple doors in one room stay unique.
/// </summary>
internal sealed class NativeDoorTeleportService
{
    private const int MaximumDestinationCount = 254;

    public IReadOnlyList<WarmupPanelChoice> GetDestinations(Func<FacilityZone, bool>? zoneAllowed = null)
    {
        try
        {
            return DoorNametagExtension.NamedDoors
                .ToArray()
                .Where(entry => TryResolve(entry.Key, out _, out _, out FacilityZone zone)
                    && (zoneAllowed == null || zoneAllowed(zone)))
                .OrderBy(entry => RoomSortKey(entry.Value), StringComparer.Ordinal)
                .ThenBy(entry => entry.Key, StringComparer.Ordinal)
                .Take(MaximumDestinationCount)
                .Select(entry => BuildChoice(entry.Key, entry.Value))
                .ToArray();
        }
        catch
        {
            return Array.Empty<WarmupPanelChoice>();
        }
    }

    public bool TryResolve(
        string destinationId,
        out Vector3 position,
        out string roomLabel,
        out FacilityZone zone)
    {
        position = default;
        roomLabel = string.Empty;
        zone = FacilityZone.None;
        if (string.IsNullOrWhiteSpace(destinationId)
            || !DoorNametagExtension.NamedDoors.TryGetValue(destinationId, out DoorNametagExtension namedDoor)
            || namedDoor == null
            || namedDoor.TargetDoor == null
            || namedDoor.transform == null)
        {
            return false;
        }

        try
        {
            position = DoorTPCommand.EnsurePositionSafety(namedDoor.transform);
            if (!IsFinite(position))
            {
                return false;
            }

            if (!TryGetRoom(namedDoor, position, out RoomIdentifier room))
            {
                return false;
            }

            zone = room.Zone;
            roomLabel = $"{room.Name} [{room.Zone}]";
            return true;
        }
        catch
        {
            position = default;
            roomLabel = string.Empty;
            zone = FacilityZone.None;
            return false;
        }
    }

    private WarmupPanelChoice BuildChoice(string doorName, DoorNametagExtension namedDoor)
    {
        Vector3 position = DoorTPCommand.EnsurePositionSafety(namedDoor.transform);
        string room = TryGetRoom(namedDoor, position, out RoomIdentifier roomIdentifier)
            ? $"{roomIdentifier.Name} [{roomIdentifier.Zone}]"
            : "Unknown room";
        string label = $"{room} — {doorName}";
        return new WarmupPanelChoice(doorName, label, label);
    }

    private static string RoomSortKey(DoorNametagExtension namedDoor)
    {
        if (namedDoor == null || namedDoor.transform == null)
        {
            return string.Empty;
        }

        try
        {
            Vector3 position = DoorTPCommand.EnsurePositionSafety(namedDoor.transform);
            return TryGetRoom(namedDoor, position, out RoomIdentifier room)
                ? $"{room.Zone}:{room.Name}:{room.MainCoords}"
                : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool TryGetRoom(
        DoorNametagExtension namedDoor,
        Vector3 safePosition,
        out RoomIdentifier room) =>
        RoomUtils.TryGetRoom(namedDoor.transform.position, out room)
        || RoomUtils.TryGetRoom(safePosition, out room);

    private static bool IsFinite(Vector3 position) =>
        IsFinite(position.x) && IsFinite(position.y) && IsFinite(position.z);

    private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
}
