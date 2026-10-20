using HarmonyLib;
using LabApi.Features;
using LabApi.Features.Console;
using LabApi.Loader.Features.Plugins;
using LabApi.Loader.Features.Plugins.Enums;
using SCPSLBot.AI;
using SCPSLBot.Diagnostics;
using SCPSLBot.Api;
using SCPSLBot.Health;
using SCPSLBot.Navigation;
using SCPSLBot.Navigation.Mesh;
using SCPSLBot.Presentation;
using System;
using System.IO;
using System.Reflection;

namespace SCPSLBot
{
    public class LabApiPlugin : Plugin<BotRuntimeConfig>
    {
        public static LabApiPlugin Instance { get; private set; }

        public override string Name { get; } = "SCPSLBot";
        public override string Description { get; } = "Bot players addon.";
        public override string Author { get; } = "repkins(19)";
        public override Version Version { get; } = new ("1.0.0");
        public override Version RequiredApiVersion { get; } = new(LabApiProperties.CompiledVersion);
        public override LoadPriority Priority => LoadPriority.High;

        private const string HarmonyId = "SCPSLBot";
        private Harmony harmonyInstance;
        private bool harmonyStarted;
        private bool navigationSystemStarted;
        private bool navigationEditorStarted;
        private bool botManagerStarted;
        private bool normalRoundBotsStarted;
        private bool roundTraceStarted;
        private readonly NormalRoundBotSpawner normalRoundBots = new();
        private bool presentationStarted;
        private bool isEnabled;
        private BotPresentationService presentation;
        internal NetworkRegistryHealthService NetworkHealth { get; private set; }

        internal BotPresentationService Presentation => presentation;

        public override void Enable()
        {
            if (isEnabled)
            {
                return;
            }

            Instance = this;
            // Published before anything that can fail, so companions never wake on a failed enable.
            BotsOnlyMode.IsEnabled = true;
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

                NetworkHealth = new NetworkRegistryHealthService(Config);
                NetworkHealth.Enable();

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
                NavigationSystem.Instance.Init(Config.Navigation);
                navigationEditorStarted = true;
                NavigationMeshEditor.Instance.Init();

                botManagerStarted = true;
                BotManager.Instance.Init();
                normalRoundBotsStarted = true;
                normalRoundBots.Init(Config);
                roundTraceStarted = true;
                RoundTraceRecorder.Instance.Init(Config.RoundTrace);

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
            try { BotServerPolicy.ManagementStopping?.Invoke(); }
            catch (Exception exception) { Logger.Error($"Management shutdown failed: {exception}"); }
            BotServerPolicy.ManagementStopping = null;
            BotsOnlyMode.IsEnabled = true;
            NetworkHealth?.Disable();
            NetworkHealth = null;

            ShutdownComponent(ref roundTraceStarted, RoundTraceRecorder.Instance.Terminate, nameof(RoundTraceRecorder));
            ShutdownComponent(ref normalRoundBotsStarted, normalRoundBots.Terminate, nameof(NormalRoundBotSpawner));
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
