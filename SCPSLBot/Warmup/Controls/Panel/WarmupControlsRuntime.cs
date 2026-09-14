#nullable enable

using System;
using LabApi.Events.Handlers;
using LabApi.Features.Console;
using SCPSLBot.Presentation;

namespace SCPSLBot.Warmup.Controls.Panel;

/// <summary>
/// Owns the live policy facades and personalized SSS blocks as one reload-safe lifecycle unit.
/// Gameplay decisions remain in the policy services; this class only composes and wakes them.
/// </summary>
internal sealed class WarmupControlsRuntime
{
    private WarmupSssController? controller;
    private LabApiItemGrantService? items;
    private string roundId = string.Empty;
    private int roundGeneration;
    private bool initialized;

    public void Init(BotPluginConfig pluginConfig, BotPresentationService presentation)
    {
        if (initialized)
        {
            return;
        }

        if (pluginConfig == null)
        {
            throw new ArgumentNullException(nameof(pluginConfig));
        }

        if (presentation == null)
        {
            throw new ArgumentNullException(nameof(presentation));
        }

        if (!pluginConfig.Panel.Enabled)
        {
            initialized = true;
            Logger.Info("[SCPSLBot] Warmup SSS controls are disabled by config.");
            return;
        }

        AdvanceRound();
        ServerKeybinds.KeybindRegistry.Language = pluginConfig.Language ?? string.Empty;
        var guard = new PerUserRequestGuard();
        var actions = new LabApiWarmupPanelActions(
            pluginConfig.Controls,
            pluginConfig.Panel,
            presentation,
            guard,
            currentRoundId: () => roundId);
        var roles = new LabApiRoleControlService(pluginConfig.Controls.Roles, actions, guard);

        if (!LabApiItemGrantService.TryCreate(
                pluginConfig.Controls.Items,
                actions,
                out items,
                out var errors,
                sharedRequestGuard: guard))
        {
            foreach (string error in errors)
            {
                Logger.Error($"[SCPSLBot] Warmup item catalog disabled: {error}");
            }

            items = null;
        }

        items?.BeginRound(roundId);
        controller = new WarmupSssController(
            pluginConfig.Controls,
            pluginConfig.Panel,
            roles,
            items,
            actions);

        try
        {
            controller.Enable();
            WarmupManager.Instance.ModeChanged += OnModeChanged;
            ServerEvents.RoundRestarted += OnRoundRestarted;
            initialized = true;
        }
        catch
        {
            controller.Disable();
            controller = null;
            items = null;
            throw;
        }
    }

    public void Terminate()
    {
        if (!initialized)
        {
            return;
        }

        initialized = false;
        ServerEvents.RoundRestarted -= OnRoundRestarted;
        WarmupManager.Instance.ModeChanged -= OnModeChanged;
        controller?.Disable();
        controller = null;
        items = null;
        roundId = string.Empty;
    }

    private void OnModeChanged(WarmupMode _)
    {
        try
        {
            controller?.NotifyArenaPresetChanged("warmup-mode-changed");
        }
        catch (Exception exception)
        {
            Logger.Warn($"[SCPSLBot] Warmup SSS mode refresh failed: {exception.GetBaseException().Message}");
        }
    }

    private void OnRoundRestarted()
    {
        AdvanceRound();
        items?.BeginRound(roundId);
        try
        {
            controller?.NotifyArenaPresetChanged("round-restarted");
        }
        catch (Exception exception)
        {
            Logger.Warn($"[SCPSLBot] Warmup SSS round refresh failed: {exception.GetBaseException().Message}");
        }
    }

    private void AdvanceRound()
    {
        roundGeneration++;
        string nativeRound;
        try
        {
            nativeRound = RoundRestarting.RoundRestart.UptimeRounds.ToString();
        }
        catch
        {
            nativeRound = "unknown";
        }

        roundId = $"{nativeRound}:{roundGeneration}";
    }
}
