#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.Handlers;
using LabApi.Features.Console;
using LabApi.Features.Wrappers;
using MEC;
using PlayerRoles;
using RemoteAdmin;
using SCPSLBot.Api;
using SCPSLBot.Warmup.Controls.Policy;
using ServerKeybinds;

namespace SCPSLBot.Warmup.Controls.Panel;

/// <summary>
/// Lifecycle owner for SCPSLBot's personalized warmup and Tools SSS blocks. Candidate builders are
/// side-effect free. Role, item, and arena dropdowns only stage a choice; explicit native buttons
/// execute it, and every mutation revalidates its live player, full UserId, option, and permission.
/// </summary>
public sealed class WarmupSssController
{
    private const PlayerPermissions NoPermission = (PlayerPermissions)0;

    private const int RespawnAsLocalId = 1;
    private const int ApplyRoleLocalId = 2;
    private const int RequestItemLocalId = 3;
    private const int TeleportToLocalId = 4;
    private const int ArenaPresetLocalId = 5;
    private const int GrantItemLocalId = 6;
    private const int ApplyArenaLocalId = 7;

    private const int ForceRoleLocalId = 1;
    private const int BotDiagnosticsLocalId = 2;
    private const int NavAuthoringLocalId = 3;

    private readonly WarmupControlsConfig controlsConfig;
    private readonly WarmupPanelConfig panelConfig;
    private readonly LabApiRoleControlService roles;
    private readonly LabApiItemGrantService? items;
    private readonly IWarmupPanelActions actions;
    private readonly PerUserActionRateLimiter actionRateLimiter;
    private readonly PendingPanelSelectionStore pendingSelections = new();
    private readonly KeybindBlock warmupBlock;
    private readonly KeybindBlock toolsBlock;
    private readonly Dictionary<int, Player> realPlayers = new();
    private readonly Dictionary<CooldownRefreshKey, CoroutineHandle> cooldownRefreshes = new();
    private bool enabled;

    public WarmupSssController(
        WarmupControlsConfig controlsConfig,
        WarmupPanelConfig panelConfig,
        LabApiRoleControlService roles,
        LabApiItemGrantService? items,
        IWarmupPanelActions actions)
    {
        this.controlsConfig = controlsConfig ?? throw new ArgumentNullException(nameof(controlsConfig));
        this.panelConfig = panelConfig ?? throw new ArgumentNullException(nameof(panelConfig));
        this.roles = roles ?? throw new ArgumentNullException(nameof(roles));
        this.items = items;
        this.actions = actions ?? throw new ArgumentNullException(nameof(actions));
        actionRateLimiter = new PerUserActionRateLimiter();

        warmupBlock = KeybindRegistry
            .ClaimBlock(SssIdBlocks.ScpslBotWarmup, "SCPSLBot.Warmup")
            .Header(GlobalText("Warmup controls", "热身控制"))
            .InCategory(SettingsCategory.Gameplay)
            .Order(-100)
            .AddDropdownForPlayer(
                RespawnAsLocalId,
                BuildRespawnModel,
                OnRespawnChanged,
                onAcquired: OnRespawnChanged)
            .AddButtonForPlayer(ApplyRoleLocalId, BuildApplyRoleModel, OnApplyRolePressed)
            .AddDropdownForPlayer(
                RequestItemLocalId,
                BuildItemModel,
                OnItemChanged,
                onAcquired: OnItemChanged)
            .AddButtonForPlayer(GrantItemLocalId, BuildGrantItemModel, OnGrantItemPressed)
            .AddDropdownForPlayer(TeleportToLocalId, BuildTeleportModel, OnTeleportChanged)
            .AddDropdownForPlayer(
                ArenaPresetLocalId,
                BuildArenaPresetModel,
                OnArenaPresetChanged,
                onAcquired: OnArenaPresetChanged)
            .AddButtonForPlayer(ApplyArenaLocalId, BuildApplyArenaModel, OnApplyArenaPressed);

        toolsBlock = KeybindRegistry
            .ClaimBlock(SssIdBlocks.ScpslBotTools, "SCPSLBot.Tools")
            .Header(GlobalText("SCPSLBot tools", "SCPSLBot 管理工具"))
            .InCategory(SettingsCategory.Tools)
            .Order(-100)
            .VisibleTo(HasAnyToolPermission)
            .AddDropdownForPlayer(ForceRoleLocalId, BuildForceRoleModel, OnForceRoleChanged)
            .AddDropdownForPlayer(BotDiagnosticsLocalId, BuildBotDiagnosticsModel, OnBotDiagnosticsChanged)
            .AddDropdownForPlayer(NavAuthoringLocalId, BuildNavAuthoringModel, OnNavAuthoringChanged);
    }

    public bool IsEnabled => enabled;

    public void Enable()
    {
        if (enabled || !panelConfig.Enabled)
        {
            return;
        }

        enabled = true;
        Subscribe();
        CaptureCurrentRealPlayers();

        try
        {
            warmupBlock.Enable();
            // Debug and authoring controls are RA-only. Never expose the Tools block in the
            // player-facing Server-Specific Settings menu, including to staff players.
        }
        catch
        {
            DisableBlockSafely(toolsBlock, "Tools");
            DisableBlockSafely(warmupBlock, "Warmup");

            Unsubscribe();
            realPlayers.Clear();
            enabled = false;
            throw;
        }
    }

