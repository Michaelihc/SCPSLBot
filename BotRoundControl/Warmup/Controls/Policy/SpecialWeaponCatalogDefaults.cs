#nullable enable

using System;
using System.Collections.Generic;

namespace SCPSLBot.Warmup.Controls;

/// <summary>One player-requestable special weapon as the warmup item catalog sees it.</summary>
public sealed class SpecialWeaponCatalogRow
{
    public SpecialWeaponCatalogRow(string weaponId, string englishLabel, string chineseLabel)
    {
        WeaponId = weaponId;
        EnglishLabel = englishLabel;
        ChineseLabel = chineseLabel;
    }

    public string WeaponId { get; }

    public string EnglishLabel { get; }

    public string ChineseLabel { get; }

    public string CatalogId => "special." + WeaponId;

    public string ItemId => SpecialWeaponCatalogDefaults.ToItemId(WeaponId);
}

/// <summary>
/// The catalog side of the special weapon list. A catalog <c>ItemId</c> of <c>special:&lt;weapon&gt;</c>
/// names a weapon instead of a native ItemType, so a weapon can share a carrier with a plain native entry.
/// </summary>
public static class SpecialWeaponCatalogDefaults
{
    private const string ItemIdPrefix = "special:";

    public static IReadOnlyList<SpecialWeaponCatalogRow> Weapons { get; } = new[]
    {
        new SpecialWeaponCatalogRow("graviton-disruptor", "Graviton Disruptor", "引力粒子裂解者"),
        new SpecialWeaponCatalogRow("guided-rocket", "Guided Rocket Launcher", "制导火箭筒"),
    };

    public static string ToItemId(string weaponId) => ItemIdPrefix + weaponId;

    public static bool TryGetWeaponId(string? itemId, out string weaponId)
    {
        if (itemId != null
            && itemId.Length > ItemIdPrefix.Length
            && itemId.StartsWith(ItemIdPrefix, StringComparison.OrdinalIgnoreCase))
        {
            weaponId = itemId.Substring(ItemIdPrefix.Length);
            return true;
        }

        weaponId = string.Empty;
        return false;
    }
}
