using System;
using System.Linq;
using LabApi.Features.Wrappers;

namespace ScpslPluginStarter.Services;

internal sealed class SurfaceHealthDrainService
{
    private readonly WarmupSafezoneConfig _config;
    private readonly SafezoneVolumeService _volumes;
    private readonly OwnedDamageRegistry _ownedDamage;
    private readonly IHintDisplayProvider _hints;
    private readonly WarmupLocalization _localization;

    public SurfaceHealthDrainService(
        WarmupSafezoneConfig config,
        SafezoneVolumeService volumes,
        OwnedDamageRegistry ownedDamage,
        IHintDisplayProvider hints,
        WarmupLocalization localization)
    {
        _config = config;
        _volumes = volumes;
        _ownedDamage = ownedDamage;
        _hints = hints;
        _localization = localization;
    }

    public void Tick()
    {
        if (!_config.Enabled
            || !_config.SurfaceEscapeSafezoneHealthDrainEnabled
            || _config.SurfaceEscapeSafezoneHealthDrainPercentPerSecond <= 0f)
        {
            return;
        }

        float fraction = _config.SurfaceEscapeSafezoneHealthDrainPercentPerSecond / 100f;
        foreach (Player player in Player.List.Where(SafezoneVolumeService.IsEligible))
        {
            // This policy is deliberately surface-only. SCP-914 never enters this predicate.
            if (!_volumes.ContainsSurface(player))
            {
                _hints.Remove(player, "surface-drain");
                continue;
            }

            float damage = Math.Max(1f, player.MaxHealth) * fraction;
            _ownedDamage.ApplyHealthDrain(
                player,
                damage,
                _localization.For(player, "Surface safezone health drain", "地表安全区生命流失"));

            if (_config.SurfaceEscapeSafezoneHealthDrainWarningEnabled)
            {
                string chinese = string.IsNullOrWhiteSpace(_config.SurfaceEscapeSafezoneHealthDrainWarningTextChinese)
                    ? _config.SurfaceEscapeSafezoneHealthDrainWarningText
                    : _config.SurfaceEscapeSafezoneHealthDrainWarningTextChinese;
                string text = _localization.For(player, _config.SurfaceEscapeSafezoneHealthDrainWarningTextEnglish, chinese)
                    .Replace("{percent}", _config.SurfaceEscapeSafezoneHealthDrainPercentPerSecond.ToString("0.##"));
                _hints.ShowPrompt(
                    player,
                    "surface-drain",
                    _config.HintDisplay.SurfaceDrainPromptY,
                    text,
                    1.2f);
            }
        }
    }
}