    public void Disable()
    {
        if (!enabled)
        {
            return;
        }

        enabled = false;
        Unsubscribe();
        CancelCooldownRefreshes();
        actionRateLimiter.Clear();
        pendingSelections.ClearAll();
        realPlayers.Clear();

        // Disable both regardless of live config mutation; Disable is idempotent for an inactive block.
        DisableBlockSafely(toolsBlock, "Tools");
        DisableBlockSafely(warmupBlock, "Warmup");
    }

    /// <summary>Targeted integration hook for language, zone, permission, or display changes.</summary>
    public void NotifyPlayerStateChanged(Player player, SssInterest changed, string reason)
    {
        if (enabled && IsRealPlayer(player))
        {
            KeybindRegistry.InvalidatePlayer(player, changed, reason);
        }
    }

    /// <summary>Call after an out-of-band preset change; every real player's role/item view is affected.</summary>
    public void NotifyArenaPresetChanged(string reason = "warmup-preset")
    {
        if (!enabled)
        {
            return;
        }

        foreach (Player player in CurrentRealPlayers())
        {
            KeybindRegistry.InvalidatePlayer(
                player,
                SssInterest.WarmupMode | SssInterest.Role | SssInterest.Item | SssInterest.Zone,
                reason);
        }
    }

    /// <summary>
    /// Reconciles the real-player set and routes only the 1-to-2 or 2-to-1 global-authority boundary.
    /// Managed bots, dummies, NPCs, unverified clients, and the host never contribute to the count.
    /// </summary>
    public void NotifyPopulationChanged(string reason = "real-population-boundary")
    {
        if (!enabled)
        {
            return;
        }

        Player[] before = realPlayers.Values.ToArray();
        Player[] after = CurrentRealPlayers();
        realPlayers.Clear();
        foreach (Player player in after)
        {
            realPlayers[player.PlayerId] = player;
            KeybindRegistry.SetPlayerInterests(player, SssInterest.All);
        }

        KeybindRegistry.InvalidatePopulationBoundary(before, after, reason);
    }

    private void Subscribe()
    {
        PlayerEvents.Joined += OnJoined;
        PlayerEvents.Left += OnLeft;
        PlayerEvents.Death += OnDeath;
        PlayerEvents.ChangedRole += OnChangedRole;
        PlayerEvents.ChangedItem += OnChangedItem;
        PlayerEvents.PickedUpItem += OnPickedUpItem;
        PlayerEvents.DroppedItem += OnDroppedItem;
    }

    private void Unsubscribe()
    {
        PlayerEvents.Joined -= OnJoined;
        PlayerEvents.Left -= OnLeft;
        PlayerEvents.Death -= OnDeath;
        PlayerEvents.ChangedRole -= OnChangedRole;
        PlayerEvents.ChangedItem -= OnChangedItem;
        PlayerEvents.PickedUpItem -= OnPickedUpItem;
        PlayerEvents.DroppedItem -= OnDroppedItem;
    }

    private void CaptureCurrentRealPlayers()
    {
        realPlayers.Clear();
        foreach (Player player in CurrentRealPlayers())
        {
            realPlayers[player.PlayerId] = player;
            KeybindRegistry.SetPlayerInterests(player, SssInterest.All);
        }
    }

    private void OnJoined(PlayerJoinedEventArgs ev)
    {
        if (!enabled || !IsRealPlayer(ev.Player))
        {
            return;
        }

        Player[] before = realPlayers.Values.ToArray();
        realPlayers[ev.Player.PlayerId] = ev.Player;
        KeybindRegistry.SetPlayerInterests(ev.Player, SssInterest.All);
        KeybindRegistry.InvalidatePopulationBoundary(before, realPlayers.Values.ToArray(), "real-player-joined");
    }

    private void OnLeft(PlayerLeftEventArgs ev)
    {
        if (!enabled)
        {
            return;
        }

        Player[] before = realPlayers.Values.ToArray();
        try
        {
            actionRateLimiter.Forget(ev.Player.UserId ?? string.Empty);
        }
        catch
        {
            // The disconnected wrapper may already be destroyed; Disable clears any remainder.
        }
        pendingSelections.ForgetPlayer(ev.Player.PlayerId);
        if (!realPlayers.Remove(ev.Player.PlayerId))
        {
            return;
        }

        KeybindRegistry.InvalidatePopulationBoundary(before, realPlayers.Values.ToArray(), "real-player-left");
    }

    private void OnDeath(PlayerDeathEventArgs ev)
    {
        // A personalized dropdown can remain visibly selected across this refresh. Keep its stable
        // server-side value; the eventual button press still revalidates the new life and role.
        InvalidatePersonal(ev.Player, SssInterest.Role | SssInterest.Item | SssInterest.Zone, "player-death");
    }

