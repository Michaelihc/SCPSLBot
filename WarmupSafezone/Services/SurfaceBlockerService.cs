using System;
using System.Collections.Generic;
using System.Linq;
using LabApi.Features.Wrappers;
using ScpslPluginStarter.Core;

namespace ScpslPluginStarter.Services;

internal sealed class SurfaceBlockerService
{
    private readonly WarmupSafezoneConfig _config;
    private readonly SafezoneVolumeService _volumes;
    private readonly OwnedDamageRegistry _ownedDamage;
    private readonly IHintDisplayProvider _hints;
    private readonly WarmupLocalization _localization;
    private readonly IMonotonicClock _clock;
    private readonly BlockerPenaltyTracker _tracker = new();
    private readonly HashSet<int> _knownPlayerIds = new();

    public SurfaceBlockerService(
        WarmupSafezoneConfig config,
        SafezoneVolumeService volumes,
        OwnedDamageRegistry ownedDamage,
        IHintDisplayProvider hints,
        WarmupLocalization localization,
        IMonotonicClock clock)
    {
        _config = config;
        _volumes = volumes;
        _ownedDamage = ownedDamage;
        _hints = hints;
        _localization = localization;
        _clock = clock;
    }

    public void Tick()
    {
        if (!_config.Enabled || !_config.SurfaceEscapeBlockerEnabled)
        {
            Reset();
            return;
        }

        long now = _clock.NowMilliseconds;
        HashSet<int> live = new();
        foreach (Player player in Player.List.Where(SafezoneVolumeService.IsEligible))
        {
            live.Add(player.PlayerId);
            bool active = _volumes.ContainsSurfaceBlocker(player);
            BlockerUpdate update = _tracker.Update(
                player.PlayerId,
                active,
                now,
                Math.Max(0, _config.SurfaceEscapeBlockerGraceSeconds) * 1000,
                Math.Max(1, _config.SurfaceEscapeBlockerResetSeconds) * 1000);

            if (update.Tracked)
            {
                _knownPlayerIds.Add(player.PlayerId);
            }

            if (update.Reset)
            {
                _knownPlayerIds.Remove(player.PlayerId);
                _hints.Remove(player, "blocker");
                continue;
            }

            if (update.PunishableDeltaMilliseconds > 0L)
            {
                float drain = CalculateDrain(
                    player.MaxHealth,
                    update.PunishableBeforeMilliseconds,
                    update.PunishableDeltaMilliseconds);
                if (drain > 0f)
                {
                    _ownedDamage.ApplyHealthDrain(
                        player,
                        drain,
                        _localization.For(player, "Safezone blocker health drain", "堵安全区生命流失"));
                }

                float nextDrain = CalculateDrain(
                    player.MaxHealth,
                    update.PunishableBeforeMilliseconds + update.PunishableDeltaMilliseconds,
                    1000L);
                SendActiveWarning(player, nextDrain);
            }
            else if (!active && update.ResetRemainingMilliseconds > 0L)
            {
                SendResetCountdown(player, update.ResetRemainingMilliseconds);
            }
        }

        foreach (int stalePlayerId in _knownPlayerIds.Where(id => !live.Contains(id)).ToArray())
        {
            _tracker.Forget(stalePlayerId);
            _knownPlayerIds.Remove(stalePlayerId);
        }
    }

    public void Forget(Player player)
    {
        _tracker.Forget(player.PlayerId);
        _knownPlayerIds.Remove(player.PlayerId);
        _hints.Remove(player, "blocker");
    }

    public void Reset()
    {
        foreach (int playerId in _knownPlayerIds.ToArray())
        {
            Player? player = Player.Get(playerId);
            if (player != null && !player.IsDestroyed)
            {
                _hints.Remove(player, "blocker");
            }
        }

        _knownPlayerIds.Clear();
        _tracker.Clear();
    }

    private float CalculateDrain(float maxHealth, long punishableStartMilliseconds, long durationMilliseconds) =>
        BlockerDrainCalculator.Calculate(
            maxHealth,
            punishableStartMilliseconds,
            durationMilliseconds,
            _config.SurfaceEscapeBlockerInitialDrainHpPerSecond,
            _config.SurfaceEscapeBlockerDrainMultiplierPerSecond,
            _config.SurfaceEscapeBlockerMaxDrainPercentPerSecond);

    private void SendActiveWarning(Player player, float nextDrain)
    {
        string chineseTitle = string.IsNullOrWhiteSpace(_config.SurfaceEscapeBlockerWarningTextChinese)
            ? _config.SurfaceEscapeBlockerWarningText
            : _config.SurfaceEscapeBlockerWarningTextChinese;
        string title = _localization.For(player, _config.SurfaceEscapeBlockerWarningTextEnglish, chineseTitle);
        string detail = _localization.For(
            player,
            $"Next: {Math.Max(0f, nextDrain):0.##} HP · Reset after {Math.Max(1, _config.SurfaceEscapeBlockerResetSeconds)}s outside.",
            $"下次：{Math.Max(0f, nextDrain):0.##} 生命值 · 连续离开 {Math.Max(1, _config.SurfaceEscapeBlockerResetSeconds)} 秒重置。");
        _hints.ShowPrompt(
            player,
            "blocker",
            _config.HintDisplay.BlockerPromptY,
            $"{title}\n<size=22><color=#ffd166>{detail}</color></size>",
            1.2f);
    }

    private void SendResetCountdown(Player player, long remainingMilliseconds)
    {
        int seconds = Math.Max(1, (int)Math.Ceiling(remainingMilliseconds / 1000d));
        string text = _localization.For(
            player,
            $"<size=24><color=#ffd166>Blocker reset: {seconds}s outside.</color></size>",
            $"<size=24><color=#ffd166>堵塞惩罚：离开 {seconds} 秒后重置。</color></size>");
        _hints.ShowPrompt(player, "blocker", _config.HintDisplay.BlockerPromptY, text, 1.2f);
    }
}
