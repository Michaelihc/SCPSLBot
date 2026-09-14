#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using InventorySystem;
using LabApi.Features.Console;
using LabApi.Features.Wrappers;
using MapGeneration;
using PlayerRoles;
using RemoteAdmin;
using SCPSLBot.Api;
using SCPSLBot.Navigation.Mesh;
using SCPSLBot.Presentation;
using UnityEngine;
using LabLogger = LabApi.Features.Console.Logger;

namespace SCPSLBot.Warmup.Controls.Panel;

/// <summary>
/// Default live LabAPI authority for the panel and the role/item policy facades. It keeps the active
/// preset in one place, uses full UserIds for every action, grants loadouts transactionally, and gives
/// NavigationMeshEditor exactly one current owner.
/// </summary>
internal sealed class LabApiWarmupPanelActions :
    IWarmupPanelActions,
    ILabApiRoleControlAuthority,
    ILabApiItemControlAuthority
{
    private const int MaximumTeleportDestinationSlots = 254;
    private static readonly ArenaPresetDefinition ClosedPreset = new("unconfigured", false);

    private readonly WarmupControlsConfig controlsConfig;
    private readonly BotPresentationService presentation;
    private readonly Func<bool> isWarmupActive;
    private readonly Func<string> currentRoundId;
    private readonly Func<Player, string?> clientLanguage;
    private readonly PerUserRequestGuard requestGuard;
    private readonly Dictionary<string, WarmupLoadoutConfig> loadouts;
    private readonly Dictionary<string, List<string>> teleportDestinationSlots = new(StringComparer.Ordinal);

    public LabApiWarmupPanelActions(
        WarmupControlsConfig controlsConfig,
        WarmupPanelConfig panelConfig,
        BotPresentationService presentation,
        PerUserRequestGuard sharedRequestGuard,
        Func<bool>? isWarmupActive = null,
        Func<string>? currentRoundId = null,
        Func<Player, string?>? clientLanguage = null)
    {
        this.controlsConfig = controlsConfig ?? throw new ArgumentNullException(nameof(controlsConfig));
        panelConfig = panelConfig ?? throw new ArgumentNullException(nameof(panelConfig));
        this.presentation = presentation ?? throw new ArgumentNullException(nameof(presentation));
        requestGuard = sharedRequestGuard ?? throw new ArgumentNullException(nameof(sharedRequestGuard));
        this.isWarmupActive = isWarmupActive ?? (() => global::SCPSLBot.Warmup.WarmupManager.Instance.IsStandardWarmup);
        this.currentRoundId = currentRoundId ?? (() => RoundRestarting.RoundRestart.UptimeRounds.ToString(CultureInfo.InvariantCulture));
        this.clientLanguage = clientLanguage ?? (_ => null);

        loadouts = ValidateLoadouts(panelConfig.Loadouts);
    }

    public bool IsWarmupActive => SafeBool(isWarmupActive);

    public string CurrentRoundId => SafeString(currentRoundId);

    public string GetActiveArenaPresetId(Player player, string expectedFullUserId) =>
        IsCurrentRealPlayer(player, expectedFullUserId)
            ? global::SCPSLBot.Warmup.WarmupManager.Instance.GetPlayerArenaId(player.PlayerId)
            : string.Empty;

    public string? GetClientLanguage(Player player)
    {
        try
        {
            return clientLanguage(player);
        }
        catch
        {
            return null;
        }
    }

    public ArenaPresetDefinition GetActivePreset(Player player)
    {
        string activeId = player == null
            ? string.Empty
            : global::SCPSLBot.Warmup.WarmupManager.Instance.GetPlayerArenaId(player.PlayerId);
        ArenaPresetConfig? config = (controlsConfig.Presets ?? new List<ArenaPresetConfig>())
            .FirstOrDefault(preset => preset != null
                && string.Equals(preset.Id, activeId, StringComparison.OrdinalIgnoreCase))
            ?? (controlsConfig.Presets ?? new List<ArenaPresetConfig>())
                .FirstOrDefault(preset => preset != null && !string.IsNullOrWhiteSpace(preset.Id));
        return config == null ? ClosedPreset : ArenaPresetDefinition.FromConfig(config);
    }

    public bool HasForceRolePermission(Player player) =>
        IsCurrentRealPlayer(player)
        && HasPermission(player, PlayerPermissions.PlayersManagement);

    public IReadOnlyList<WarmupPanelChoice> GetAvailableLoadouts(Player player, string expectedFullUserId)
    {
        if (!IsCurrentRealPlayer(player, expectedFullUserId) || !IsWarmupActive || !player.IsAlive)
        {
            return Array.Empty<WarmupPanelChoice>();
        }

        // Keep slots stable across role, zone, and inventory changes. Execution revalidates the
        // selected loadout; removing an earlier option would remap a stale client index.
        return loadouts.Values
            .OrderBy(config => config.Id, StringComparer.Ordinal)
            .Select(config => new WarmupPanelChoice(config.Id, config.EnglishLabel, config.ChineseLabel))
            .ToArray();
    }

    public ControlResult TryEquipLoadout(Player player, string expectedFullUserId, string loadoutId)
    {
        string requestedLoadoutId = loadoutId ?? string.Empty;
        if (!TryEnterCurrent(player, expectedFullUserId, out IDisposable? lease, out ControlResult rejected)
            || lease == null)
        {
            return rejected;
        }

        using (lease)
        {
            if (!IsWarmupActive)
            {
                return ControlResult.Reject(ControlResultCode.WarmupInactive);
            }

            if (!loadouts.TryGetValue(requestedLoadoutId, out WarmupLoadoutConfig? config)
                || !TryResolveLoadout(player, config, out ItemType[] exactItems))
            {
                return ControlResult.Reject(ControlResultCode.InvalidRequest, requestedLoadoutId);
            }

            var granted = new List<Item>(exactItems.Length);
            try
            {
                foreach (ItemType exactItem in exactItems)
                {
                    Item? item = player.AddItem(exactItem);
                    if (item != null)
                    {
                        granted.Add(item);
                    }

                    if (item == null || item.Type != exactItem)
                    {
                        RollBackItems(player, granted);
                        return ControlResult.Reject(ControlResultCode.ItemGrantFailed, requestedLoadoutId);
                    }
                }

                if (!IsCurrentRealPlayer(player, expectedFullUserId))
                {
                    RollBackItems(player, granted);
                    return ControlResult.Reject(ControlResultCode.PlayerUnavailable, requestedLoadoutId);
                }

                return ControlResult.Success(requestedLoadoutId);
            }
            catch (Exception exception)
            {
                RollBackItems(player, granted);
                LabLogger.Warn($"[SCPSLBot] Loadout '{requestedLoadoutId}' rolled back: {exception.GetBaseException().Message}");
                return ControlResult.Reject(ControlResultCode.ItemGrantFailed, requestedLoadoutId);
            }
        }
    }

    public IReadOnlyList<WarmupPanelChoice> GetAvailableTeleportDestinations(
        Player player,
        string expectedFullUserId)
    {
        if (!IsCurrentRealPlayer(player, expectedFullUserId) || !IsWarmupActive || !player.IsAlive)
        {
            return Array.Empty<WarmupPanelChoice>();
        }

        try
        {
            Dictionary<string, Player> liveTargets = Player.ReadyList
                .Where(candidate => IsTeleportTarget(candidate, player, expectedFullUserId))
                .GroupBy(candidate => candidate.UserId, StringComparer.Ordinal)
                .Where(group => group.Count() == 1)
                .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);

            if (!teleportDestinationSlots.TryGetValue(expectedFullUserId, out List<string>? slots))
            {
                slots = new List<string>();
                teleportDestinationSlots[expectedFullUserId] = slots;
            }

            string[] newlyAvailable = liveTargets.Values
                .Where(candidate => global::SCPSLBot.Warmup.WarmupManager.Instance
                    .CanPlayersTeleportWithinArena(player, candidate))
                .Select(candidate => candidate.UserId)
                .Where(id => !slots.Contains(id, StringComparer.Ordinal))
                .OrderBy(id => id, StringComparer.Ordinal)
                .Take(Math.Max(0, MaximumTeleportDestinationSlots - slots.Count))
                .ToArray();
            slots.AddRange(newlyAvailable);

            return slots.Select(id =>
                {
                    if (liveTargets.TryGetValue(id, out Player? candidate)
                        && global::SCPSLBot.Warmup.WarmupManager.Instance
                            .CanPlayersTeleportWithinArena(player, candidate))
                    {
                        return new WarmupPanelChoice(id, candidate.DisplayName, candidate.DisplayName);
                    }

                    // Tombstones preserve every earlier slot. A stale click resolves to the same
                    // stable UserId and is rejected by TryTeleport instead of targeting a neighbour.
                    return new WarmupPanelChoice(id, "Unavailable", "不可用");
                })
                .ToArray();
        }
        catch
        {
            return Array.Empty<WarmupPanelChoice>();
        }
    }

    public ControlResult TryTeleport(Player player, string expectedFullUserId, string destinationId)
    {
        if (!TryEnterCurrent(player, expectedFullUserId, out IDisposable? lease, out ControlResult rejected)
            || lease == null)
        {
            return rejected;
        }

        using (lease)
        {
            if (!IsWarmupActive || !player.IsAlive)
            {
                return ControlResult.Reject(
                    IsWarmupActive ? ControlResultCode.NotAlive : ControlResultCode.WarmupInactive,
                    destinationId);
            }

            Player[] matches = Player.ReadyList
                .Where(candidate => string.Equals(candidate.UserId, destinationId, StringComparison.Ordinal)
                    && IsTeleportTarget(candidate, player, expectedFullUserId)
                    && global::SCPSLBot.Warmup.WarmupManager.Instance
                        .CanPlayersTeleportWithinArena(player, candidate))
                .ToArray();
            if (matches.Length != 1)
            {
                return ControlResult.Reject(ControlResultCode.InvalidRequest, destinationId);
            }

            Player target = matches[0];
            Vector3 oldPosition = player.Position;
            Vector3 targetPosition = target.Position;
            try
            {
                player.Position = targetPosition;
                if (!IsCurrentRealPlayer(player, expectedFullUserId)
                    || Vector3.SqrMagnitude(player.Position - targetPosition) > 0.25f)
                {
                    if (IsCurrentRealPlayer(player, expectedFullUserId))
                    {
                        player.Position = oldPosition;
                    }

                    return ControlResult.Reject(ControlResultCode.PlayerUnavailable, destinationId);
                }

                return ControlResult.Success(destinationId);
            }
            catch
            {
                if (IsCurrentRealPlayer(player, expectedFullUserId))
                {
                    player.Position = oldPosition;
                }

                return ControlResult.Reject(ControlResultCode.PlayerUnavailable, destinationId);
            }
        }
    }

    public bool IsArenaPresetAvailable(Player player, string expectedFullUserId, string presetId) =>
        IsCurrentRealPlayer(player, expectedFullUserId)
        && IsWarmupActive
        && (controlsConfig.Presets ?? new List<ArenaPresetConfig>()).Any(
            preset => preset != null
                && !string.IsNullOrWhiteSpace(preset.Id)
                && string.Equals(preset.Id, presetId, StringComparison.Ordinal));

    public ControlResult TryActivateArenaPreset(Player player, string expectedFullUserId, string presetId)
    {
        if (!TryEnterCurrent(player, expectedFullUserId, out IDisposable? lease, out ControlResult rejected)
            || lease == null)
        {
            return rejected;
        }

        using (lease)
        {
            if (!IsWarmupActive)
            {
                return ControlResult.Reject(ControlResultCode.WarmupInactive, presetId);
            }

            ArenaPresetConfig[] exactMatches = (controlsConfig.Presets ?? new List<ArenaPresetConfig>())
                .Where(preset => preset != null
                    && string.Equals(preset.Id, presetId, StringComparison.Ordinal))
                .ToArray();
            if (exactMatches.Length != 1)
            {
                return ControlResult.Reject(ControlResultCode.InvalidRequest, presetId);
            }

            return global::SCPSLBot.Warmup.WarmupManager.Instance.TrySetPlayerArena(
                    player.PlayerId,
                    exactMatches[0].Id,
                    out string response)
                ? ControlResult.Success(exactMatches[0].Id)
                : ControlResult.Reject(ControlResultCode.ArenaSwitchRejected, response);
        }
    }

    public bool IsBotDiagnosticsEnabled(Player player) =>
        IsCurrentRealPlayer(player)
        && presentation.IsBotDiagnosticsEnabled(player);

    public ControlResult TrySetBotDiagnostics(Player player, string expectedFullUserId, bool enabled)
    {
        if (!TryEnterCurrent(player, expectedFullUserId, out IDisposable? lease, out ControlResult rejected)
            || lease == null)
        {
            return rejected;
        }

        using (lease)
        {
            if (!HasPermission(player, PlayerPermissions.GameplayData))
            {
                return ControlResult.Reject(ControlResultCode.PermissionDenied);
            }

            presentation.SetBotDiagnosticsEnabled(player, enabled);
            return presentation.IsBotDiagnosticsEnabled(player) == enabled
                ? ControlResult.Success(enabled ? "on" : "off")
                : ControlResult.Reject(ControlResultCode.PlayerUnavailable);
        }
    }

    public bool IsNavAuthoringEnabled(Player player) =>
        IsCurrentRealPlayer(player)
        && IsSamePlayer(NavigationMeshEditor.Instance.PlayerEditing, player);

    public ControlResult TrySetNavAuthoring(Player player, string expectedFullUserId, bool enabled)
    {
        if (!TryEnterCurrent(player, expectedFullUserId, out IDisposable? lease, out ControlResult rejected)
            || lease == null)
        {
            return rejected;
        }

        using (lease)
        {
            if (!HasPermission(player, PlayerPermissions.ServerConfigs))
            {
                return ControlResult.Reject(ControlResultCode.PermissionDenied);
            }

            Player? current = NavigationMeshEditor.Instance.PlayerEditing;
            if (enabled)
            {
                if (current != null && !current.IsDestroyed && !IsSamePlayer(current, player))
                {
                    return ControlResult.Reject(ControlResultCode.ConcurrentRequest);
                }

                NavigationMeshEditor.Instance.PlayerEditing = player;
                presentation.SetNavDiagnosticsEnabled(player, true);
                return IsSamePlayer(NavigationMeshEditor.Instance.PlayerEditing, player)
                    ? ControlResult.Success("on")
                    : ControlResult.Reject(ControlResultCode.PlayerUnavailable);
            }

            if (current != null && !IsSamePlayer(current, player))
            {
                return ControlResult.Reject(ControlResultCode.ConcurrentRequest);
            }

            NavigationMeshEditor.Instance.PlayerEditing = null;
            presentation.SetNavDiagnosticsEnabled(player, false);
            return NavigationMeshEditor.Instance.PlayerEditing == null
                ? ControlResult.Success("off")
                : ControlResult.Reject(ControlResultCode.PlayerUnavailable);
        }
    }

    private Dictionary<string, WarmupLoadoutConfig> ValidateLoadouts(
        IEnumerable<WarmupLoadoutConfig>? configured)
    {
        var valid = new Dictionary<string, WarmupLoadoutConfig>(StringComparer.Ordinal);
        foreach (WarmupLoadoutConfig? config in configured ?? Array.Empty<WarmupLoadoutConfig>())
        {
            if (config == null
                || string.IsNullOrWhiteSpace(config.Id)
                || valid.ContainsKey(config.Id)
                || config.ItemIds == null
                || config.ItemIds.Count == 0
                || config.ItemIds.Any(id => !Enum.TryParse(id, true, out ItemType parsed) || parsed == ItemType.None)
                || (config.AllowedRoleIds ?? new List<string>()).Any(
                    id => !Enum.TryParse(id, true, out RoleTypeId parsed)
                        || parsed is RoleTypeId.None or RoleTypeId.Destroyed)
                || (config.AllowedZoneIds ?? new List<string>()).Any(
                    id => !Enum.TryParse(id, true, out FacilityZone _)))
            {
                LabLogger.Warn($"[SCPSLBot] Ignoring invalid or duplicate warmup loadout '{config?.Id ?? "<null>"}'.");
                continue;
            }

            valid.Add(config.Id, config);
        }

        return valid;
    }

    private bool TryResolveLoadout(Player player, WarmupLoadoutConfig config, out ItemType[] exactItems)
    {
        exactItems = Array.Empty<ItemType>();
        if (!IsCurrentRealPlayer(player)
            || !IsWarmupActive
            || !player.IsAlive
            || player.Role == RoleTypeId.Spectator)
        {
            return false;
        }

        string role = player.Role.ToString();
        string zone = player.Zone.ToString();
        if ((config.AllowedRoleIds ?? new List<string>()).Count > 0
            && !config.AllowedRoleIds.Contains(role, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        if ((config.AllowedZoneIds ?? new List<string>()).Count > 0
            && !config.AllowedZoneIds.Contains(zone, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        ItemType[] parsed = (config.ItemIds ?? new List<string>())
            .Select(id => Enum.TryParse(id, true, out ItemType item) ? item : ItemType.None)
            .ToArray();
        if (parsed.Length == 0 || parsed.Any(item => item == ItemType.None))
        {
            return false;
        }

        int currentCount = player.Items.Count();
        if (currentCount + parsed.Length > InventorySystem.Inventory.MaxSlots)
        {
            return false;
        }

        exactItems = parsed;
        return true;
    }

    private bool TryEnterCurrent(
        Player player,
        string expectedFullUserId,
        out IDisposable? lease,
        out ControlResult rejected)
    {
        lease = null;
        if (!IsCurrentRealPlayer(player, expectedFullUserId))
        {
            rejected = ControlResult.Reject(ControlResultCode.InvalidRequest);
            return false;
        }

        if (!requestGuard.TryEnter(expectedFullUserId, out lease) || lease == null)
        {
            rejected = ControlResult.Reject(ControlResultCode.ConcurrentRequest);
            return false;
        }

        if (!IsCurrentRealPlayer(player, expectedFullUserId))
        {
            lease.Dispose();
            lease = null;
            rejected = ControlResult.Reject(ControlResultCode.PlayerUnavailable);
            return false;
        }

        rejected = ControlResult.Success();
        return true;
    }

    private static bool IsTeleportTarget(Player candidate, Player requester, string requesterUserId)
    {
        try
        {
            return IsCurrentRealPlayer(candidate)
                && candidate.IsAlive
                && candidate.Role != RoleTypeId.Spectator
                && !IsSamePlayer(candidate, requester)
                && !string.Equals(candidate.UserId, requesterUserId, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsCurrentRealPlayer(Player? player)
    {
        try
        {
            return player != null
                && !player.IsDestroyed
                && player.IsReady
                && player.IsPlayer
                && !player.IsDummy
                && !player.IsHost
                && !ManagedBotIdentity.IsManaged(player)
                && !string.IsNullOrWhiteSpace(player.UserId);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsCurrentRealPlayer(Player? player, string expectedFullUserId)
    {
        try
        {
            return IsCurrentRealPlayer(player)
                && !string.IsNullOrWhiteSpace(expectedFullUserId)
                && string.Equals(player!.UserId, expectedFullUserId, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static bool HasPermission(Player? player, PlayerPermissions permission)
    {
        try
        {
            return player != null && !player.IsDestroyed && player.HasPermission(permission);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsSamePlayer(Player? first, Player? second)
    {
        try
        {
            return first != null
                && second != null
                && !first.IsDestroyed
                && !second.IsDestroyed
                && first.PlayerId == second.PlayerId
                && string.Equals(first.UserId, second.UserId, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static void RollBackItems(Player player, IEnumerable<Item> granted)
    {
        if (player == null || player.IsDestroyed)
        {
            return;
        }

        foreach (Item item in granted.Reverse())
        {
            try
            {
                player.RemoveItem(item);
            }
            catch (Exception exception)
            {
                LabLogger.Error($"[SCPSLBot] Failed to roll back loadout item {item.Type}: {exception.GetBaseException().Message}");
            }
        }
    }

    private static bool SafeBool(Func<bool> callback)
    {
        try
        {
            return callback();
        }
        catch
        {
            return false;
        }
    }

    private static string SafeString(Func<string> callback)
    {
        try
        {
            return callback() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}
