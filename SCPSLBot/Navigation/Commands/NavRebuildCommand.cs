using CommandSystem;
using System;

namespace SCPSLBot.Navigation.Commands
{
    [CommandHandler(typeof(Nav))]
    internal sealed class NavRebuildCommand : ICommand
    {
        public string Command => "rebuild";
        public string[] Aliases => Array.Empty<string>();
        public string Description => "Re-bakes (runtime) or re-loads (authored) navigation for the current map.";

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

            navigation.Rebuild();
            response = $"Navigation rebuild started for map generation {navigation.MapGeneration} with backend {navigation.Config.Backend.ToString().ToLowerInvariant()}. Watch nav status / NAV_BAKED.";
            return true;
        }
    }
}
