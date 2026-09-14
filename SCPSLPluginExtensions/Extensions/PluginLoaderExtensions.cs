using LabApi.Features.Console;
using LabApi.Features.Wrappers;
using LabApi.Loader;
using LabApi.Loader.Features.Paths;
using LabApi.Loader.Features.Plugins;
using static LabApi.Loader.PluginLoader;

namespace SCPSLPluginExtensions.Extensions
{
    internal static class PluginLoaderExtensions
    {
        public static void DisablePlugin(Plugin plugin)
        {
            try
            {
                plugin.UnregisterCommands();
                plugin.Disable();
                EnabledPlugins.Remove(plugin);
                Logger.Info(string.Format("{0} Successfully disabled {1}", "[LOADER EXTENSIONS]", plugin));
            }
            catch (Exception message)
            {
                Logger.Error(string.Format("{0} Couldn't disable the plugin {1}", "[LOADER EXTENSIONS]", plugin));
                Logger.Error(message);
            }
        }

        public static void LoadPlugins(string assemblyName)
        {
            Logger.Info($"[LOADER EXTENSIONS] Loading {assemblyName} plugins");
            foreach (string pluginPath in Config.PluginPaths)
            {
                string path = pluginPath.Replace("$port", Server.Port.ToString());
                string path2 = Path.Combine(PathManager.Plugins.FullName, path);
                Directory.CreateDirectory(path2);
                if (Directory.Exists(path2))
                {
                    PluginLoader.LoadPlugins(new DirectoryInfo(path2).GetFiles($"{assemblyName}.dll"));
                }
            }

            Logger.Info($"[LOADER EXTENSIONS] Enabling {assemblyName} plugins");
            EnablePlugins(Plugins.Where(p => p.Value.GetName().Name == assemblyName).Select(p => p.Key).OrderBy((plugin) => plugin.Priority));
        }
    }
}