    private void OnChangedRole(PlayerChangedRoleEventArgs ev)
    {
        // Preserve visible staged choices across role-driven refreshes. Intrinsic role/item/zone
        // authority is rechecked at execution, so retaining intent does not bypass policy.
        InvalidatePersonal(ev.Player, SssInterest.Role | SssInterest.Item | SssInterest.Zone, "role-changed");
    }

    private void OnChangedItem(PlayerChangedItemEventArgs ev) =>
        InvalidatePersonal(ev.Player, SssInterest.Item, "held-item-changed");

    private void OnPickedUpItem(PlayerPickedUpItemEventArgs ev) =>
        InvalidatePersonal(ev.Player, SssInterest.Item, "item-picked-up");

    private void OnDroppedItem(PlayerDroppedItemEventArgs ev) =>
        InvalidatePersonal(ev.Player, SssInterest.Item, "item-dropped");

    private void InvalidatePersonal(Player player, SssInterest interest, string reason)
    {
        if (enabled && IsRealPlayer(player))
        {
            KeybindRegistry.InvalidatePlayer(player, interest, reason);
        }
    }

    private DropdownModel BuildRespawnModel(Player player)
    {
        if (!TryGetIdentity(player, out _))
        {
            return DropdownModel.Hidden;
        }

        RoleTypeId[] exactRoles = roles.GetConfiguredRoles(player, RoleControlSurface.Regular).ToArray();
        return BuildExactRoleModel(player, exactRoles, "Respawn as", "复活为");
    }

    private DropdownModel BuildForceRoleModel(Player player)
    {
        if (!TryGetIdentity(player, out _) || !HasPermission(player, PlayerPermissions.PlayersManagement))
        {
            return DropdownModel.Hidden;
        }

        RoleTypeId[] exactRoles = roles.GetConfiguredRoles(player, RoleControlSurface.AdminForce).ToArray();
        return BuildExactRoleModel(player, exactRoles, "Force role", "强制角色");
    }

    private DropdownModel BuildExactRoleModel(
        Player player,
        IReadOnlyCollection<RoleTypeId> exactRoles,
        string englishLabel,
        string chineseLabel)
    {
        if (exactRoles.Count == 0)
        {
            return DropdownModel.Hidden;
        }

        string[] options = new[] { SelectText(player) }
            .Concat(exactRoles.Select(role => role.ToString()))
            .ToArray();
        return new DropdownModel(
            Text(player, englishLabel, chineseLabel),
            options,
            hint: Text(
                player,
                "Select the exact role, then press Apply; the server never substitutes a fallback.",
                "选择精确角色后点击应用；服务器绝不会替换为其他角色。"));
    }

    private ButtonModel BuildApplyRoleModel(Player player)
    {
        if (!TryGetIdentity(player, out _))
        {
            return ButtonModel.Hidden;
        }

        return new ButtonModel(
            Text(player, "Role action", "角色操作"),
            Text(player, "Apply", "应用"),
            hint: Text(
                player,
                "Select a role above, then press Apply.",
                "先在上方选择角色，再点击应用。"));
    }

    private DropdownModel BuildItemModel(Player player)
    {
        if (items == null || !TryGetIdentity(player, out _))
        {
            return DropdownModel.Hidden;
        }

        if (!actions.IsWarmupActive || !player.IsAlive)
        {
            return DropdownModel.Hidden;
        }

        // Keep the full catalog in stable StableId order. TryGrant performs the live role, zone,
        // capacity, cooldown, and limit checks; dynamic removal would remap stale numeric indices.
        ItemCatalogEntry[] entries = items.Entries.Values
            .OrderBy(entry => entry.StableId, StringComparer.Ordinal)
            .ToArray();
        if (entries.Length == 0)
        {
            return DropdownModel.Hidden;
        }

        string[] options = new[] { SelectText(player) }
            .Concat(entries.Select(entry => ChoiceDisplay(player, ItemChoice(entry))))
            .ToArray();
        return new DropdownModel(
            Text(player, "Request item", "请求物品"),
            options,
            hint: Text(
                player,
                "Select an item, then press Grant; cooldowns and limits are checked at that time.",
                "选择物品后点击发放；服务器会在发放时检查冷却与次数限制。"));
    }

    private ButtonModel BuildGrantItemModel(Player player)
    {
        if (items == null
            || !TryGetIdentity(player, out _)
            || !actions.IsWarmupActive
            || !player.IsAlive)
        {
            return ButtonModel.Hidden;
        }

        return new ButtonModel(
            Text(player, "Item action", "物品操作"),
            Text(player, "Grant", "发放"),
            hint: Text(
                player,
                "Select an item above, then press Grant.",
                "先在上方选择物品，再点击发放。"));
    }

    private DropdownModel BuildTeleportModel(Player player)
    {
        if (!TryGetIdentity(player, out string userId))
        {
            return DropdownModel.Hidden;
        }

        IReadOnlyList<WarmupPanelChoice> choices = SafeChoices(
            () => actions.GetAvailableTeleportDestinations(player, userId));
        return BuildChoiceModel(player, choices, "Teleport to", "传送到", "Teleport to an available warmup destination.", "传送到当前可用的热身地点。");
    }

