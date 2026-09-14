using HarmonyLib;
using LabApi.Features;
using LabApi.Features.Console;
using LabApi.Loader.Features.Plugins;
using SCPSLBot.AI;
using SCPSLBot.Cleanup;
using SCPSLBot.Navigation;
using SCPSLBot.Navigation.Mesh;
using SCPSLBot.Presentation;
using SCPSLBot.Warmup;
using SCPSLBot.Warmup.Controls.Panel;
using System;
using System.IO;
using System.Reflection;

namespace SCPSLBot
{
    public class LabApiPlugin : Plugin<BotPluginConfig>
    {
        public static LabApiPlugin Instance { get; private set; }

        public override string Name { get; } = "SCPSLBot";
        public override string Description { get; } = "Bot players addon.";
        public override string Author { get; } = "repkins(19)";
        public override Version Version { get; } = new ("1.0.0");
        public override Version RequiredApiVersion { get; } = new(LabApiProperties.CompiledVersion);

        private const string HarmonyId = "SCPSLBot";
        private Harmony harmonyInstance;
        private bool harmonyStarted;
        private bool navigationSystemStarted;
        private bool navigationEditorStarted;
        private bool botManagerStarted;
        private bool cleanupManagerStarted;
        private bool warmupManagerStarted;
        private bool warmupControlsStarted;
        private bool presentationStarted;
        private bool isEnabled;
        private BotPresentationService presentation;
        private WarmupControlsRuntime warmupControls;

        internal BotPresentationService Presentation => presentation;

        public override void Enable()
        {
            if (isEnabled)
            {
                return;
            }

            Instance = this;
            try
            {
                var normalizedSettings = Config.Normalize();
                foreach (string normalizedSetting in normalizedSettings)
                {
                    Logger.Warn($"[SCPSLBot] Normalized config value: {normalizedSetting}");
                }
                if (normalizedSettings.Count > 0)
                {
                    SaveConfig();
                }

                // A stable owner id lets a later load remove patches left by an interrupted enable.
                harmonyInstance = new Harmony(HarmonyId);
                harmonyStarted = true;
                harmonyInstance.UnpatchAll(HarmonyId);
                harmonyInstance.PatchAll(Assembly.GetExecutingAssembly());
                Logger.Info("Patching successful.");

                presentation = new BotPresentationService(
                    Config.HintDisplay,
                    new BotLocalization(Config.Language),
                    HintDisplayProviderFactory.Create(Config.HintDisplay));
                presentationStarted = true;
                presentation.Enable();
                Logger.Info($"[SCPSLBot] Player text provider: {presentation.ProviderName}.");

                NavigationSystem.Instance.BaseDir = Path.Combine(Path.GetDirectoryName(FilePath), Path.GetFileNameWithoutExtension(FilePath));

                if (!Directory.Exists(NavigationSystem.Instance.BaseDir))
                {
                    Directory.CreateDirectory(NavigationSystem.Instance.BaseDir);
                }

                navigationSystemStarted = true;
                NavigationSystem.Instance.Init();
                navigationEditorStarted = true;
                NavigationMeshEditor.Instance.Init();

                botManagerStarted = true;
                BotManager.Instance.Init();
                cleanupManagerStarted = true;
                OverflowCleanupManager.Instance.Init(Config);
                warmupManagerStarted = true;
                WarmupManager.Instance.Init(Config);

                warmupControls = new WarmupControlsRuntime();
                warmupControlsStarted = true;
                warmupControls.Init(Config, presentation);

                isEnabled = true;
                Logger.Info("Enabled plugin.");
            }
            catch (Exception exception)
            {
                Logger.Error($"SCPSLBot enable failed; rolling back initialized components: {exception}");
                ShutdownComponents();
                throw;
            }
        }

        public override void Disable()
        {
            ShutdownComponents();
            Logger.Info("Disabled plugin.");
        }

        private void ShutdownComponents()
        {
            isEnabled = false;

            ShutdownComponent(ref warmupControlsStarted, () => warmupControls?.Terminate(), nameof(WarmupControlsRuntime));
            warmupControls = null;
            ShutdownComponent(ref warmupManagerStarted, WarmupManager.Instance.Terminate, nameof(WarmupManager));
            ShutdownComponent(ref cleanupManagerStarted, OverflowCleanupManager.Instance.Terminate, nameof(OverflowCleanupManager));
            ShutdownComponent(ref botManagerStarted, BotManager.Instance.Terminate, nameof(BotManager));
            ShutdownComponent(ref navigationEditorStarted, NavigationMeshEditor.Instance.Terminate, nameof(NavigationMeshEditor));
            ShutdownComponent(ref navigationSystemStarted, NavigationSystem.Instance.Terminate, nameof(NavigationSystem));
            ShutdownComponent(ref presentationStarted, () => presentation?.Disable(), nameof(BotPresentationService));
            presentation = null;

            if (harmonyStarted)
            {
                harmonyStarted = false;
                try
                {
                    harmonyInstance?.UnpatchAll(HarmonyId);
                    Logger.Info("Unpatching successful.");
                }
                catch (Exception exception)
                {
                    Logger.Error($"Failed to remove SCPSLBot patches during shutdown: {exception}");
                }
            }

            harmonyInstance = null;
            Instance = null;
        }

        private static void ShutdownComponent(ref bool started, Action terminate, string componentName)
        {
            if (!started)
            {
                return;
            }

            started = false;
            try
            {
                terminate();
            }
            catch (Exception exception)
            {
                Logger.Error($"Failed to terminate {componentName}: {exception}");
            }
        }

        public void SaveSettings()
        {
            SaveConfig();
        }
    }
}
