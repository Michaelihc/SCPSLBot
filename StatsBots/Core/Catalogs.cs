using System;
using System.Collections.Generic;
using System.Linq;
using StatsBots.Config;

namespace StatsBots.Core;

internal static class TierCatalog
{
    public static List<TierConfig> Normalize(IEnumerable<TierConfig>? source)
    {
        var result = new List<TierConfig>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (TierConfig? tier in source ?? TierConfig.Defaults())
        {
            if (tier == null || !IsCatalogId(tier.Id) || ids.Contains(tier.Id) || string.IsNullOrWhiteSpace(tier.English) || string.IsNullOrWhiteSpace(tier.Chinese)) continue;
            ids.Add(tier.Id);
            tier.MinimumScore = Math.Max(0, tier.MinimumScore);
            result.Add(tier);
        }
        if (result.Count == 0) result = TierConfig.Defaults();
        result.Sort(static (a, b) => a.MinimumScore.CompareTo(b.MinimumScore));
        if (result[0].MinimumScore != 0)
        {
            TierConfig fallback = TierConfig.Defaults()[0];
            if (ids.Contains(fallback.Id)) fallback.Id = UniqueBaselineId(ids);
            result.Insert(0, fallback);
        }
        return result;
    }

    public static TierConfig Resolve(IReadOnlyList<TierConfig> tiers, long score)
    {
        TierConfig current = tiers[0];
        for (int i = 1; i < tiers.Count && score >= tiers[i].MinimumScore; i++) current = tiers[i];
        return current;
    }

    public static long? NextThreshold(IReadOnlyList<TierConfig> tiers, long score)
        => tiers.FirstOrDefault(t => t.MinimumScore > score)?.MinimumScore;

    internal static bool IsCatalogId(string? id)
        => !string.IsNullOrWhiteSpace(id) && id!.Length <= 48 && id.All(static c => char.IsLetterOrDigit(c) || c is '-' or '_');

    private static string UniqueBaselineId(ISet<string> ids)
    {
        const string stem = "baseline";
        if (!ids.Contains(stem)) return stem;
        for (int i = 2; ; i++)
        {
            string candidate = stem + "-" + i;
            if (!ids.Contains(candidate)) return candidate;
        }
    }
}

internal static class TitleCatalog
{
    public static List<TitleConfig> Normalize(IEnumerable<TitleConfig>? source)
    {
        var result = new List<TitleConfig>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var codes = new HashSet<long>();
        foreach (TitleConfig? title in source ?? TitleConfig.Defaults())
        {
            if (title == null || !TierCatalog.IsCatalogId(title.Id) || title.Code <= 0 || ids.Contains(title.Id) || codes.Contains(title.Code)
                || string.IsNullOrWhiteSpace(title.English) || string.IsNullOrWhiteSpace(title.Chinese)) continue;
            ids.Add(title.Id);
            codes.Add(title.Code);
            title.MinimumScore = Math.Max(0, title.MinimumScore);
            result.Add(title);
        }
        if (result.Count == 0) result = TitleConfig.Defaults();
        result.Sort(static (a, b) => a.Code.CompareTo(b.Code));
        return result;
    }

    public static TitleConfig? ById(IReadOnlyList<TitleConfig> titles, string id)
        => titles.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

    public static TitleConfig? ByCode(IReadOnlyList<TitleConfig> titles, long code)
        => titles.FirstOrDefault(t => t.Code == code);

    public static bool IsUnlocked(TitleConfig title, long score, long explicitState)
        => explicitState > 0 || (explicitState == 0 && score >= title.MinimumScore);
}