    private DropdownModel BuildArenaPresetModel(Player player)
    {
        if (!panelConfig.ShowArenaPreset
            || !TryGetIdentity(player, out string userId))
        {
            return DropdownModel.Hidden;
        }

        string active = actions.GetActiveArenaPresetId(player, userId) ?? string.Empty;
        ArenaPresetConfig[] presets = GetAvailableArenaPresets(player, userId);
        if (presets.Length == 0)
        {
            return DropdownModel.Hidden;
        }

        int activeIndex = Array.FindIndex(
            presets,
            preset => string.Equals(preset.Id, active, StringComparison.OrdinalIgnoreCase));
        return new DropdownModel(
            Text(player, "Arena preset", "竞技场预设"),
            presets.Select(preset => ChoiceDisplay(player, PresetChoice(preset))),
            activeIndex < 0 ? 0 : activeIndex,
            hint: Text(
                player,
                "Select a zone, then press Apply; this moves you and refreshes the menu.",
                "选择区域后点击应用；系统会移动你并刷新菜单。"));
    }

    private ButtonModel BuildApplyArenaModel(Player player)
    {
        if (!panelConfig.ShowArenaPreset
            || !TryGetIdentity(player, out string userId)
            || GetAvailableArenaPresets(player, userId).Length == 0)
        {
            return ButtonModel.Hidden;
        }

        return new ButtonModel(
            Text(player, "Zone action", "区域操作"),
            Text(player, "Apply", "应用"),
            hint: Text(
                player,
                "Select a zone above, then press Apply. Applying refreshes this menu.",
                "先在上方选择区域，再点击应用；应用后会刷新此菜单。"));
    }

    private DropdownModel BuildBotDiagnosticsModel(Player player)
    {
        if (!TryGetIdentity(player, out _) || !HasPermission(player, PlayerPermissions.GameplayData))
        {
            return DropdownModel.Hidden;
        }

        return BuildToggleModel(
            player,
            "Bot diagnostics",
            "机器人诊断",
            SafeBool(() => actions.IsBotDiagnosticsEnabled(player)));
    }

    private DropdownModel BuildNavAuthoringModel(Player player)
    {
        if (!TryGetIdentity(player, out _) || !HasPermission(player, PlayerPermissions.ServerConfigs))
        {
            return DropdownModel.Hidden;
        }

        return BuildToggleModel(
            player,
            "Navigation authoring",
            "导航编辑",
            SafeBool(() => actions.IsNavAuthoringEnabled(player)));
    }

    private DropdownModel BuildToggleModel(Player player, string englishLabel, string chineseLabel, bool on)
    {
        string off = Text(player, "Off", "关闭");
        string enabledText = Text(player, "On", "开启");
        return new DropdownModel(
            Text(player, englishLabel, chineseLabel),
            new[] { off, enabledText },
            on ? 1 : 0);
    }

    private DropdownModel BuildChoiceModel(
        Player player,
        IReadOnlyList<WarmupPanelChoice> choices,
        string englishLabel,
        string chineseLabel,
        string englishHint,
        string chineseHint)
    {
        if (choices.Count == 0)
        {
            return DropdownModel.Hidden;
        }

        string[] displays = choices
            .Select(choice => ChoiceDisplay(player, choice))
            .ToArray();
        return new DropdownModel(
            Text(player, englishLabel, chineseLabel),
            new[] { SelectText(player) }.Concat(displays),
            hint: Text(player, englishHint, chineseHint));
    }

    private void OnRespawnChanged(Player player, DropdownSelection selection)
    {
        if (!TryGetIdentity(player, out string userId))
        {
            return;
        }

        if (IsSelectValue(player, selection.Value))
        {
            pendingSelections.Clear(player.PlayerId, userId, PendingPanelAction.Role);
            return;
        }

        IReadOnlyList<RoleTypeId> current = roles.GetEligibleRoles(player, RoleControlSurface.Regular);
        if (!Enum.TryParse(selection.Value, true, out RoleTypeId exactRole)
            || !current.Contains(exactRole))
        {
            pendingSelections.Clear(player.PlayerId, userId, PendingPanelAction.Role);
            RejectStale(player);
            return;
        }

        pendingSelections.Stage(player.PlayerId, userId, PendingPanelAction.Role, exactRole.ToString());
    }

    private void OnApplyRolePressed(Player player)
    {
        if (!TryGetIdentity(player, out string expectedUserId)
            || !pendingSelections.TryGet(
                player.PlayerId,
                expectedUserId,
                PendingPanelAction.Role,
                out string roleId))
        {
            SendFeedback(player, Text(player, "Select a role first.", "请先选择角色。"));
            return;
        }

        if (!TryAuthorizeCallback(player, NoPermission, out string userId))
        {
            return;
        }

        IReadOnlyList<RoleTypeId> current = roles.GetEligibleRoles(player, RoleControlSurface.Regular);
        if (!Enum.TryParse(roleId, true, out RoleTypeId exactRole)
            || !current.Contains(exactRole))
        {
            pendingSelections.Clear(player.PlayerId, userId, PendingPanelAction.Role);
            RejectStale(player);
            return;
        }

        ExecuteExactRole(player, userId, exactRole, RoleControlSurface.Regular, "respawn-role");
    }

