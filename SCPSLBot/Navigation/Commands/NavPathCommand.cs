using CommandSystem;
using SCPSLBot.Navigation.Policy;
using System;
using System.Globalization;
using UnityEngine;
using UnityEngine.AI;

namespace SCPSLBot.Navigation.Commands
{
    [CommandHandler(typeof(Nav))]
    internal sealed class NavPathCommand : ICommand
    {
        public string Command => "path";
        public string[] Aliases => Array.Empty<string>();
        public string Description => "Runtime navmesh path query: nav path <from x y z|RoomName> <to x y z|RoomName> [perms <hex DoorPermissionFlags>|all].";

        public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
        {
            if (!sender.CheckPermission(PlayerPermissions.GameplayData, out response))
            {
                return false;
            }

            var navigation = NavigationSystem.Instance;
            if (navigation.Backend?.Name != "runtime" || !navigation.Runtime.IsBuilt)
            {
                response = $"nav path needs the runtime backend (active={navigation.Backend?.Name ?? "none"}, built={navigation.Runtime.IsBuilt}).";
                return false;
            }

            if (!TryParseEndpoint(arguments, 0, out var from, out var consumed, out response)
                || !TryParseEndpoint(arguments, consumed, out var to, out var consumedTo, out response))
            {
                return false;
            }

            var index = consumedTo;
            var areaMask = navigation.Runtime.Areas.BuildAreaMask(0);
            var permissionsLabel = "none";
            if (arguments.Count > index && string.Equals(arguments.At(index), "perms", StringComparison.OrdinalIgnoreCase) && arguments.Count > index + 1)
            {
                var value = arguments.At(index + 1);
                if (string.Equals(value, "all", StringComparison.OrdinalIgnoreCase))
                {
                    areaMask = NavMesh.AllAreas;
                    permissionsLabel = "all";
                }
                else if (ushort.TryParse(value.Replace("0x", string.Empty), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var permissions))
                {
                    areaMask = navigation.Runtime.Areas.BuildAreaMask(permissions);
                    permissionsLabel = $"0x{permissions:X}";
                }
                else
                {
                    response = $"Unparseable permissions '{value}'. Use hex DoorPermissionFlags (e.g. 20 for ContainmentLevelTwo) or all.";
                    return false;
                }
            }

            if (!NavMesh.SamplePosition(from, out var fromHit, 4f, NavMesh.AllAreas))
            {
                response = $"from={NavCommandParsing.Format(from)} is not within 4m of the navmesh.";
                return false;
            }

            if (!NavMesh.SamplePosition(to, out var toHit, 4f, NavMesh.AllAreas))
            {
                response = $"to={NavCommandParsing.Format(to)} is not within 4m of the navmesh.";
                return false;
            }

            var path = new NavMeshPath();
            var started = System.Diagnostics.Stopwatch.StartNew();
            var computed = NavMesh.CalculatePath(fromHit.position, toHit.position, areaMask | fromHit.mask, path);
            started.Stop();
            var length = 0f;
            for (var corner = 1; corner < path.corners.Length; corner++)
            {
                length += Vector3.Distance(path.corners[corner - 1], path.corners[corner]);
            }

            var status = computed ? path.status.ToString() : "NotComputed";
            response = $"status={status} perms={permissionsLabel} corners={path.corners.Length} length={length:F1} query_ms={started.Elapsed.TotalMilliseconds:F2} from={NavCommandParsing.Format(fromHit.position)} to={NavCommandParsing.Format(toHit.position)} door_classes={navigation.Runtime.Areas.ClassCount}";
            return true;
        }

        private static bool TryParseEndpoint(ArraySegment<string> arguments, int offset, out Vector3 point, out int next, out string error)
        {
            point = default;
            next = offset;
            error = string.Empty;
            if (arguments.Count >= offset + 3
                && float.TryParse(arguments.At(offset), NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                && float.TryParse(arguments.At(offset + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
                && float.TryParse(arguments.At(offset + 2), NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
            {
                point = new Vector3(x, y, z);
                next = offset + 3;
                return true;
            }

            if (arguments.Count >= offset + 1 && NavCommandParsing.TryResolveRoom(arguments.At(offset), out point, out error))
            {
                next = offset + 1;
                return true;
            }

            error = string.IsNullOrEmpty(error) ? "Usage: nav path <from x y z|RoomName> <to x y z|RoomName> [perms <hex>|all]" : error;
            return false;
        }
    }
}
