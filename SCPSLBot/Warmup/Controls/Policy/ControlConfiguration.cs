#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace SCPSLBot.Warmup.Controls;

/// <summary>
/// Standalone configuration model intended to be composed into BotPluginConfig later.
/// It deliberately has no loader or global singleton.
/// </summary>
public sealed class WarmupControlsConfig
{
    /// <summary>"" follows the client, "cn" forces Chinese, and "en" forces English.</summary>
    public string Language { get; set; } = string.Empty;

    public RoleControlsConfig Roles { get; set; } = RoleControlsConfig.CreateDefault();

    public List<ItemCatalogEntryConfig> Items { get; set; } = new();

    public List<ArenaPresetConfig> Presets { get; set; } = new();

    public static WarmupControlsConfig CreateDefault()
    {
        List<ItemCatalogEntryConfig> items = WarmupPlayerCatalogDefaults.CreateItems();

        return new WarmupControlsConfig
        {
            Language = string.Empty,
            Roles = RoleControlsConfig.CreateDefault(),
            Items = items,
            Presets = WarmupPlayerCatalogDefaults.CreateArenaPresets(items),
        };
    }

    /// <summary>
    /// Migrates the short-lived curated-panel defaults back to the classic player contract. Existing
    /// per-item cooldowns are retained, but every safe native role/item becomes visible and the three
    /// physical arena presets are restored.
    /// </summary>
    public IReadOnlyList<string> RestoreClassicPlayerOptions()
    {
        var changes = new List<string>();
        Roles ??= RoleControlsConfig.CreateDefault();

        string[] safeRoles = WarmupPlayerCatalogDefaults.RegularRoles;
        string[] normalizedRoles = (Roles.AllowedRegularRoleIds ?? new List<string>())
            .Where(WarmupPlayerCatalogDefaults.IsSafeRegularRole)
            .Concat(safeRoles)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (!(Roles.AllowedRegularRoleIds ?? new List<string>()).SequenceEqual(normalizedRoles, StringComparer.OrdinalIgnoreCase))
        {
            Roles.AllowedRegularRoleIds = normalizedRoles.ToList();
            changes.Add("restored the full safe player role list");
        }

        List<ItemCatalogEntryConfig> defaults = WarmupPlayerCatalogDefaults.CreateItems();
        var configuredByItem = (Items ?? new List<ItemCatalogEntryConfig>())
            .Where(entry => entry != null && !string.IsNullOrWhiteSpace(entry.ItemId))
            .GroupBy(entry => entry.ItemId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var normalizedItems = new List<ItemCatalogEntryConfig>(defaults.Count);
        foreach (ItemCatalogEntryConfig fallback in defaults)
        {
            ItemCatalogEntryConfig entry = configuredByItem.TryGetValue(fallback.ItemId, out ItemCatalogEntryConfig? existing)
                ? existing ?? fallback
                : fallback;
            entry.AllowedRoleIds = new List<string>(safeRoles);
            entry.AllowedZoneIds = new List<string>(WarmupPlayerCatalogDefaults.RequestZones);
            normalizedItems.Add(entry);
        }

        if ((Items ?? new List<ItemCatalogEntryConfig>()).Count != normalizedItems.Count
            || normalizedItems.Any(item => !(Items ?? new List<ItemCatalogEntryConfig>()).Any(
                existing => existing != null && string.Equals(existing.ItemId, item.ItemId, StringComparison.OrdinalIgnoreCase))))
        {
            changes.Add($"restored the full safe player item list ({normalizedItems.Count} entries)");
        }
        Items = normalizedItems;

        string[] classicPresetIds = { "surface", "pvpve", "lcz" };
        if (Presets == null
            || Presets.Count != classicPresetIds.Length
            || !classicPresetIds.All(id => Presets.Any(preset => preset != null && string.Equals(preset.Id, id, StringComparison.OrdinalIgnoreCase))))
        {
            Presets = WarmupPlayerCatalogDefaults.CreateArenaPresets(Items);
            changes.Add("restored the classic Surface PvE, HCZ/EZ PvPvE, and LCZ SCP arena presets");
        }

        Dictionary<string, ArenaPresetConfig> nativeDefaults = WarmupPlayerCatalogDefaults
            .CreateArenaPresets(Items)
            .ToDictionary(preset => preset.Id, StringComparer.OrdinalIgnoreCase);
        foreach (ArenaPresetConfig preset in Presets.Where(preset => preset != null))
        {
            if (!nativeDefaults.TryGetValue(preset.Id, out ArenaPresetConfig? expected)
                || (string.Equals(preset.SpawnAnchorRoleId, expected.SpawnAnchorRoleId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(preset.ScpSpawnAnchorRoleId, expected.ScpSpawnAnchorRoleId, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            preset.SpawnAnchorRoleId = expected.SpawnAnchorRoleId;
            preset.ScpSpawnAnchorRoleId = expected.ScpSpawnAnchorRoleId;
            changes.Add($"restored native spawn anchors for arena '{preset.Id}'");
        }

        return changes;
    }
}

public sealed class RoleControlsConfig
{
    /// <summary>Legacy compatibility field. Regular role selection is permissive and ignores this list.</summary>
    public List<string> AllowedRegularRoleIds { get; set; } = new();

    /// <summary>Legacy compatibility field. Native role selection ignores this list.</summary>
    public List<string> AllowedAdminForceRoleIds { get; set; } = new();

    /// <summary>
    /// Optional explicit role-to-anchor-role mappings. This is useful for Tutorial, whose native
    /// role template may not expose its own map spawnpoint. It never substitutes the assigned role.
    /// </summary>
    public Dictionary<string, string> SpawnAnchorRoleOverrides { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public static RoleControlsConfig CreateDefault() => new()
    {
        AllowedRegularRoleIds = new List<string>(WarmupPlayerCatalogDefaults.RegularRoles),
        AllowedAdminForceRoleIds = new List<string>(WarmupPlayerCatalogDefaults.RegularRoles.Concat(new[] { "Tutorial" })),
        SpawnAnchorRoleOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Tutorial"] = "ClassD",
        },
    };
}

public static class WarmupPlayerCatalogDefaults
{
    public static readonly string[] RegularRoles =
    {
        "Scp173", "ClassD", "Scp106", "NtfSpecialist", "Scp049", "Scientist",
        "Scp079", "ChaosConscript", "Scp096", "Scp0492", "NtfSergeant", "NtfCaptain",
        "NtfPrivate", "FacilityGuard", "Scp939", "ChaosRifleman", "ChaosMarauder",
        "ChaosRepressor", "Scp3114",
    };

    public static readonly string[] RequestZones =
    {
        "LightContainment", "HeavyContainment", "Entrance", "Surface",
    };

    private static readonly string[] NativeItems =
    {
        "KeycardJanitor", "KeycardScientist", "KeycardResearchCoordinator", "KeycardZoneManager",
        "KeycardGuard", "KeycardMTFPrivate", "KeycardContainmentEngineer", "KeycardMTFOperative",
        "KeycardMTFCaptain", "KeycardFacilityManager", "KeycardChaosInsurgency", "KeycardO5",
        "Radio", "GunCOM15", "Medkit", "Flashlight", "MicroHID", "SCP500", "SCP207",
        "Ammo12gauge", "GunE11SR", "GunCrossvec", "Ammo556x45", "GunFSP9", "GunLogicer",
        "GrenadeHE", "GrenadeFlash", "Ammo44cal", "Ammo762x39", "Ammo9x19", "GunCOM18",
        "SCP018", "SCP268", "Adrenaline", "Painkillers", "Coin", "ArmorLight", "ArmorCombat",
        "ArmorHeavy", "GunRevolver", "GunAK", "GunShotgun", "SCP330", "SCP2176", "SCP244a",
        "SCP244b", "SCP1853", "ParticleDisruptor", "GunCom45", "SCP1576", "Jailbird",
        "AntiSCP207", "GunFRMG0", "GunA7", "Lantern", "SCP1344", "Snowball", "Coal",
        "SpecialCoal", "SCP1507Tape", "SurfaceAccessPass", "GunSCP127", "KeycardCustomTaskForce",
        "KeycardCustomSite02", "KeycardCustomManagement", "KeycardCustomMetalCase", "MarshmallowItem",
        "SCP1509", "Scp021J",
    };

    public static bool IsSafeRegularRole(string roleId) =>
        RegularRoles.Contains(roleId ?? string.Empty, StringComparer.OrdinalIgnoreCase);

    public static List<ItemCatalogEntryConfig> CreateItems()
    {
        var entries = new List<ItemCatalogEntryConfig>(NativeItems.Length);
        foreach (string itemId in NativeItems)
        {
            bool highImpact = itemId is "GrenadeHE" or "GrenadeFlash" or "MicroHID" or "ParticleDisruptor" or "Jailbird";
            string stableId = itemId switch
            {
                "Medkit" => "medical.medkit",
                "GrenadeHE" => "high-impact.grenade-he",
                "MicroHID" => "high-impact.micro-hid",
                _ => "native." + itemId.ToLowerInvariant(),
            };
            entries.Add(new ItemCatalogEntryConfig
            {
                Id = stableId,
                EnglishLabel = itemId,
                ChineseLabel = ChineseItemLabel(itemId),
                ItemId = itemId,
                CooldownSeconds = highImpact ? 60 : 0,
                SharedCooldownGroup = highImpact ? "high-impact" : string.Empty,
                SharedCooldownSeconds = highImpact ? 60 : 0,
                PerLifeLimit = highImpact ? 1 : 0,
                PerRoundLimit = highImpact ? 2 : 0,
                AllowedRoleIds = new List<string>(RegularRoles),
                AllowedZoneIds = new List<string>(RequestZones),
            });
        }
        return entries;
    }

    public static List<ArenaPresetConfig> CreateArenaPresets(IEnumerable<ItemCatalogEntryConfig> items)
    {
        List<string> itemIds = (items ?? Array.Empty<ItemCatalogEntryConfig>())
            .Where(item => item != null && !string.IsNullOrWhiteSpace(item.Id))
            .Select(item => item.Id)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return new List<ArenaPresetConfig>
        {
            Arena("surface", "Surface PvE", "地表 PvE", "NtfPrivate", itemIds),
            Arena("pvpve", "HCZ / EZ PvPvE", "重收 / 入口 PvPvE 混战", "Scp939", itemIds),
            Arena("lcz", "LCZ SCP arena", "轻收 SCP 竞技场", "ClassD", itemIds),
        };
    }

    private static ArenaPresetConfig Arena(
        string id,
        string english,
        string chinese,
        string nativeSpawnAnchorRoleId,
        List<string> itemIds) => new()
    {
        Id = id,
        EnglishLabel = english,
        ChineseLabel = chinese,
        AllowScpPlayers = true,
        AllowedRoleIds = new List<string>(RegularRoles),
        AllowedItemIds = new List<string>(itemIds),
        ScpSpawnAnchorRoleId = nativeSpawnAnchorRoleId,
        SpawnAnchorRoleId = nativeSpawnAnchorRoleId,
    };

    private static string ChineseItemLabel(string itemId) => itemId switch
    {
        "KeycardJanitor" => "清洁工钥匙卡", "KeycardScientist" => "科学家钥匙卡",
        "KeycardResearchCoordinator" => "研究主管钥匙卡", "KeycardZoneManager" => "分区经理钥匙卡",
        "KeycardGuard" => "设施警卫钥匙卡", "KeycardMTFPrivate" => "九尾狐列兵钥匙卡",
        "KeycardContainmentEngineer" => "收容工程师钥匙卡", "KeycardMTFOperative" => "九尾狐中士钥匙卡",
        "KeycardMTFCaptain" => "九尾狐指挥官钥匙卡", "KeycardFacilityManager" => "设施主管钥匙卡",
        "KeycardChaosInsurgency" => "混沌破解装置", "KeycardO5" => "O5 钥匙卡",
        "Radio" => "对讲机", "GunCOM15" => "COM-15 手枪", "Medkit" => "急救包",
        "Flashlight" => "手电筒", "MicroHID" => "Micro H.I.D. 粒子炮", "Ammo12gauge" => "12 号霰弹",
        "GunE11SR" => "E-11-SR 步枪", "GunCrossvec" => "Crossvec 冲锋枪", "Ammo556x45" => "5.56×45 毫米弹药",
        "GunFSP9" => "FSP-9 冲锋枪", "GunLogicer" => "Logicer 轻机枪", "GrenadeHE" => "高爆手榴弹",
        "GrenadeFlash" => "闪光弹", "Ammo44cal" => ".44 口径弹药", "Ammo762x39" => "7.62×39 毫米弹药",
        "Ammo9x19" => "9×19 毫米弹药", "GunCOM18" => "COM-18 手枪", "Adrenaline" => "肾上腺素",
        "Painkillers" => "止痛药", "Coin" => "硬币", "ArmorLight" => "轻型护甲",
        "ArmorCombat" => "战斗护甲", "ArmorHeavy" => "重型护甲", "GunRevolver" => ".44 左轮手枪",
        "GunAK" => "AK 步枪", "GunShotgun" => "霰弹枪", "ParticleDisruptor" => "粒子干扰器",
        "GunCom45" => "COM-45 手枪", "Jailbird" => "囚鸟电击棍", "AntiSCP207" => "逆向 SCP-207",
        "GunFRMG0" => "FR-MG-0 轻机枪", "GunA7" => "A7 步枪", "Lantern" => "提灯",
        "Snowball" => "雪球", "Coal" => "煤块", "SpecialCoal" => "特殊煤块",
        "SCP1507Tape" => "SCP-1507 录像带", "SurfaceAccessPass" => "地表通行证",
        "GunSCP127" => "SCP-127 枪械", "KeycardCustomTaskForce" => "特遣队自定义钥匙卡",
        "KeycardCustomSite02" => "Site-02 自定义钥匙卡", "KeycardCustomManagement" => "管理层自定义钥匙卡",
        "KeycardCustomMetalCase" => "金属盒自定义钥匙卡", "MarshmallowItem" => "棉花糖",
        "Scp021J" => "SCP-021-J",
        _ => itemId,
    };
}

public sealed class ItemCatalogEntryConfig
{
    /// <summary>Stable SSS/config identity. Renaming it intentionally resets that entry's ledger state.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Player-facing English label. Empty falls back to the stable Id.</summary>
    public string EnglishLabel { get; set; } = string.Empty;

    /// <summary>Player-facing Chinese label. Empty falls back to the stable Id.</summary>
    public string ChineseLabel { get; set; } = string.Empty;

    /// <summary>Exact native ItemType name. One request invokes AddItem exactly once.</summary>
    public string ItemId { get; set; } = string.Empty;

    public double CooldownSeconds { get; set; }

    public string SharedCooldownGroup { get; set; } = string.Empty;

    /// <summary>Zero uses CooldownSeconds for the shared group.</summary>
    public double SharedCooldownSeconds { get; set; }

    /// <summary>Zero means unlimited.</summary>
    public int PerLifeLimit { get; set; }

    /// <summary>Zero means unlimited.</summary>
    public int PerRoundLimit { get; set; }

    public List<string> AllowedRoleIds { get; set; } = new();

    public List<string> AllowedZoneIds { get; set; } = new();
}

public sealed class ArenaPresetConfig
{
    public string Id { get; set; } = string.Empty;

    /// <summary>Player-facing English label. Empty falls back to the stable Id.</summary>
    public string EnglishLabel { get; set; } = string.Empty;

    /// <summary>Player-facing Chinese label. Empty falls back to the stable Id.</summary>
    public string ChineseLabel { get; set; } = string.Empty;

    public bool AllowScpPlayers { get; set; }

    /// <summary>If non-empty, regular role choices are intersected with this exact allowlist.</summary>
    public List<string> AllowedRoleIds { get; set; } = new();

    /// <summary>If non-empty, item requests are intersected with these stable catalog IDs.</summary>
    public List<string> AllowedItemIds { get; set; } = new();

    /// <summary>Explicit native anchor role for SCP assignments in this preset.</summary>
    public string ScpSpawnAnchorRoleId { get; set; } = string.Empty;

    /// <summary>Explicit native anchor role for all assignments when the target has no override.</summary>
    public string SpawnAnchorRoleId { get; set; } = string.Empty;
}

public sealed class ArenaPresetDefinition
{
    public ArenaPresetDefinition(
        string id,
        bool allowScpPlayers,
        IEnumerable<string>? allowedRoleIds = null,
        IEnumerable<string>? allowedItemIds = null,
        string scpSpawnAnchorRoleId = "",
        string spawnAnchorRoleId = "",
        string englishLabel = "",
        string chineseLabel = "")
    {
        Id = id ?? string.Empty;
        EnglishLabel = string.IsNullOrWhiteSpace(englishLabel) ? Id : englishLabel;
        ChineseLabel = string.IsNullOrWhiteSpace(chineseLabel) ? Id : chineseLabel;
        AllowScpPlayers = allowScpPlayers;
        AllowedRoleIds = new HashSet<string>(allowedRoleIds ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        AllowedItemIds = new HashSet<string>(allowedItemIds ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        ScpSpawnAnchorRoleId = scpSpawnAnchorRoleId ?? string.Empty;
        SpawnAnchorRoleId = spawnAnchorRoleId ?? string.Empty;
    }

    public string Id { get; }

    public string EnglishLabel { get; }

    public string ChineseLabel { get; }

    public bool AllowScpPlayers { get; }

    public HashSet<string> AllowedRoleIds { get; }

    public HashSet<string> AllowedItemIds { get; }

    public string ScpSpawnAnchorRoleId { get; }

    public string SpawnAnchorRoleId { get; }

    public static ArenaPresetDefinition FromConfig(ArenaPresetConfig config)
    {
        if (config == null)
        {
            throw new ArgumentNullException(nameof(config));
        }

        return new ArenaPresetDefinition(
            config.Id,
            config.AllowScpPlayers,
            config.AllowedRoleIds,
            config.AllowedItemIds,
            config.ScpSpawnAnchorRoleId,
            config.SpawnAnchorRoleId,
            config.EnglishLabel,
            config.ChineseLabel);
    }
}