    private void OnForceRoleChanged(Player player, DropdownSelection selection) =>
        ExecuteImmediateExactRole(
            player,
            selection,
            RoleControlSurface.AdminForce,
            PlayerPermissions.PlayersManagement,
            "force-role");

    private void ExecuteImmediateExactRole(
        Player player,
        DropdownSelection selection,
        RoleControlSurface surface,
        PlayerPermissions requiredPermission,
        string reason)
    {
        if (IsSelectValue(player, selection.Value))
        {
            return;
        }

        if (!TryAuthorizeCallback(player, requiredPermission, out string userId))
        {
            return;
        }

        IReadOnlyList<RoleTypeId> current = roles.GetEligibleRoles(player, surface);
        if (!Enum.TryParse(selection.Value, true, out RoleTypeId exactRole)
            || !current.Contains(exactRole))
        {
            RejectStale(player);
            return;
        }

        ExecuteExactRole(player, userId, exactRole, surface, reason);
    }

    private void ExecuteExactRole(
        Player player,
        string userId,
        RoleTypeId exactRole,
        RoleControlSurface surface,
        string reason)
    {
        WarmupManager? warmup = null;
        PlayerRoleArenaTransition transition = default;
        if (surface == RoleControlSurface.Regular)
        {
            warmup = global::SCPSLBot.Warmup.WarmupManager.Instance;
            if (!warmup.TryPreparePlayerRoleChange(player, exactRole, out transition))
            {
                SendResult(player, ControlResult.Reject(ControlResultCode.PlayerUnavailable));
                return;
            }
        }

        ControlResult result = roles.TryChangeRole(player, userId, exactRole, surface);
        if (warmup != null)
        {
            if (result.Succeeded)
            {
                warmup.CompletePlayerRoleChange(player, exactRole, transition);
            }
            else
            {
                warmup.RestorePlayerArena(transition);
            }
        }

        SendResult(player, result);
        if (result.Succeeded)
        {
            Logger.Info(
                $"[SCPSLBot] SSS {reason}: {userId} -> {exactRole}; " +
                $"originArena={transition.PreviousArena}, targetArena={transition.TargetArena}, " +
                $"relocateFromSurface={transition.RelocateFromSurface}, finalZone={player.Zone}, " +
                $"finalPosition={player.Position}.");
            KeybindRegistry.InvalidatePlayer(
                player,
                SssInterest.Role | SssInterest.Zone | SssInterest.Item,
                reason);
        }
    }

    private void OnItemChanged(Player player, DropdownSelection selection)
    {
        if (items == null || !TryGetIdentity(player, out string userId))
        {
            return;
        }

        if (IsSelectValue(player, selection.Value))
        {
            pendingSelections.Clear(player.PlayerId, userId, PendingPanelAction.Item);
            return;
        }

        ItemCatalogEntry? selected = items.Entries.Values
            .SingleOrDefault(entry => string.Equals(
                ChoiceDisplay(player, ItemChoice(entry)),
                selection.Value,
                StringComparison.Ordinal));
        if (selected == null)
        {
            pendingSelections.Clear(player.PlayerId, userId, PendingPanelAction.Item);
            RejectStale(player);
            return;
        }

        pendingSelections.Stage(
            player.PlayerId,
            userId,
            PendingPanelAction.Item,
            selected.StableId);
    }

    private void OnGrantItemPressed(Player player)
    {
        if (items == null || !TryGetIdentity(player, out string expectedUserId))
        {
            return;
        }

        if (!pendingSelections.TryGet(
                player.PlayerId,
                expectedUserId,
                PendingPanelAction.Item,
                out string stableId))
        {
            SendFeedback(player, Text(player, "Select an item first.", "请先选择物品。"));
            return;
        }

        if (!TryAuthorizeCallback(player, NoPermission, out string userId))
        {
            return;
        }

        if (!items.Entries.ContainsKey(stableId))
        {
            pendingSelections.Clear(player.PlayerId, userId, PendingPanelAction.Item);
            RejectStale(player);
            return;
        }

        ControlResult result = items.TryGrant(player, userId, stableId);
        SendResult(player, result);
        KeybindRegistry.InvalidatePlayer(player, SssInterest.Item | SssInterest.Cooldown, "item-requested");
        if (result.Succeeded)
        {
            if (items.Entries.TryGetValue(stableId, out ItemCatalogEntry? entry))
            {
                ScheduleCooldownRefresh(userId, stableId, entry);
            }
        }
    }

    private void OnTeleportChanged(Player player, DropdownSelection selection)
    {
        if (!TryResolveChoice(
                player,
                selection.Value,
                userId => actions.GetAvailableTeleportDestinations(player, userId),
                out string userId,
                out string choiceId))
        {
            return;
        }

        ControlResult result = SafeAction(
            () => actions.TryTeleport(player, userId, choiceId),
            choiceId);
        SendResult(player, result);
        if (result.Succeeded)
        {
            KeybindRegistry.InvalidatePlayer(
                player,
                SssInterest.Zone | SssInterest.Role | SssInterest.Item,
                "warmup-teleport");
        }
    }

