#nullable enable

using System.Collections.Generic;

namespace SCPSLBot.Warmup.Controls.Panel;

/// <summary>
/// Presentation-only configuration for the warmup Server-Specific Settings surface. Gameplay
/// allowlists and item/preset policy remain in <see cref="WarmupControlsConfig"/>.
/// </summary>
public sealed class WarmupPanelConfig
{
    public bool Enabled { get; set; } = true;

    public bool ShowArenaPreset { get; set; } = true;

    /// <summary>Deprecated. Debug/authoring controls are RA-only and are never shown to players.</summary>
    public bool ShowAdminTools { get; set; } = false;

    /// <summary>Native feedback broadcast duration. Values outside 1..30 are clamped.</summary>
    public int FeedbackDurationSeconds { get; set; } = 4;

    /// <summary>Minimum interval between any two accepted player-facing SSS mutations.</summary>
    public int MinimumActionIntervalMilliseconds { get; set; } = 1000;

    /// <summary>
    /// Legacy loadout data retained for configuration compatibility. The player SSS loadout control
    /// is intentionally not registered; individual item grants use the explicit Grant button.
    /// </summary>
    public List<WarmupLoadoutConfig> Loadouts { get; set; } = new();

    public static WarmupPanelConfig CreateDefault() => new()
    {
        Loadouts = new List<WarmupLoadoutConfig>
        {
            new()
            {
                Id = "field-rifle-kit",
                EnglishLabel = "Field rifle kit",
                ChineseLabel = "战地步枪套装",
                ItemIds = new List<string> { "GunE11SR", "ArmorCombat", "Medkit" },
                AllowedRoleIds = new List<string>
                {
                    "ClassD",
                    "Scientist",
                    "FacilityGuard",
                    "NtfPrivate",
                    "ChaosRifleman",
                },
                AllowedZoneIds = new List<string>
                {
                    "LightContainment",
                    "HeavyContainment",
                    "Entrance",
                    "Surface",
                },
            },
        },
    };
}

public sealed class WarmupLoadoutConfig
{
    public string Id { get; set; } = string.Empty;

    public string EnglishLabel { get; set; } = string.Empty;

    public string ChineseLabel { get; set; } = string.Empty;

    /// <summary>Exact native ItemType names.</summary>
    public List<string> ItemIds { get; set; } = new();

    /// <summary>Empty means all roles; otherwise exact RoleTypeId names.</summary>
    public List<string> AllowedRoleIds { get; set; } = new();

    /// <summary>Empty means all zones; otherwise exact FacilityZone names.</summary>
    public List<string> AllowedZoneIds { get; set; } = new();
}
