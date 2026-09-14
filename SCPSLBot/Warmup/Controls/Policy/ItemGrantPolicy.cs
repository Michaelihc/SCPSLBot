#nullable enable

using System;

namespace SCPSLBot.Warmup.Controls;

/// <summary>
/// Live state adapter. Implementations must read current server state rather than an SSS render snapshot.
/// </summary>
public interface IItemGrantContext
{
    string FullUserId { get; }

    bool IsAuthenticated { get; }

    bool IsRealPlayer { get; }

    bool IsPlayerAvailable { get; }

    bool IsWarmupActive { get; }

    bool IsAlive { get; }

    string ExactRoleId { get; }

    string ExactZoneId { get; }

    string LifeId { get; }

    string RoundId { get; }

    bool HasInventoryCapacity { get; }

    bool IsAllowedByActivePreset(ItemCatalogEntry entry);

    /// <summary>Calls the native inventory add exactly once and reports whether an item was returned.</summary>
    bool TryAddExactItemOnce(ItemCatalogEntry entry);
}

public sealed class ItemGrantRequest
{
    public ItemGrantRequest(string expectedFullUserId, string stableCatalogId)
    {
        ExpectedFullUserId = expectedFullUserId ?? string.Empty;
        StableCatalogId = stableCatalogId ?? string.Empty;
    }

    public string ExpectedFullUserId { get; }

    public string StableCatalogId { get; }
}

/// <summary>
/// Performs the complete item grant transaction under a per-user guard. Cooldowns and counters
/// are committed only after a single successful native add.
/// </summary>
public sealed class ItemGrantPolicy
{
    private readonly ItemCatalog catalog;
    private readonly CooldownLedger ledger;
    private readonly PerUserRequestGuard requestGuard;

    public ItemGrantPolicy(ItemCatalog catalog, CooldownLedger ledger, PerUserRequestGuard requestGuard)
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        this.requestGuard = requestGuard ?? throw new ArgumentNullException(nameof(requestGuard));
    }

    public ControlResult TryGrant(IItemGrantContext context, ItemGrantRequest request)
    {
        if (context == null
            || request == null
            || string.IsNullOrWhiteSpace(request.ExpectedFullUserId)
            || string.IsNullOrWhiteSpace(request.StableCatalogId))
        {
            return ControlResult.Reject(ControlResultCode.InvalidRequest);
        }

        if (!requestGuard.TryEnter(request.ExpectedFullUserId, out IDisposable? lease) || lease == null)
        {
            return ControlResult.Reject(ControlResultCode.ConcurrentRequest);
        }

        using (lease)
        {
            try
            {
                ControlResult validated = EvaluateState(context, request, out ItemCatalogEntry? entry);
                if (!validated.Succeeded || entry == null)
                {
                    return validated;
                }

                if (!ledger.TryReserve(
                        context.RoundId,
                        request.ExpectedFullUserId,
                        entry,
                        context.LifeId,
                        out CooldownLedger.Reservation? reservation,
                        out ControlResult availability)
                    || reservation == null)
                {
                    return availability;
                }

                using (reservation)
                {
                    bool added;
                    try
                    {
                        added = context.TryAddExactItemOnce(entry);
                    }
                    catch
                    {
                        added = false;
                    }

                    if (!added)
                    {
                        return ControlResult.Reject(ControlResultCode.ItemGrantFailed, entry.StableId);
                    }

                    reservation.Commit();
                    return ControlResult.Success(entry.StableId);
                }
            }
            catch
            {
                return ControlResult.Reject(ControlResultCode.ItemGrantFailed, request.StableCatalogId);
            }
        }
    }

    /// <summary>Read-only availability check for personalized SSS option construction.</summary>
    public ControlResult EvaluateAvailability(IItemGrantContext context, ItemGrantRequest request)
    {
        try
        {
            ControlResult validated = EvaluateState(context, request, out ItemCatalogEntry? entry);
            if (!validated.Succeeded || entry == null)
            {
                return validated;
            }

            return ledger.GetAvailability(context.RoundId, request.ExpectedFullUserId, entry, context.LifeId);
        }
        catch
        {
            return ControlResult.Reject(ControlResultCode.PlayerUnavailable);
        }
    }

    private ControlResult EvaluateState(
        IItemGrantContext context,
        ItemGrantRequest request,
        out ItemCatalogEntry? entry)
    {
        entry = null;
        if (context == null
            || request == null
            || string.IsNullOrWhiteSpace(request.ExpectedFullUserId)
            || string.IsNullOrWhiteSpace(request.StableCatalogId))
        {
            return ControlResult.Reject(ControlResultCode.InvalidRequest);
        }

        if (!string.Equals(context.FullUserId, request.ExpectedFullUserId, StringComparison.Ordinal))
        {
            return ControlResult.Reject(ControlResultCode.InvalidRequest);
        }

        if (!context.IsAuthenticated)
        {
            return ControlResult.Reject(ControlResultCode.Unauthenticated);
        }

        if (!context.IsRealPlayer)
        {
            return ControlResult.Reject(ControlResultCode.NotRealPlayer);
        }

        if (!context.IsPlayerAvailable)
        {
            return ControlResult.Reject(ControlResultCode.PlayerUnavailable);
        }

        if (!context.IsWarmupActive)
        {
            return ControlResult.Reject(ControlResultCode.WarmupInactive);
        }

        if (!context.IsAlive)
        {
            return ControlResult.Reject(ControlResultCode.NotAlive);
        }

        if (!catalog.TryGet(request.StableCatalogId, out entry) || entry == null)
        {
            return ControlResult.Reject(ControlResultCode.CatalogEntryNotFound, request.StableCatalogId);
        }

        if (!context.IsAllowedByActivePreset(entry))
        {
            return ControlResult.Reject(ControlResultCode.ItemNotAllowedByPreset, entry.StableId);
        }

        if (!entry.AllowedRoleIds.Contains(context.ExactRoleId))
        {
            return ControlResult.Reject(ControlResultCode.RoleCannotRequestItem, entry.StableId);
        }

        if (!entry.AllowedZoneIds.Contains(context.ExactZoneId))
        {
            return ControlResult.Reject(ControlResultCode.ZoneCannotRequestItem, entry.StableId);
        }

        if (!context.HasInventoryCapacity)
        {
            return ControlResult.Reject(ControlResultCode.InventoryFull, entry.StableId);
        }

        return ControlResult.Success(entry.StableId);
    }
}