    private void OnArenaPresetChanged(Player player, DropdownSelection selection)
    {
        if (!TryGetIdentity(player, out string userId))
        {
            return;
        }

        ArenaPresetConfig[] matchingPresets = GetAvailableArenaPresets(player, userId)
            .Where(candidate => string.Equals(
                ChoiceDisplay(player, PresetChoice(candidate)),
                selection.Value,
                StringComparison.Ordinal))
            .ToArray();
        if (matchingPresets.Length != 1)
        {
            pendingSelections.Clear(player.PlayerId, userId, PendingPanelAction.Arena);
            RejectStale(player);
            return;
        }

        pendingSelections.Stage(
            player.PlayerId,
            userId,
            PendingPanelAction.Arena,
            matchingPresets[0].Id);
    }

    private void OnApplyArenaPressed(Player player)
    {
        if (!TryGetIdentity(player, out string expectedUserId))
        {
            return;
        }

        string presetId;
        if (!pendingSelections.TryGet(
                player.PlayerId,
                expectedUserId,
                PendingPanelAction.Arena,
                out presetId))
        {
            // The active zone is the dropdown's initial visible value. Let Apply deliberately
            // re-apply it (including exact-Spectator recovery) even when no change callback fired.
            presetId = actions.GetActiveArenaPresetId(player, expectedUserId) ?? string.Empty;
        }

        if (!TryAuthorizeCallback(player, NoPermission, out string userId))
        {
            return;
        }

        ArenaPresetConfig[] current = GetAvailableArenaPresets(player, userId)
            .Where(candidate => string.Equals(candidate.Id, presetId, StringComparison.Ordinal))
            .ToArray();
        if (current.Length != 1)
        {
            pendingSelections.Clear(player.PlayerId, userId, PendingPanelAction.Arena);
            RejectStale(player);
            return;
        }

        ControlResult result = SafeAction(
            () => actions.TryActivateArenaPreset(player, userId, presetId),
            presetId);
        SendResult(player, result);
        if (result.Succeeded)
        {
            Logger.Info($"[SCPSLBot] SSS arena preset: {userId} -> {presetId}.");
            KeybindRegistry.InvalidatePlayer(
                player,
                SssInterest.WarmupMode | SssInterest.Role | SssInterest.Item | SssInterest.Zone,
                "arena-preset-applied");
        }
    }

    private ArenaPresetConfig[] GetAvailableArenaPresets(Player player, string userId) =>
        (controlsConfig.Presets ?? new List<ArenaPresetConfig>())
        .Where(preset => preset != null && !string.IsNullOrWhiteSpace(preset.Id))
        .GroupBy(preset => preset.Id, StringComparer.Ordinal)
        .Select(group => group.First())
        .Where(preset => SafeBool(() => actions.IsArenaPresetAvailable(player, userId, preset.Id)))
        .OrderBy(preset => ArenaOrder(preset.Id))
        .ThenBy(preset => preset.Id, StringComparer.Ordinal)
        .ToArray();

    private static int ArenaOrder(string id) => (id ?? string.Empty).ToLowerInvariant() switch
    {
        "surface" => 0,
        "pvpve" => 1,
        "lcz" => 2,
        _ => 3,
    };

    private void OnBotDiagnosticsChanged(Player player, DropdownSelection selection)
    {
        if (!TryResolveToggle(
                player,
                selection.Value,
                PlayerPermissions.GameplayData,
                out string userId,
                out bool selected))
        {
            return;
        }

        ControlResult result = SafeAction(
            () => actions.TrySetBotDiagnostics(player, userId, selected),
            selected ? "on" : "off");
        SendResult(player, result);
        if (result.Succeeded)
        {
            Logger.Info($"[SCPSLBot] SSS bot diagnostics: {userId} -> {selected}.");
            KeybindRegistry.InvalidatePlayer(player, SssInterest.Display, "bot-diagnostics");
        }
    }

    private void OnNavAuthoringChanged(Player player, DropdownSelection selection)
    {
        if (!TryResolveToggle(
                player,
                selection.Value,
                PlayerPermissions.ServerConfigs,
                out string userId,
                out bool selected))
        {
            return;
        }

        ControlResult result = SafeAction(
            () => actions.TrySetNavAuthoring(player, userId, selected),
            selected ? "on" : "off");
        SendResult(player, result);
        if (result.Succeeded)
        {
            Logger.Info($"[SCPSLBot] SSS nav authoring: {userId} -> {selected}.");
            KeybindRegistry.InvalidatePlayer(player, SssInterest.Display, "nav-authoring");
        }
    }

