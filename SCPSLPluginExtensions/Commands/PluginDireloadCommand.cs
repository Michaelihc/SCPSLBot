using CommandSystem;
using LabApi.Loader;
using SCPSLPluginExtensions.Extensions;

namespace SCPSLPluginExtensions.Commands
{
    [CommandHandler(typeof(GameConsoleCommandHandler))]
    internal class PluginDireloadCommand : ICommand
    {
        public string Command => "plugin_direload";

        public string[] Aliases => new string[] { };

        public string Description => "Disables (di) specified running plugins assembly, removes (re) from plugin manager then loads (load) new plugins assembly.";

        public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
        {
            if (arguments.Count < 1)
            {
                response = "No plugin assembly identifier.";
                return false;
            }

            var pluginIdentifier = arguments[0];

            var pluginsOfIdentifier = PluginLoader.Plugins.Where(p => p.Value.GetName().Name == pluginIdentifier);

            foreach (var plugin in pluginsOfIdentifier.Select(p => p.Key).ToArray())
            {
                if (PluginLoader.EnabledPlugins.Contains(plugin))
                {
                    PluginLoaderExtensions.DisablePlugin(plugin);
                }

                PluginLoader.Plugins.Remove(plugin);
            }

            PluginLoaderExtensions.LoadPlugins(pluginIdentifier);

            response = "Done.";
            return true;
        }
    }
}
