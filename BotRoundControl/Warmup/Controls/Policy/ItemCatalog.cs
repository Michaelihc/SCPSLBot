#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace SCPSLBot.Warmup.Controls;

public sealed class ItemCatalogEntry
{
    internal ItemCatalogEntry(ItemCatalogEntryConfig config)
    {
        StableId = config.Id;
        EnglishLabel = string.IsNullOrWhiteSpace(config.EnglishLabel) ? StableId : config.EnglishLabel;
        ChineseLabel = string.IsNullOrWhiteSpace(config.ChineseLabel) ? StableId : config.ChineseLabel;
        NativeItemId = config.ItemId;
        CooldownSeconds = config.CooldownSeconds;
        SharedCooldownGroup = config.SharedCooldownGroup ?? string.Empty;
        SharedCooldownSeconds = config.SharedCooldownSeconds > 0d
            ? config.SharedCooldownSeconds
            : config.CooldownSeconds;
        PerLifeLimit = config.PerLifeLimit;
        PerRoundLimit = config.PerRoundLimit;
        AllowedRoleIds = new HashSet<string>(config.AllowedRoleIds, StringComparer.OrdinalIgnoreCase);
        AllowedZoneIds = new HashSet<string>(config.AllowedZoneIds, StringComparer.OrdinalIgnoreCase);
    }

    public string StableId { get; }

    public string EnglishLabel { get; }

    public string ChineseLabel { get; }

    public string NativeItemId { get; }

    public double CooldownSeconds { get; }

    public string SharedCooldownGroup { get; }

    public double SharedCooldownSeconds { get; }

    public int PerLifeLimit { get; }

    public int PerRoundLimit { get; }

    public HashSet<string> AllowedRoleIds { get; }

    public HashSet<string> AllowedZoneIds { get; }
}

/// <summary>Validated stable-ID catalog. Unknown or duplicate IDs fail closed.</summary>
public sealed class ItemCatalog
{
    private readonly ReadOnlyDictionary<string, ItemCatalogEntry> entries;

    private ItemCatalog(Dictionary<string, ItemCatalogEntry> entries)
    {
        this.entries = new ReadOnlyDictionary<string, ItemCatalogEntry>(entries);
    }

    public IReadOnlyDictionary<string, ItemCatalogEntry> Entries => entries;

    public bool TryGet(string stableId, out ItemCatalogEntry? entry) =>
        entries.TryGetValue(stableId ?? string.Empty, out entry);

    public static bool TryCreate(
        IEnumerable<ItemCatalogEntryConfig> configs,
        out ItemCatalog? catalog,
        out IReadOnlyList<string> errors)
    {
        var foundErrors = new List<string>();
        var foundEntries = new Dictionary<string, ItemCatalogEntry>(StringComparer.Ordinal);

        if (configs == null)
        {
            foundErrors.Add("Item catalog is missing.");
        }
        else
        {
            int index = 0;
            foreach (ItemCatalogEntryConfig? config in configs)
            {
                string prefix = $"Items[{index}]";
                index++;
                int errorsBeforeEntry = foundErrors.Count;
                if (config == null)
                {
                    foundErrors.Add($"{prefix} is null.");
                    continue;
                }

                if (!IsStableId(config.Id))
                {
                    foundErrors.Add($"{prefix}.Id must contain only letters, digits, '.', '_' or '-'.");
                }

                if (string.IsNullOrWhiteSpace(config.ItemId))
                {
                    foundErrors.Add($"{prefix}.ItemId is required.");
                }

                if (!IsFiniteNonNegative(config.CooldownSeconds)
                    || !IsFiniteNonNegative(config.SharedCooldownSeconds))
                {
                    foundErrors.Add($"{prefix} cooldowns must be finite and non-negative.");
                }

                if (!string.IsNullOrWhiteSpace(config.SharedCooldownGroup)
                    && !IsStableId(config.SharedCooldownGroup))
                {
                    foundErrors.Add($"{prefix}.SharedCooldownGroup must be a stable ID.");
                }

                if (config.PerLifeLimit < 0 || config.PerRoundLimit < 0)
                {
                    foundErrors.Add($"{prefix} limits must be non-negative.");
                }

                if (config.AllowedRoleIds == null || config.AllowedRoleIds.All(string.IsNullOrWhiteSpace))
                {
                    foundErrors.Add($"{prefix}.AllowedRoleIds must explicitly allow at least one role.");
                }

                if (config.AllowedZoneIds == null || config.AllowedZoneIds.All(string.IsNullOrWhiteSpace))
                {
                    foundErrors.Add($"{prefix}.AllowedZoneIds must explicitly allow at least one zone.");
                }

                if (!string.IsNullOrWhiteSpace(config.Id) && foundEntries.ContainsKey(config.Id))
                {
                    foundErrors.Add($"{prefix}.Id duplicates stable item catalog ID '{config.Id}'.");
                }

                if (foundErrors.Count == errorsBeforeEntry)
                {
                    foundEntries.Add(config.Id, new ItemCatalogEntry(config));
                }
            }
        }

        errors = foundErrors;
        if (foundErrors.Count > 0)
        {
            catalog = null;
            return false;
        }

        catalog = new ItemCatalog(foundEntries);
        return true;
    }

    private static bool IsStableId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        foreach (char character in value)
        {
            if (!char.IsLetterOrDigit(character)
                && character != '.'
                && character != '_'
                && character != '-')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsFiniteNonNegative(double value) =>
        value >= 0d && !double.IsInfinity(value) && !double.IsNaN(value);
}
