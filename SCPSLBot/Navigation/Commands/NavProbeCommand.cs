using CommandSystem;
using MapGeneration;
using RemoteAdmin;
using System;
using System.Globalization;
using UnityEngine;
using UnityEngine.AI;

namespace SCPSLBot.Navigation.Commands
{
    [CommandHandler(typeof(Nav))]
    internal sealed class NavProbeCommand : ICommand
    {
        public string Command => "probe";
        public string[] Aliases => Array.Empty<string>();
        public string Description => "Samples the navigation surface at a point: nav probe [x y z] (defaults to your position).";

        public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
        {
            if (!sender.CheckPermission(PlayerPermissions.GameplayData, out response))
            {
                return false;
            }

            if (!NavCommandParsing.TryResolvePoint(arguments, 0, sender, out var point, out response))
            {
                return false;
            }

            var navigation = NavigationSystem.Instance;
            var backend = navigation.Backend;
            var room = RoomUtils.TryGetRoom(point, out var roomWithin) && roomWithin != null ? roomWithin.gameObject.name : "none";
            var onMesh = backend != null && backend.IsOnMesh(point, 0.5f);
            var nearest = backend != null && backend.TryGetNearestPoint(point, 30f, out var nearestPoint, out var distance)
                ? $"{NavCommandParsing.Format(nearestPoint)} at {distance:F2}m"
                : "none";
            var area = "n/a";
            if (backend?.Name == "runtime" && NavMesh.SamplePosition(point, out var hit, 2f, NavMesh.AllAreas))
            {
                var areaIndex = MaskToArea(hit.mask);
                area = navigation.Runtime.Areas.TryGetClass(areaIndex, out var doorClass)
                    ? $"{areaIndex} (door class {doorClass})"
                    : areaIndex.ToString(CultureInfo.InvariantCulture);
            }

            response = $"backend={backend?.Name ?? "none"} point={NavCommandParsing.Format(point)} room={room} on_mesh={onMesh} nearest={nearest} area={area} room_navigable={(roomWithin != null && backend != null && backend.RoomHasNavigation(roomWithin))}";
            return true;
        }

        private static int MaskToArea(int mask)
        {
            for (var index = 0; index < 32; index++)
            {
                if ((mask & (1 << index)) != 0)
                {
                    return index;
                }
            }

            return -1;
        }
    }

    internal static class NavCommandParsing
    {
        public static bool TryResolvePoint(ArraySegment<string> arguments, int offset, ICommandSender sender, out Vector3 point, out string error)
        {
            point = default;
            error = string.Empty;
            if (arguments.Count >= offset + 3
                && float.TryParse(arguments.At(offset), NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                && float.TryParse(arguments.At(offset + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
                && float.TryParse(arguments.At(offset + 2), NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
            {
                point = new Vector3(x, y, z);
                return true;
            }

            if (arguments.Count >= offset + 1 && TryResolveRoom(arguments.At(offset), out point, out error))
            {
                return true;
            }

            if (sender is PlayerCommandSender playerSender && playerSender.ReferenceHub != null)
            {
                point = playerSender.ReferenceHub.transform.position;
                return true;
            }

            error = string.IsNullOrEmpty(error) ? "Provide x y z or a RoomName, or run in-game." : error;
            return false;
        }

        public static bool TryResolveRoom(string name, out Vector3 point, out string error)
        {
            point = default;
            error = string.Empty;
            if (!Enum.TryParse(name, true, out RoomName roomName) || roomName == RoomName.Unnamed)
            {
                error = $"Unknown room '{name}'.";
                return false;
            }

            foreach (var room in RoomIdentifier.AllRoomIdentifiers)
            {
                if (room != null && room.Name == roomName)
                {
                    var backend = NavigationSystem.Instance.Backend;
                    var samples = backend?.GetRoomSamples(room);
                    if (samples != null && samples.Count > 0)
                    {
                        // The sample nearest the room pivot is the most representative anchor.
                        var best = samples[0];
                        var bestDistance = float.PositiveInfinity;
                        foreach (var sample in samples)
                        {
                            var distance = Vector3.Distance(sample, room.transform.position);
                            if (distance < bestDistance)
                            {
                                bestDistance = distance;
                                best = sample;
                            }
                        }

                        point = best;
                        return true;
                    }

                    point = room.transform.position + Vector3.up * 0.5f;
                    return true;
                }
            }

            error = $"Room '{name}' is not present on this map.";
            return false;
        }

        public static string Format(Vector3 value) => $"({value.x:F2},{value.y:F2},{value.z:F2})";
    }
}
