using CommandSystem;
using SCPSLBot.Navigation.Policy;
using System;
using UnityEngine;

namespace SCPSLBot.Navigation.Commands
{
    [CommandHandler(typeof(Nav))]
    internal sealed class NavRebuildCommand : ICommand
    {
        public string Command => "rebuild";
        public string[] Aliases => Array.Empty<string>();
        public string Description => "Rebuilds current-map navigation; optionally includes a bounded custom-map region: " + CustomNavigationRegion.Usage;

        public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
        {
            if (!sender.CheckPermission(PlayerPermissions.ServerConfigs, out response))
            {
                return false;
            }

            var navigation = NavigationSystem.Instance;
            if (!navigation.Initialized)
            {
                response = "Navigation system is not initialized.";
                return false;
            }

            if (arguments.Count == 0)
            {
                navigation.Rebuild();
            }
            else if (arguments.Count == 1 && string.Equals(arguments.At(0), "clear", StringComparison.OrdinalIgnoreCase))
            {
                navigation.Rebuild(customBounds: null);
            }
            else
            {
                if (!CustomNavigationRegion.TryParse(arguments, out var region, out response)) return false;
                if (navigation.Config.Backend != NavigationBackend.Runtime)
                {
                    response = "Custom navigation regions require navigation.backend: runtime.";
                    return false;
                }

                navigation.Rebuild(new Bounds(new Vector3(region.X, region.Y, region.Z),
                    new Vector3(region.SizeX, region.SizeY, region.SizeZ)));
            }

            response = $"Navigation rebuild started for map generation {navigation.MapGeneration} with backend {navigation.Config.Backend.ToString().ToLowerInvariant()}. Watch nav status / NAV_BAKED.";
            return true;
        }
    }
}
