using CommandSystem;
using System;

namespace SCPSLBot.Navigation.Commands
{
    [CommandHandler(typeof(Nav))]
    internal sealed class NavStatusCommand : ICommand
    {
        public string Command => "status";
        public string[] Aliases => Array.Empty<string>();
        public string Description => "Navigation backend readiness and runtime navmesh diagnostics.";

        public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
        {
            if (!sender.CheckPermission(PlayerPermissions.GameplayData, out response))
            {
                return false;
            }

            var navigation = NavigationSystem.Instance;
            response = $"active_backend={navigation.Backend?.Name ?? "none"} configured_backend={navigation.Config.Backend.ToString().ToLowerInvariant()} "
                       + $"ready={navigation.IsReadyForCurrentMap} map_generation={navigation.MapGeneration} ready_generation={navigation.ReadyGeneration} "
                       + $"bake_failures={navigation.RuntimeBakeFailures} load_error={(string.IsNullOrEmpty(navigation.LastLoadError) ? "none" : navigation.LastLoadError)} "
                       + $"keycard_routing={navigation.Config.KeycardAreaRouting} reconcile_interval={navigation.Config.ReconcileIntervalSeconds} voxel={navigation.Config.VoxelSize} | "
                       + navigation.Runtime.Describe();
            return true;
        }
    }
}
