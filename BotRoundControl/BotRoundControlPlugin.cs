#nullable enable
using System;
using LabApi.Events.Handlers;
using LabApi.Features;
using LabApi.Features.Console;
using LabApi.Loader.Features.Plugins;
using LabApi.Loader.Features.Plugins.Enums;
using SCPSLBot;
using SCPSLBot.Api;
using SCPSLBot.Presentation;
using SCPSLBot.Cleanup;
using SCPSLBot.Health;
using SCPSLBot.Warmup;
using SCPSLBot.Warmup.Controls.Panel;
using HarmonyLib;
using DuelArena;

namespace BotRoundControl;

public sealed class BotRoundControlPlugin : Plugin<BotPluginConfig>
{
    public static BotRoundControlPlugin? Instance { get; private set; }
    internal static DuelRuntime? Runtime { get; private set; }
    internal BotPresentationService? Presentation { get; private set; }
    private WarmupControlsRuntime? controls;
    private HumanPlayerCountService? humanCount;
    private Harmony? harmony;
    public override string Name => "BotRoundControl";
    public override string Description => "Bot-server round management, warmup arenas, player controls, cleanup and duels.";
    public override string Author => "SCPSLBot contributors";
    public override Version Version { get; } = new(1, 0, 0);
    public override Version RequiredApiVersion => new(LabApiProperties.CompiledVersion);
    public override LoadPriority Priority => LoadPriority.Medium;
    public override void Enable()
    {
        if (Config == null || Config.BotsOnly) return;
        if (LabApiPlugin.Instance?.Presentation == null)
            throw new InvalidOperationException("BotRoundControl requires SCPSLBot to be enabled first.");
        Instance = this;
        BotServerPolicy.ManagementStopping = Disable;
        try
        {
            foreach (string change in Config.Normalize()) Logger.Warn("[BotRoundControl] " + change);
            SaveConfig();
            Presentation = new BotPresentationService(Config.HintDisplay, new BotLocalization(Config.Language),
                new HsmHintDisplayProvider(Config.HintDisplay, "BotRoundControl"));
            Presentation.Enable();
            BotsOnlyMode.IsEnabled = false;
            harmony = new Harmony("BotRoundControl");
            harmony.PatchAll(typeof(BotRoundControlPlugin).Assembly);
            humanCount = new HumanPlayerCountService();
            humanCount.Enable();
            OverflowCleanupManager.Instance.Init(Config);
            WarmupManager.Instance.Init(Config);
            BotServerPolicy.StandardWarmup = () => WarmupManager.Instance.IsStandardWarmup;
            WarmupManager.Instance.ModeChanged += OnModeChanged;
            BotServerPolicy.NotifyModeChanged();
            if (Config.Duel.Enabled)
            {
                Runtime = new DuelRuntime(Config.Duel)
                {
                    ModeActive = () => WarmupManager.Instance.IsStandardWarmup,
                    ReturnToSurface = player => WarmupManager.Instance.ReturnFromExternalArena(player),
                };
                Runtime.Enable();
            }
            BotServerPolicy.CanFight = (left, right) => WarmupManager.Instance.CanHubsFightInWarmup(left, right);
            BotServerPolicy.PopulationStatus = SCPSLBot.AI.Commands.BotServerStatusCommand.BuildDiagnostics;
            controls = new WarmupControlsRuntime();
            controls.Init(Config, Presentation);
            Logger.Info("[BotRoundControl] Management enabled; duel service ready.");
        }
        catch { Disable(); throw; }
    }
    public override void Disable()
    {
        Runtime?.Disable();
        Runtime = null;
        controls?.Terminate();
        controls = null;
        BotServerPolicy.CanFight = null;
        BotServerPolicy.PopulationStatus = null;
        BotServerPolicy.ManagementStopping = null;
        WarmupManager.Instance.ModeChanged -= OnModeChanged;
        WarmupManager.Instance.Terminate();
        BotServerPolicy.StandardWarmup = null;
        BotServerPolicy.NotifyModeChanged();
        OverflowCleanupManager.Instance.Terminate();
        humanCount?.Disable();
        humanCount = null;
        Presentation?.Disable();
        Presentation = null;
        harmony?.UnpatchAll("BotRoundControl");
        harmony = null;
        BotsOnlyMode.IsEnabled = true;
        Instance = null;
    }
    public void SaveManagementSettings() => SaveConfig();
    private void OnModeChanged(WarmupMode mode) => BotServerPolicy.NotifyModeChanged();
}
