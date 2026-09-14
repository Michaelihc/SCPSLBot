#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using InventorySystem;
using LabPlayer = LabApi.Features.Wrappers.Player;
using MapGeneration;
using PlayerRoles;

namespace SCPSLBot.Warmup.Controls;

/// <summary>Live warmup/round/preset authority supplied by the integration owner.</summary>
public interface ILabApiItemControlAuthority
{
    bool IsWarmupActive { get; }

    string CurrentRoundId { get; }

    ArenaPresetDefinition GetActivePreset(LabPlayer player);
}

/// <summary>Native facade for SSS availability and deliberate item request callbacks.</summary>
public sealed class LabApiItemGrantService
{
    private readonly ItemCatalog catalog;
    private readonly ILabApiItemControlAuthority authority;
    private readonly ItemGrantPolicy policy;

    private LabApiItemGrantService(
        ItemCatalog catalog,
        ILabApiItemControlAuthority authority,
        CooldownLedger ledger,
        PerUserRequestGuard requestGuard)
    {
        this.catalog = catalog;
        this.authority = authority;
        Ledger = ledger;
        policy = new ItemGrantPolicy(catalog, ledger, requestGuard);
    }

    public CooldownLedger Ledger { get; }

    public IReadOnlyDictionary<string, ItemCatalogEntry> Entries => catalog.Entries;

    public static bool TryCreate(
        IEnumerable<ItemCatalogEntryConfig> configs,
        ILabApiItemControlAuthority authority,
        out LabApiItemGrantService? service,
        out IReadOnlyList<string> errors,
        IMonotonicClock? clock = null,
        PerUserRequestGuard? sharedRequestGuard = null)
    {
        var foundErrors = new List<string>();
        if (!ItemCatalog.TryCreate(configs, out ItemCatalog? catalog, out IReadOnlyList<string> catalogErrors)
            || catalog == null)
        {
            foundErrors.AddRange(catalogErrors);
        }
        else
        {
            foreach (ItemCatalogEntry entry in catalog.Entries.Values)
            {
                if (!Enum.TryParse(entry.NativeItemId, true, out ItemType itemType)
                    || !Enum.IsDefined(typeof(ItemType), itemType)
                    || itemType == ItemType.None)
                {
                    foundErrors.Add(
                        $"Item catalog '{entry.StableId}' has unknown native ItemType '{entry.NativeItemId}'.");
                }

                foreach (string roleId in entry.AllowedRoleIds)
                {
                    if (!Enum.TryParse(roleId, true, out RoleTypeId role)
                        || !Enum.IsDefined(typeof(RoleTypeId), role)
                        || role is RoleTypeId.None or RoleTypeId.Spectator or RoleTypeId.Destroyed)
                    {
                        foundErrors.Add(
                            $"Item catalog '{entry.StableId}' has invalid allowed role '{roleId}'.");
                    }
                }

                foreach (string zoneId in entry.AllowedZoneIds)
                {
                    if (!Enum.TryParse(zoneId, true, out FacilityZone zone)
                        || !Enum.IsDefined(typeof(FacilityZone), zone)
                        || zone == FacilityZone.None)
                    {
                        foundErrors.Add(
                            $"Item catalog '{entry.StableId}' has invalid allowed zone '{zoneId}'.");
                    }
                }
            }
        }

        if (authority == null)
        {
            foundErrors.Add("The LabAPI item control authority is required.");
        }

        errors = foundErrors;
        if (foundErrors.Count > 0 || catalog == null || authority == null)
        {
            service = null;
            return false;
        }

        service = new LabApiItemGrantService(
            catalog,
            authority,
            new CooldownLedger(clock ?? StopwatchMonotonicClock.Instance),
            sharedRequestGuard ?? new PerUserRequestGuard());
        return true;
    }

    /// <summary>Call from the authoritative round-start transition, once per stable round ID.</summary>
    public void BeginRound(string roundId) => Ledger.BeginRound(roundId);

    public IReadOnlyList<ItemCatalogEntry> GetCurrentlyAvailableEntries(
        LabPlayer player,
        string expectedFullUserId)
    {
        if (player == null)
        {
            return Array.Empty<ItemCatalogEntry>();
        }

        var context = new LabApiItemGrantContext(player, authority);
        return catalog.Entries.Values
            .Where(entry => policy.EvaluateAvailability(
                context,
                new ItemGrantRequest(expectedFullUserId, entry.StableId)).Succeeded)
            .OrderBy(entry => entry.StableId, StringComparer.Ordinal)
            .ToArray();
    }

    public ControlResult GetAvailability(
        LabPlayer player,
        string expectedFullUserId,
        string stableCatalogId)
    {
        if (player == null)
        {
            return ControlResult.Reject(ControlResultCode.InvalidRequest);
        }

        return policy.EvaluateAvailability(
            new LabApiItemGrantContext(player, authority),
            new ItemGrantRequest(expectedFullUserId, stableCatalogId));
    }

    public ControlResult TryGrant(LabPlayer player, string expectedFullUserId, string stableCatalogId)
    {
        if (player == null)
        {
            return ControlResult.Reject(ControlResultCode.InvalidRequest);
        }

        return policy.TryGrant(
            new LabApiItemGrantContext(player, authority),
            new ItemGrantRequest(expectedFullUserId, stableCatalogId));
    }
}

internal sealed class LabApiItemGrantContext : IItemGrantContext
{
    private readonly LabPlayer player;
    private readonly ILabApiItemControlAuthority authority;

    public LabApiItemGrantContext(LabPlayer player, ILabApiItemControlAuthority authority)
    {
        this.player = player;
        this.authority = authority;
    }

    public string FullUserId => player.IsDestroyed ? string.Empty : player.UserId ?? string.Empty;

    public bool IsAuthenticated =>
        !player.IsDestroyed && player.IsReady && !string.IsNullOrWhiteSpace(player.UserId);

    public bool IsRealPlayer =>
        !player.IsDestroyed && player.IsPlayer && !player.IsDummy && !player.IsHost;

    public bool IsPlayerAvailable => !player.IsDestroyed;

    public bool IsWarmupActive => authority.IsWarmupActive;

    public bool IsAlive => !player.IsDestroyed && player.IsAlive && player.Role != RoleTypeId.Spectator;

    public string ExactRoleId => player.IsDestroyed ? string.Empty : player.Role.ToString();

    public string ExactZoneId => player.IsDestroyed ? string.Empty : player.Zone.ToString();

    public string LifeId => player.IsDestroyed
        ? string.Empty
        : player.LifeId.ToString(CultureInfo.InvariantCulture);

    public string RoundId => authority.CurrentRoundId ?? string.Empty;

    public bool HasInventoryCapacity => !player.IsDestroyed && !player.IsInventoryFull;

    public bool IsAllowedByActivePreset(ItemCatalogEntry entry)
    {
        ArenaPresetDefinition preset = authority.GetActivePreset(player);
        return preset != null
            && (preset.AllowedItemIds.Count == 0 || preset.AllowedItemIds.Contains(entry.StableId));
    }

    public bool TryAddExactItemOnce(ItemCatalogEntry entry)
    {
        if (player.IsDestroyed
            || !Enum.TryParse(entry.NativeItemId, true, out ItemType itemType)
            || !Enum.IsDefined(typeof(ItemType), itemType)
            || itemType == ItemType.None)
        {
            return false;
        }

        LabApi.Features.Wrappers.Item? added = player.AddItem(itemType);
        return added != null && added.Type == itemType;
    }
}