    private bool TryResolveChoice(
        Player player,
        string selectedDisplay,
        Func<string, IReadOnlyList<WarmupPanelChoice>> getCurrent,
        out string userId,
        out string choiceId)
    {
        choiceId = string.Empty;
        if (IsSelectValue(player, selectedDisplay))
        {
            userId = string.Empty;
            return false;
        }

        if (!TryAuthorizeCallback(player, NoPermission, out userId))
        {
            return false;
        }

        string currentUserId = userId;
        IReadOnlyList<WarmupPanelChoice> choices = SafeChoices(() => getCurrent(currentUserId));
        WarmupPanelChoice? selected = choices.SingleOrDefault(
            choice => string.Equals(ChoiceDisplay(player, choice), selectedDisplay, StringComparison.Ordinal));
        if (selected == null)
        {
            RejectStale(player);
            return false;
        }

        choiceId = selected.Id;
        return true;
    }

    private bool TryResolveToggle(
        Player player,
        string value,
        PlayerPermissions requiredPermission,
        out string userId,
        out bool selected)
    {
        selected = false;
        if (!TryAuthorizeCallback(player, requiredPermission, out userId))
        {
            return false;
        }

        if (string.Equals(value, Text(player, "On", "开启"), StringComparison.Ordinal))
        {
            selected = true;
            return true;
        }

        if (string.Equals(value, Text(player, "Off", "关闭"), StringComparison.Ordinal))
        {
            return true;
        }

        RejectStale(player);
        return false;
    }

    private bool TryAuthorizeCallback(
        Player player,
        PlayerPermissions requiredPermission,
        out string fullUserId)
    {
        if (!TryGetIdentity(player, out fullUserId))
        {
            return false;
        }

        if (requiredPermission != NoPermission && !HasPermission(player, requiredPermission))
        {
            SendPermissionDenied(player);
            return false;
        }

        // Read again after permission resolution so a stale wrapper cannot cross a disconnect/reuse edge.
        if (player.IsDestroyed || !string.Equals(player.UserId, fullUserId, StringComparison.Ordinal))
        {
            RejectStale(player);
            return false;
        }

        if (!actionRateLimiter.TryAcquire(
                fullUserId,
                Math.Max(250, panelConfig.MinimumActionIntervalMilliseconds),
                out double remainingSeconds))
        {
            SendResult(
                player,
                ControlResult.Reject(ControlResultCode.ActionRateLimited, remainingSeconds: remainingSeconds));
            return false;
        }

        return true;
    }

    private static bool HasAnyToolPermission(Player player) =>
        IsRealPlayer(player)
        && (HasPermission(player, PlayerPermissions.PlayersManagement)
            || HasPermission(player, PlayerPermissions.GameplayData)
            || HasPermission(player, PlayerPermissions.ServerConfigs));

