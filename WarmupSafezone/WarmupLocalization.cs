using System;
using LabApi.Features.Wrappers;

namespace ScpslPluginStarter;

internal sealed class WarmupLocalization
{
    private readonly string _configuredLanguage;

    public WarmupLocalization(string? language)
    {
        _configuredLanguage = (language ?? string.Empty).Trim().ToLowerInvariant();
    }

    public string For(Player? player, string english, string chinese)
    {
        // LabAPI 1.1.6 does not expose a player's client language. Keep the per-player
        // boundary here so a future supported API can replace only this resolver.
        return IsChinese(player) ? chinese : english;
    }

    public string Shared(string english, string chinese) => IsChinese(null) ? chinese : english;

    private bool IsChinese(Player? player)
    {
        if (_configuredLanguage.StartsWith("en", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (_configuredLanguage.StartsWith("cn", StringComparison.OrdinalIgnoreCase)
            || _configuredLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return true;
    }
}
