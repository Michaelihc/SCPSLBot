using SCPSLBot.Warmup.Controls;

namespace SCPSLBot.PolicyTests;

public sealed class LocalizationTests
{
    [Fact]
    public void EmptyLanguageMatchesClientAndFallsBackToChinese()
    {
        ControlResult result = ControlResult.Reject(ControlResultCode.ItemCooldown, "medkit", 2.01);

        Assert.Contains("3", result.Localize("", "en"), StringComparison.Ordinal);
        Assert.Contains("seconds", result.Localize("", "en"), StringComparison.Ordinal);
        Assert.Contains("3", result.Localize("", "zh-CN"), StringComparison.Ordinal);
        Assert.Contains("秒", result.Localize("", null), StringComparison.Ordinal);
    }

    [Fact]
    public void ForcedLanguageOverridesClient()
    {
        ControlResult result = ControlResult.Reject(ControlResultCode.PermissionDenied);

        Assert.Contains("permission", result.Localize("en", "zh-CN"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("权限", result.Localize("cn", "en"), StringComparison.Ordinal);
    }

    [Fact]
    public void ArenaPresetDefinitionCarriesBilingualLabelsWithStableFallback()
    {
        ArenaPresetDefinition localized = ArenaPresetDefinition.FromConfig(new ArenaPresetConfig
        {
            Id = "standard-arena",
            EnglishLabel = "Standard arena",
            ChineseLabel = "标准竞技场",
        });
        ArenaPresetDefinition fallback = ArenaPresetDefinition.FromConfig(new ArenaPresetConfig
        {
            Id = "fallback-id",
        });

        Assert.Equal("Standard arena", localized.EnglishLabel);
        Assert.Equal("标准竞技场", localized.ChineseLabel);
        Assert.Equal("fallback-id", fallback.EnglishLabel);
        Assert.Equal("fallback-id", fallback.ChineseLabel);
    }
}
