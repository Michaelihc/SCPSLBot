using System;
using System.Collections.Generic;
using System.Linq;
using SCPSLBot.Warmup.Controls;
using SCPSLBot.Warmup.Policy;
using Xunit;

namespace SCPSLBot.PolicyTests;

public sealed class SpecialWeaponPolicyTests
{
    [Fact]
    public void DefaultCatalogOffersWeaponsBesideTheirNativeCarriers()
    {
        WarmupControlsConfig config = WarmupControlsConfig.CreateDefault();

        foreach (SpecialWeaponCatalogRow weapon in SpecialWeaponCatalogDefaults.Weapons)
        {
            ItemCatalogEntryConfig entry = Assert.Single(config.Items, item => item.Id == weapon.CatalogId);
            Assert.Equal("high-impact", entry.SharedCooldownGroup);
            Assert.Equal(1, entry.PerLifeLimit);
            Assert.All(config.Presets, preset => Assert.Contains(entry.Id, preset.AllowedItemIds));
        }

        Assert.Single(config.Items, item => item.ItemId == "GunShotgun");
        Assert.Single(config.Items, item => item.ItemId == "ParticleDisruptor");
        Assert.True(ItemCatalog.TryCreate(config.Items, out ItemCatalog? catalog, out IReadOnlyList<string> errors),
            string.Join("; ", errors));
        Assert.True(catalog!.Entries["special.guided-rocket"].IsSpecialWeapon);
        Assert.Equal("guided-rocket", catalog.Entries["special.guided-rocket"].SpecialWeaponId);
        Assert.False(catalog.Entries["native.gunshotgun"].IsSpecialWeapon);
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    public void PanelListsWeaponsOnlyWhenRequestsAreAllowed(bool allowPlayerRequests, bool available, bool expected)
    {
        Assert.Equal(
            expected,
            SpecialWeaponCatalogDefaults.IsRequestable("special:guided-rocket", allowPlayerRequests, _ => available));
    }

    [Fact]
    public void PanelAlwaysListsNativeItems()
    {
        Assert.True(SpecialWeaponCatalogDefaults.IsRequestable("GunShotgun", false, _ => false));
    }

    [Fact]
    public void ConfigReloadKeepsTunedWeaponEntries()
    {
        WarmupControlsConfig config = WarmupControlsConfig.CreateDefault();
        ItemCatalogEntryConfig rocket = Assert.Single(config.Items, item => item.Id == "special.guided-rocket");
        rocket.CooldownSeconds = 120;

        config.RestoreClassicPlayerOptions();

        Assert.Equal(120d, Assert.Single(config.Items, item => item.Id == "special.guided-rocket").CooldownSeconds);
    }

    [Fact]
    public void ExistingConfigGainsNewWeaponsAndArenaPermission()
    {
        WarmupControlsConfig config = WarmupControlsConfig.CreateDefault();
        config.Items.RemoveAll(item => SpecialWeaponCatalogDefaults.TryGetWeaponId(item.ItemId, out _));
        foreach (ArenaPresetConfig preset in config.Presets)
        {
            preset.AllowedItemIds.RemoveAll(id => id.StartsWith("special.", StringComparison.Ordinal));
        }

        IReadOnlyList<string> changes = config.RestoreClassicPlayerOptions();

        Assert.Contains(config.Items, item => item.Id == "special.graviton-disruptor");
        Assert.All(config.Presets, preset => Assert.Contains("special.graviton-disruptor", preset.AllowedItemIds));
        Assert.Contains(changes, change => change.Contains("special.graviton-disruptor", StringComparison.Ordinal));
    }

    [Fact]
    public void OpenArenaAllowlistStaysOpen()
    {
        WarmupControlsConfig config = WarmupControlsConfig.CreateDefault();
        config.Presets[0].AllowedItemIds.Clear();

        config.RestoreClassicPlayerOptions();

        Assert.Empty(config.Presets[0].AllowedItemIds);
    }

    [Theory]
    [InlineData("special:guided-rocket", true, "guided-rocket")]
    [InlineData("SPECIAL:guided-rocket", true, "guided-rocket")]
    [InlineData("special:", false, "")]
    [InlineData("GunShotgun", false, "")]
    [InlineData(null, false, "")]
    public void WeaponIdsParseFromCatalogItemIds(string? itemId, bool expected, string expectedWeaponId)
    {
        Assert.Equal(expected, SpecialWeaponCatalogDefaults.TryGetWeaponId(itemId, out string weaponId));
        Assert.Equal(expectedWeaponId, weaponId);
    }

    [Theory]
    [InlineData(0f, 500f)]
    [InlineData(2.25f, 125f)]
    [InlineData(4.5f, 0f)]
    [InlineData(9f, 0f)]
    public void BlastFallsOffToZeroAtTheRadius(float distance, float expected)
    {
        Assert.Equal(expected, SpecialWeaponBallistics.BlastDamage(500f, distance, 4.5f), 3);
    }

    [Fact]
    public void VictimArmorScalesWithPenetration()
    {
        Assert.Equal(100f, SpecialWeaponBallistics.ApplyVictimArmor(100f, 0, 50), 3);
        Assert.Equal(75f, SpecialWeaponBallistics.ApplyVictimArmor(100f, 50, 50), 3);
        Assert.Equal(100f, SpecialWeaponBallistics.ApplyVictimArmor(100f, 50, 100), 3);
        Assert.Equal(0f, SpecialWeaponBallistics.ApplyVictimArmor(float.NaN, 50, 50));
    }

    [Theory]
    [InlineData(100f, false, 90f)]
    [InlineData(2000f, false, 800f)]
    [InlineData(100f, true, 890f)]
    public void ShockwaveTakesCappedHealthShareAndCoreBonus(float maxHealth, bool insideCore, float expected)
    {
        Assert.Equal(expected, SpecialWeaponBallistics.ShockwaveDamage(maxHealth, 0.9f, 800f, insideCore, 800f), 3);
    }
}