    private static bool HasPermission(Player player, PlayerPermissions permission)
    {
        try
        {
            return IsRealPlayer(player) && player.HasPermission(permission);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetIdentity(Player player, out string fullUserId)
    {
        fullUserId = string.Empty;
        if (!IsRealPlayer(player))
        {
            return false;
        }

        try
        {
            string current = player.UserId ?? string.Empty;
            if (string.IsNullOrWhiteSpace(current))
            {
                return false;
            }

            fullUserId = current;
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static bool IsRealPlayer(Player? player)
    {
        try
        {
            return player != null
                && !player.IsDestroyed
                && player.IsReady
                && player.IsPlayer
                && !player.IsDummy
                && !player.IsHost
                && !ManagedBotIdentity.IsManaged(player);
        }
        catch
        {
            return false;
        }
    }

    private static Player[] CurrentRealPlayers() => Player.ReadyList.Where(IsRealPlayer).ToArray();

    private IReadOnlyList<WarmupPanelChoice> SafeChoices(
        Func<IReadOnlyList<WarmupPanelChoice>> resolve)
    {
        try
        {
            return (resolve() ?? Array.Empty<WarmupPanelChoice>())
                .Where(choice => choice != null && !string.IsNullOrWhiteSpace(choice.Id))
                .GroupBy(choice => choice.Id, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToArray();
        }
        catch (Exception exception)
        {
            Logger.Warn($"[SCPSLBot] Warmup SSS option resolver failed: {exception.GetBaseException().Message}");
            return Array.Empty<WarmupPanelChoice>();
        }
    }

    private static bool SafeBool(Func<bool> resolve)
    {
        try
        {
            return resolve();
        }
        catch
        {
            return false;
        }
    }

    private static ControlResult SafeAction(Func<ControlResult> execute, string detail)
    {
        try
        {
            return execute() ?? ControlResult.Reject(ControlResultCode.InvalidRequest, detail);
        }
        catch (Exception exception)
        {
            Logger.Warn($"[SCPSLBot] Warmup SSS action failed: {exception.GetBaseException().Message}");
            return ControlResult.Reject(ControlResultCode.PlayerUnavailable, detail);
        }
    }

    private string ChoiceDisplay(Player player, WarmupPanelChoice choice)
    {
        string label = IsChinese(player) ? choice.ChineseLabel : choice.EnglishLabel;
        // Stable suffix prevents two localized labels from becoming an ambiguous executable value.
        return $"{label} [{choice.Id}]";
    }

    private static WarmupPanelChoice ItemChoice(ItemCatalogEntry entry) =>
        new(entry.StableId, entry.EnglishLabel, entry.ChineseLabel);

    private static WarmupPanelChoice PresetChoice(ArenaPresetConfig preset) =>
        new(preset.Id, preset.EnglishLabel, preset.ChineseLabel);

    private bool IsSelectValue(Player player, string value) =>
        string.Equals(value, SelectText(player), StringComparison.Ordinal);

    private string SelectText(Player player) => Text(player, "Select…", "请选择…");

    private string Text(Player player, string english, string chinese) =>
        IsChinese(player) ? chinese : english;

    private string GlobalText(string english, string chinese) =>
        ControlResultLocalizer.ResolveChinese(controlsConfig.Language, null) ? chinese : english;

    private bool IsChinese(Player player)
    {
        string? clientLanguage = null;
        try
        {
            clientLanguage = actions.GetClientLanguage(player);
        }
        catch
        {
            // The project-wide fallback is Chinese when client language is unavailable.
        }

        return ControlResultLocalizer.ResolveChinese(controlsConfig.Language, clientLanguage);
    }

    private void RejectStale(Player player)
    {
        SendFeedback(
            player,
            Text(player, "That selection is no longer available. Reopen the settings panel.", "该选项已失效，请重新打开设置面板。"));
        KeybindRegistry.RequestPlayerRefresh(player, "stale-warmup-selection");
    }

    private void SendPermissionDenied(Player player) =>
        SendFeedback(player, Text(player, "You no longer have permission for that control.", "你已无权使用该控制项。"));

    private void SendResult(Player player, ControlResult result)
    {
        if (!result.Succeeded)
        {
            Logger.Warn(
                $"[SCPSLBot] SSS request rejected for player {player.PlayerId}: " +
                $"code={result.Code}, detail={result.Detail ?? "-"}.");
        }

        string? clientLanguage = null;
        try
        {
            clientLanguage = actions.GetClientLanguage(player);
        }
        catch
        {
            // Localizer falls back to Chinese.
        }

        SendFeedback(player, result.Localize(controlsConfig.Language, clientLanguage));
    }

    private void SendFeedback(Player player, string message)
    {
        if (player == null || player.IsDestroyed || string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        ushort duration = (ushort)Math.Max(1, Math.Min(30, panelConfig.FeedbackDurationSeconds));
        player.SendBroadcast(
            message,
            duration,
            global::Broadcast.BroadcastFlags.Normal,
            shouldClearPrevious: true);
    }

    private void ScheduleCooldownRefresh(string fullUserId, string stableId, ItemCatalogEntry entry)
    {
        double delaySeconds = Math.Max(entry.CooldownSeconds, entry.SharedCooldownSeconds);
        if (delaySeconds <= 0d || double.IsNaN(delaySeconds) || double.IsInfinity(delaySeconds))
        {
            return;
        }

        var key = new CooldownRefreshKey(fullUserId, stableId);
        if (cooldownRefreshes.TryGetValue(key, out CoroutineHandle previous))
        {
            Timing.KillCoroutines(previous);
        }

        float delay = (float)Math.Min(delaySeconds + 0.05d, float.MaxValue);
        CoroutineHandle handle = default;
        handle = Timing.CallDelayed(delay, () =>
        {
            if (!enabled
                || !cooldownRefreshes.TryGetValue(key, out CoroutineHandle current)
                || !current.Equals(handle))
            {
                return;
            }

            cooldownRefreshes.Remove(key);
            Player? currentPlayer = CurrentRealPlayers().FirstOrDefault(
                candidate => string.Equals(candidate.UserId, fullUserId, StringComparison.Ordinal));
            if (currentPlayer != null)
            {
                KeybindRegistry.InvalidatePlayer(currentPlayer, SssInterest.Cooldown, "item-cooldown-expired");
            }
        });
        cooldownRefreshes[key] = handle;
    }

    private void CancelCooldownRefreshes()
    {
        foreach (CoroutineHandle handle in cooldownRefreshes.Values)
        {
            Timing.KillCoroutines(handle);
        }

        cooldownRefreshes.Clear();
    }

    private static void DisableBlockSafely(KeybindBlock block, string name)
    {
        try
        {
            block.Disable();
        }
        catch (Exception exception)
        {
            Logger.Error(
                $"[SCPSLBot] Failed to disable the {name} SSS block: {exception.GetBaseException().Message}");
        }
    }

    private readonly struct CooldownRefreshKey : IEquatable<CooldownRefreshKey>
    {
        public CooldownRefreshKey(string userId, string stableId)
        {
            UserId = userId;
            StableId = stableId;
        }

        private string UserId { get; }

        private string StableId { get; }

        public bool Equals(CooldownRefreshKey other) =>
            string.Equals(UserId, other.UserId, StringComparison.Ordinal)
            && string.Equals(StableId, other.StableId, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is CooldownRefreshKey other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                return (StringComparer.Ordinal.GetHashCode(UserId) * 397)
                    ^ StringComparer.Ordinal.GetHashCode(StableId);
            }
        }
    }
}
