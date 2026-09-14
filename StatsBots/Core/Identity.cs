using System;

namespace StatsBots.Core;

internal static class AuthenticatedIdentity
{
    public const string DummyUserId = "ID_Dummy";

    public static bool IsFullUserId(string? userId)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.Equals(userId, DummyUserId, StringComparison.OrdinalIgnoreCase))
            return false;

        string value = userId!.Trim();
        int at = value.LastIndexOf('@');
        return at > 0 && at == value.IndexOf('@') && at < value.Length - 1
            && value.IndexOfAny(new[] { ' ', '\t', '\r', '\n' }) < 0;
    }

    public static bool TryNormalize(string? userId, out string normalized)
    {
        if (!IsFullUserId(userId))
        {
            normalized = string.Empty;
            return false;
        }

        normalized = userId!.Trim();
        return true;
    }
}

internal static class StatsKeys
{
    public const string BotKills = "Warmup.BotKills";
    public const string BotDeaths = "Warmup.BotDeaths";
    public const string Score = "Warmup.Score";
    public const string CurrentStreak = "Warmup.CurrentStreak";
    public const string BestStreak = "Warmup.BestStreak";
    public const string SelectedTagCode = "Warmup.SelectedTagCode";
    public const string TotalPlayTime = "TotalPlayTime";

    public static string TagUnlocked(string id) => "Warmup.TagUnlocked." + id;

    public static bool IsWarmupKey(string key) => key != null && key.StartsWith("Warmup.", StringComparison.Ordinal);
}
