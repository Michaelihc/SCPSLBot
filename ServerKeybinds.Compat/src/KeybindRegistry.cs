using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using CentralAuth;
using LabApi.Features.Wrappers;
using LabApi.Loader;
using MEC;
using PlayerRoles;
using RemoteAdmin;
using RoundRestarting;
using UserSettings.ServerSpecific;
using Logger = LabApi.Features.Console.Logger;

namespace ServerKeybinds;

/// <summary>
/// The single, process-wide registry for SCP:SL Server-Specific Settings, shared by every
/// plugin on the server. It exists because <see cref="ServerSpecificSettingsSync.DefinedSettings"/> is one
/// static array shared by ALL plugins: registering settings independently (as plugins used to) means rival
/// merge paths that drop or duplicate each other's entries and risk id collisions. This type owns ONE merge
/// path, ONE pair of event subscriptions, and a fixed <see cref="SssIdBlocks"/> allocation so ids never collide.
///
/// Usage from any plugin:
/// <code>
/// var block = KeybindRegistry.ClaimBlock(SssIdBlocks.Reinforcements, "Task Force")
///     .Header("Task Force")
///     .Add(1, "Deploy SRA", KeyCode.X, "Press while equipped to deploy.", OnDeploy);
/// block.Enable();  // in Plugin.Enable
/// // block.Disable();  // in Plugin.Disable
/// </code>
///
/// API 4 retains API 3's keybinds, dropdowns, sliders, two-button toggles, and menu ordering, then adds
/// personalized regular dropdowns plus targeted, fingerprinted, budgeted refreshes. Blocks remain grouped
/// by <see cref="SettingsCategory"/> under one synthesised group header each, so
/// what the player sees no longer depends on plugin load order. This ships as a dependency LIBRARY (deployed to LabAPI's <c>dependencies/global</c>), so it loads exactly
/// once before any plugin and its statics are shared. A plugin that hard-references it but is missing the DLL
/// fails to load entirely rather than running half-broken.
/// </summary>
public static class KeybindRegistry
{
    /// <summary>
    /// What this registry currently implements, for logs and diagnostics.
    ///
    /// It is NOT a compatibility gate and nothing branches on it. Every consumer in this metarepo is built
    /// and deployed together with this assembly, so "the loaded registry might be older" is not a state
    /// that can occur; a mismatched DLL is a deployment bug and should fail loudly, not be papered over.
    /// </summary>
    public static int ApiVersion => 4;

    /// <summary>
    /// Language for the category headers this registry synthesises. An empty value or <c>cn</c> renders
    /// Chinese, <c>en</c> renders English. Everything else in the menu is authored by the consuming plugin,
    /// which localises its own strings; only these headers belong to the registry.
    ///
    /// <c>DefinedSettings</c> is one global array, so this cannot be per-player. It follows the metarepo
    /// default of falling back to Chinese. A consumer may set it from its own language config; last writer
    /// wins, so a server should not set it from more than one plugin.
    /// </summary>
    public static string Language { get; set; } = string.Empty;

    /// <summary>
    /// Gates the per-send audit log line (one <c>Logger.Debug</c> per personalized settings send, tagged
    /// with the reason) and the join-send skip diagnostics. Like <see cref="Language"/>, a consumer sets
    /// it from its own config; last writer wins.
    /// </summary>
    public static bool Debug { get; set; }

    /// <summary>
    /// Gates the press-trace log lines emitted at every keybind/value routing decision (received value
    /// swallowed, unknown id, latch outcomes, handler routing). Same consumer-set convention as
    /// <see cref="Language"/>; also toggleable at runtime via the <c>keybinds trace</c> RA command.
    /// </summary>
    public static bool PressTrace { get; set; }

    private static readonly Dictionary<int, KeybindBlock> Blocks = new();
    private static readonly Dictionary<int, ActiveBinding> ActiveBindings = new();
    private static readonly Dictionary<int, ActiveValueSetting> ActiveValueSettings = new();
    private static readonly Dictionary<ReferenceHub, HashSet<int>> Pressed = new();
    private static readonly Dictionary<int, Dictionary<int, SentDropdownState>> SentPersonalizedDropdowns = new();
    private static readonly Dictionary<int, long> SendGenerations = new();
    private static readonly Stopwatch RefreshClock = Stopwatch.StartNew();
    private static readonly SssRefreshCoordinator<int, PersonalizedSnapshot> RefreshCoordinator = new(
        () => RefreshClock.Elapsed.TotalSeconds,
        snapshot => snapshot.Fingerprint,
        SendCoordinatedSnapshot);

    /// <summary>The process-wide interest router used to target invalidations.</summary>
    public static SssInterestIndex<int> InterestIndex { get; } = new();

    /// <summary>
    /// The two-button default each player was ACTUALLY sent, keyed by player id then setting id.
    ///
    /// Recorded at serialisation rather than recomputed on demand. A per-player default is a function of
    /// live state - playtime, role, permissions - so asking the resolver again later can return a different
    /// answer than the one on the player's screen, and a consumer comparing against it would then read an
    /// untouched default as a deliberate choice. What was sent is the only thing worth comparing to.
    /// </summary>
    private static readonly Dictionary<int, Dictionary<int, bool>> SentTwoButtonDefaults = new();
    private static readonly HashSet<int> WarnedForeignIds = new();
    private static readonly Dictionary<int, DateTime> LastSendUtc = new();
    private static readonly Dictionary<int, int> LastSendCount = new();
    // Keyed by hub.netId: a uint hashes safely and never dereferences a destroyed gameObject.
    private static readonly HashSet<uint> JoinRetried = new();
    private static bool _commandRegistered;
    private static bool _subscribed;
    private static bool _refreshPumpScheduled;
    private static int _refreshPumpGeneration;
    private static double _refreshPumpDueSeconds;
    private static Predicate<ReferenceHub>? _previousJoinFilter;

    /// <summary>
    /// Claims the 1000-wide id block based at <paramref name="baseId"/> (use a <see cref="SssIdBlocks"/> constant).
    /// Throws if the base is not 1000-aligned or the block is already claimed — turning silent runtime id
    /// collisions into a hard load-time failure.
    /// </summary>
    public static KeybindBlock ClaimBlock(int baseId, string ownerName)
    {
        if (baseId % SssIdBlocks.BlockWidth != 0)
        {
            throw new ArgumentException($"Block base {baseId} must be a multiple of {SssIdBlocks.BlockWidth}.", nameof(baseId));
        }

        // The registry synthesises its category headers inside this block, at RegistryHeaders +
        // (int)category - and Gameplay is 0, so a consumer claiming this base and adding the conventional
        // local-0 header would emit a SECOND entry with id 23000. Blocks are 1000-aligned, so this base is
        // the only claimable one that can overlap the reserved range.
        if (baseId == SssIdBlocks.RegistryHeaders)
        {
            throw new ArgumentException(
                $"Block base {baseId} is reserved for the registry's own category headers. " +
                "Pick another base in SssIdBlocks.",
                nameof(baseId));
        }

        // The actual claim (and the collision check) happens at Enable, so that a plugin reload — which
        // Disables (releasing the base) then Enables again — can re-claim its own block without throwing.
        // Blocks are fixed-width and 1000-aligned, so two blocks can only collide if they share a base.
        return new KeybindBlock(baseId, ownerName);
    }

    /// <summary>Every active keybind across all plugins, for diagnostics (e.g. a "list keybinds" admin command).</summary>
    public static IReadOnlyList<KeybindInfo> Registered
    {
        get
        {
            List<KeybindInfo> list = new();
            foreach (KeybindBlock block in Blocks.Values)
            {
                if (!block.Active)
                {
                    continue;
                }

                foreach (KeybindBlock.Binding binding in block.Bindings.Values)
                {
                    list.Add(new KeybindInfo(block.BaseId + binding.Local, binding.Label, binding.DefaultKey, block.Owner));
                }
            }

            return list;
        }
    }

    internal static void EnableBlock(KeybindBlock block)
    {
        if (Blocks.TryGetValue(block.BaseId, out KeybindBlock existing) && !ReferenceEquals(existing, block))
        {
            throw new InvalidOperationException(
                $"SSS block {block.BaseId} is already claimed by '{existing.Owner}' (requested by '{block.Owner}').");
        }

        Blocks[block.BaseId] = block;
        block.Active = true;
        EnsureSubscribed();
        Rebuild();
        LogBlock(block);
    }

    internal static void DisableBlock(KeybindBlock block)
    {
        block.Active = false;
        // Rebuild while the block is still in Blocks so its ids are stripped from DefinedSettings (it is
        // inactive, so its settings are not re-added); then release the base so a reload can re-claim it.
        Rebuild();
        Blocks.Remove(block.BaseId);
        if (Blocks.Count == 0)
        {
            Unsubscribe();
        }
    }

    /// <summary>A binding/header was added to a block after it was already enabled; re-merge.</summary>
    internal static void OnBlockChanged(KeybindBlock block)
    {
        if (block.Active)
        {
            Rebuild();
        }
    }

    private static string CategoryLabel(SettingsCategory category)
    {
        bool english = string.Equals(Language, "en", StringComparison.OrdinalIgnoreCase);
        return category switch
        {
            SettingsCategory.Gameplay => english ? "Gameplay" : "游戏玩法",
            SettingsCategory.Display => english ? "Display & Tags" : "显示与标签",
            SettingsCategory.Announcements => english ? "Announcements" : "公告与提示",
            SettingsCategory.Tools => english ? "Tools & Admin" : "工具与管理",
            _ => english ? "Other" : "其他",
        };
    }

    /// <summary>
    /// The registry canonical render order: category first (by the enum numeric order), then base id, so a
    /// category members are adjacent and the sequence is identical on every server regardless of the order
    /// plugins happened to load in. One category header is emitted the first time that category appears, and
    /// a category with no included block emits nothing.
    /// </summary>
    private static IEnumerable<ServerSpecificSettingBase> BuildOrdered(Func<KeybindBlock, bool> include, Player? player = null)
    {
        SettingsCategory? current = null;
        foreach (KeybindBlock block in Blocks.Values
                     .Where(include)
                     .OrderBy(block => (int)block.Category)
                     .ThenBy(block => block.SortOrder)
                     .ThenBy(block => block.BaseId))
        {
            if (current != block.Category)
            {
                current = block.Category;
                yield return new SSGroupHeader(SssIdBlocks.CategoryHeaderId(block.Category), CategoryLabel(block.Category));
            }

            foreach (ServerSpecificSettingBase setting in block.BuildSettings(player))
            {
                yield return setting;
            }
        }
    }

    /// <summary>
    /// Every id the registry may emit that no block owns: the synthesised category headers.
    ///
    /// Derived from the CLAIMED BLOCKS as well as the declared list, not from the declared list alone. A
    /// header whose category is no longer represented still has to be strippable, or Rebuild would leave
    /// the stale one in place and prepend a fresh one on every pass. <see cref="KeybindBlock.InCategory"/>
    /// rejects undeclared values, so in practice these agree - the union is belt and braces for a block
    /// claimed before that validation existed.
    /// </summary>
    private static IEnumerable<int> RegistryOwnedIds()
    {
        HashSet<SettingsCategory> categories = new(SssIdBlocks.AllCategories);
        foreach (KeybindBlock block in Blocks.Values)
        {
            categories.Add(block.Category);
        }

        foreach (SettingsCategory category in categories)
        {
            yield return SssIdBlocks.CategoryHeaderId(category);
        }
    }

    /// <summary>
    /// Rebuilds the shared <see cref="ServerSpecificSettingsSync.DefinedSettings"/>: strip every id this registry
    /// owns (across ALL claimed blocks, so a disabled block's stale entries are removed), re-add only ACTIVE blocks'
    /// settings, and preserve all foreign entries (additive merge). Then broadcast to everyone.
    /// </summary>
    private static void Rebuild()
    {
        HashSet<int> ownedIds = new(RegistryOwnedIds());
        foreach (KeybindBlock block in Blocks.Values)
        {
            foreach (int id in block.OwnedIds())
            {
                ownedIds.Add(id);
            }
        }

        ActiveBindings.Clear();
        ActiveValueSettings.Clear();
        foreach (KeybindBlock block in Blocks.Values)
        {
            if (!block.Active)
            {
                continue;
            }

            foreach (KeyValuePair<int, KeybindBlock.Binding> pair in block.Bindings)
            {
                ActiveBindings[block.BaseId + pair.Key] = new ActiveBinding(block, pair.Value);
            }
            foreach (KeyValuePair<int, KeybindBlock.ValueSetting> pair in block.ValueSettings)
            {
                ActiveValueSettings[block.BaseId + pair.Key] = new ActiveValueSetting(block, pair.Value);
            }
        }

        List<ServerSpecificSettingBase> ours = BuildOrdered(block => block.Active).ToList();

        ServerSpecificSettingBase[] existingSettings = ServerSpecificSettingsSync.DefinedSettings ?? Array.Empty<ServerSpecificSettingBase>();
        WarnOnForeignCollisions(existingSettings, ownedIds);

        // OURS FIRST, foreign entries after. Appending used to put every registry setting behind every
        // plugin that merges into DefinedSettings on its own (HUD toggles, music mutes), so the ability
        // keybinds - which nothing works without - sat at the very bottom of the menu no matter how the
        // categories were ordered. Foreign entries keep their own relative order and are otherwise
        // untouched; the registry is the sanctioned owner of this array for metarepo plugins.
        ServerSpecificSettingsSync.DefinedSettings = ours
            .Concat(existingSettings.Where(setting => !ownedIds.Contains(setting.SettingId)))
            .ToArray();
        RequestPersonalizedForAll("rebuild");
    }

    /// <summary>
    /// The starting position a two-button setting hands <paramref name="player"/>, or null when that id is
    /// not a registry-owned two-button setting. Consumers compare the value they RECEIVE against this to
    /// tell an explicit choice from an untouched default - the client reports its value on acquisition as
    /// well as on change, so a callback alone proves nothing.
    /// </summary>
    public static bool? DefaultTwoButtonsFor(Player player, int settingId)
    {
        if (player == null)
        {
            return null;
        }

        return SentTwoButtonDefaults.TryGetValue(player.PlayerId, out Dictionary<int, bool> perSetting)
            && perSetting.TryGetValue(settingId, out bool sent)
                ? sent
                : (bool?)null;
    }

    /// <summary>
    /// Backward-compatible refresh entry point. The request now uses the shared per-player coordinator;
    /// it is debounced, coalesced, fingerprinted, and rate limited rather than sent synchronously.
    /// </summary>
    public static void RefreshPlayer(Player player)
    {
        RequestPlayerRefresh(player, "refresh");
    }

    /// <summary>Queues the latest personalized view for one player through the process-wide budget.</summary>
    public static void RequestPlayerRefresh(Player player, string reason)
    {
        if (player == null || player.IsDestroyed || !player.IsPlayer || !player.IsReady)
        {
            return;
        }

        InterestIndex.Track(player.PlayerId);
        RefreshCoordinator.Request(player.PlayerId, BuildPersonalizedSnapshot(player), reason);
        ScheduleRefreshPump();
    }

    /// <summary>Registers which state domains can affect one player's view.</summary>
    public static void SetPlayerInterests(Player player, SssInterest interests)
    {
        if (player != null)
        {
            InterestIndex.Track(player.PlayerId, interests);
        }
    }

    /// <summary>Invalidates a personal state domain without ever fanning out to unrelated players.</summary>
    public static bool InvalidatePlayer(Player player, SssInterest changed, string reason)
    {
        if (player == null || InterestIndex.ResolvePersonal(player.PlayerId, changed).Count == 0)
        {
            return false;
        }

        RequestPlayerRefresh(player, reason);
        return true;
    }

    /// <summary>
    /// Invalidates only existing players affected by the one-to-two or two-to-one real-population boundary.
    /// The newcomer is intentionally handled by its normal join send.
    /// </summary>
    public static int InvalidatePopulationBoundary(
        IReadOnlyCollection<Player> before,
        IReadOnlyCollection<Player> after,
        string reason)
    {
        int[] beforeIds = before?.Where(player => player != null).Select(player => player.PlayerId).ToArray()
            ?? throw new ArgumentNullException(nameof(before));
        int[] afterIds = after?.Where(player => player != null).Select(player => player.PlayerId).ToArray()
            ?? throw new ArgumentNullException(nameof(after));
        IReadOnlyList<int> affected = InterestIndex.ResolvePopulationBoundary(beforeIds, afterIds);
        int requested = 0;
        foreach (int playerId in affected)
        {
            Player? player = Player.Get(playerId);
            if (player != null)
            {
                RequestPlayerRefresh(player, reason);
                requested++;
            }
        }

        return requested;
    }

    /// <summary>Process-wide refresh counters.</summary>
    public static SssRefreshCounters RefreshCounters => RefreshCoordinator.Counters;

    /// <summary>Returns the player's monotonic last-send time, final-view fingerprint, and budget state.</summary>
    public static bool TryGetRefreshDiagnostics(Player player, out SssRefreshPlayerDiagnostics diagnostics)
    {
        if (player == null)
        {
            diagnostics = default;
            return false;
        }

        return RefreshCoordinator.TryGetDiagnostics(player.PlayerId, out diagnostics);
    }

    /// <summary>
    /// The last personalized-send audit record for <paramref name="player"/>: when it happened (UTC) and
    /// how many entries it carried. False when no send has been recorded for them this round.
    /// </summary>
    public static bool TryGetSendAudit(Player player, out DateTime lastSendUtc, out int entryCount)
    {
        lastSendUtc = default;
        entryCount = 0;
        return player != null
            && LastSendUtc.TryGetValue(player.PlayerId, out lastSendUtc)
            && LastSendCount.TryGetValue(player.PlayerId, out entryCount);
    }

    /// <summary>The setting ids currently latched as pressed for <paramref name="player"/> (snapshot).</summary>
    public static IReadOnlyCollection<int> PressedFor(Player player)
    {
        return player?.ReferenceHub != null && Pressed.TryGetValue(player.ReferenceHub, out HashSet<int> pressed)
            ? pressed.ToArray()
            : Array.Empty<int>();
    }

    /// <summary>
    /// Diagnostics only: the entry count <paramref name="player"/> WOULD receive from a personalized send
    /// right now. Building a candidate has no send-side effects; defaults and acquisition generations are
    /// recorded only after the network send succeeds.
    /// </summary>
    internal static int PersonalizedEntryCountFor(Player player)
    {
        HashSet<int> allOwnedIds = new(RegistryOwnedIds());
        foreach (KeybindBlock block in Blocks.Values)
        {
            foreach (int id in block.OwnedIds())
            {
                allOwnedIds.Add(id);
            }
        }

        return BuildOrdered(block => block.Active && block.IsVisibleTo(player), player).Count()
            + (ServerSpecificSettingsSync.DefinedSettings ?? Array.Empty<ServerSpecificSettingBase>())
                .Count(setting => !allOwnedIds.Contains(setting.SettingId));
    }

    private static void RequestPersonalizedForAll(string reason)
    {
        foreach (Player player in Player.ReadyList)
        {
            if (player.IsPlayer && player.IsReady)
            {
                RequestPlayerRefresh(player, reason);
            }
        }
    }

    private static PersonalizedSnapshot BuildPersonalizedSnapshot(Player player)
    {
        HashSet<int> allOwnedIds = new(RegistryOwnedIds());
        foreach (KeybindBlock block in Blocks.Values)
        {
            foreach (int id in block.OwnedIds())
            {
                allOwnedIds.Add(id);
            }
        }

        // Ordered per RECIPIENT, not once globally: a category whose only block is hidden from this player
        // must not leave a dangling header behind for them. Ours lead here too, matching Rebuild.
        List<ServerSpecificSettingBase> collection =
            BuildOrdered(block => block.Active && block.IsVisibleTo(player), player).ToList();
        collection.AddRange((ServerSpecificSettingsSync.DefinedSettings ?? Array.Empty<ServerSpecificSettingBase>())
            .Where(setting => !allOwnedIds.Contains(setting.SettingId)));

        return new PersonalizedSnapshot(player, collection.ToArray(), SssViewFingerprint.Compute(collection));
    }

    private static bool SendSnapshot(PersonalizedSnapshot snapshot, string reason)
    {
        Player player = snapshot.Player;
        if (player == null || player.IsDestroyed || !player.IsPlayer || !player.IsReady || player.ReferenceHub?.connectionToClient == null)
        {
            return false;
        }

        ServerSpecificSettingsSync.SendToPlayer(player.ReferenceHub, snapshot.Settings);
        RecordSentTwoButtonDefaults(player.PlayerId, snapshot.Settings);
        RecordPersonalizedDropdownGeneration(player.PlayerId, snapshot.Settings);
        LastSendUtc[player.PlayerId] = DateTime.UtcNow;
        LastSendCount[player.PlayerId] = snapshot.Settings.Length;
        // LabAPI's Logger.Debug is NOT globally gated - always pass the flag or this spams every send.
        Logger.Debug($"[ServerKeybinds] Sent {snapshot.Settings.Length} entries to {player.Nickname} ({player.PlayerId}) [{reason}].", Debug);
        return true;
    }

    private static bool SendCoordinatedSnapshot(
        int playerId,
        PersonalizedSnapshot snapshot,
        IReadOnlyCollection<string> reasons)
    {
        if (snapshot.Player.PlayerId != playerId)
        {
            return false;
        }

        return SendSnapshot(snapshot, "refresh:" + string.Join("+", reasons));
    }

    private static void SendPersonalizedNow(Player player, string reason)
    {
        PersonalizedSnapshot snapshot = BuildPersonalizedSnapshot(player);
        if (SendSnapshot(snapshot, reason))
        {
            RefreshCoordinator.RecordSent(player.PlayerId, snapshot);
        }
    }

    private static void ScheduleRefreshPump()
    {
        double? delay = RefreshCoordinator.SecondsUntilNextProcess();
        if (!delay.HasValue)
        {
            return;
        }

        double dueSeconds = RefreshClock.Elapsed.TotalSeconds + delay.Value;
        if (_refreshPumpScheduled && dueSeconds >= _refreshPumpDueSeconds - 0.001)
        {
            return;
        }

        _refreshPumpScheduled = true;
        _refreshPumpDueSeconds = dueSeconds;
        int generation = ++_refreshPumpGeneration;
        Timing.CallDelayed((float)Math.Max(0.01, delay.Value), () =>
        {
            if (generation != _refreshPumpGeneration)
            {
                return;
            }

            _refreshPumpScheduled = false;
            if (!_subscribed)
            {
                return;
            }

            RefreshCoordinator.ProcessDue();
            ScheduleRefreshPump();
        });
    }

    private static void RecordPersonalizedDropdownGeneration(
        int playerId,
        IReadOnlyCollection<ServerSpecificSettingBase> settings)
    {
        long generation = SendGenerations.TryGetValue(playerId, out long previous) ? previous + 1 : 1;
        SendGenerations[playerId] = generation;
        Dictionary<int, SentDropdownState> sent = new();
        foreach (ServerSpecificSettingBase setting in settings)
        {
            if (setting is SSDropdownSetting dropdown
                && ActiveValueSettings.TryGetValue(setting.SettingId, out ActiveValueSetting active)
                && active.Setting is KeybindBlock.PersonalizedDropdownSetting)
            {
                sent[setting.SettingId] = new SentDropdownState(generation, dropdown.Options.ToArray());
            }
        }

        SentPersonalizedDropdowns[playerId] = sent;
    }

    private static void RecordSentTwoButtonDefaults(
        int playerId,
        IReadOnlyCollection<ServerSpecificSettingBase> settings)
    {
        Dictionary<int, bool> sent = new();
        foreach (ServerSpecificSettingBase setting in settings)
        {
            if (setting is SSTwoButtonsSetting twoButtons
                && ActiveValueSettings.TryGetValue(setting.SettingId, out ActiveValueSetting active)
                && active.Setting is KeybindBlock.TwoButtonsSetting)
            {
                sent[setting.SettingId] = twoButtons.DefaultIsB;
            }
        }

        SentTwoButtonDefaults[playerId] = sent;
    }

    /// <summary>
    /// Classifies the first value in each send generation as acquisition-only. Later duplicates are swallowed;
    /// an acquisition or changed in-range value is returned for the personalized setting to revalidate.
    /// </summary>
    internal static bool TryTakePersonalizedDropdownResponse(
        Player player,
        int settingId,
        int rawIndex,
        out DropdownSelection selection,
        out PersonalizedDropdownResponseKind responseKind)
    {
        selection = default;
        responseKind = PersonalizedDropdownResponseKind.Duplicate;
        if (!SentPersonalizedDropdowns.TryGetValue(player.PlayerId, out Dictionary<int, SentDropdownState> settings)
            || !settings.TryGetValue(settingId, out SentDropdownState state)
            || rawIndex < 0
            || rawIndex >= state.Options.Length)
        {
            return false;
        }

        responseKind = state.Latch.Observe(rawIndex);
        if (responseKind == PersonalizedDropdownResponseKind.Duplicate)
        {
            return false;
        }

        selection = new DropdownSelection(rawIndex, state.Options[rawIndex], state.Generation);
        return true;
    }

    private static void WarnOnForeignCollisions(ServerSpecificSettingBase[] existingSettings, HashSet<int> ownedIds)
    {
        foreach (ServerSpecificSettingBase setting in existingSettings)
        {
            if (ownedIds.Contains(setting.SettingId) || WarnedForeignIds.Contains(setting.SettingId))
            {
                continue;
            }

            foreach (KeybindBlock block in Blocks.Values)
            {
                if (SssIdBlocks.Contains(block.BaseId, setting.SettingId))
                {
                    WarnedForeignIds.Add(setting.SettingId);
                    Logger.Warn(
                        $"[ServerKeybinds] Foreign setting id {setting.SettingId} sits inside block '{block.Owner}' " +
                        $"({block.BaseId}-{block.BaseId + SssIdBlocks.BlockWidth - 1}); a non-migrated plugin may collide.");
                    break;
                }
            }
        }
    }

    private static void EnsureSubscribed()
    {
        if (_subscribed)
        {
            return;
        }

        _subscribed = true;
        _previousJoinFilter = ServerSpecificSettingsSync.SendOnJoinFilter;
        ServerSpecificSettingsSync.SendOnJoinFilter = SuppressNativeJoinSend;
        ServerSpecificSettingsSync.ServerOnSettingValueReceived += OnSettingValueReceived;
        PlayerAuthenticationManager.OnInstanceModeChanged += OnInstanceModeChanged;
        PlayerRoleManager.OnRoleChanged += OnRoleChanged;
        RoundRestart.OnRestartTriggered += OnRoundRestart;
        ReferenceHub.OnPlayerRemoved += OnPlayerRemoved;

        if (!_commandRegistered)
        {
            // LabAPI only scans PLUGIN assemblies for [CommandHandler] types; a dependencies/global
            // library must self-register. Registered once for the process lifetime - TryRegisterCommand
            // no-ops (returning true) on a duplicate name, so a later re-subscribe cannot double-add.
            _commandRegistered = CommandLoader.TryRegisterCommand(
                new KeybindsCommand(), CommandProcessor.RemoteAdminCommandHandler, "ServerKeybinds");
            if (!_commandRegistered)
            {
                Logger.Warn("[ServerKeybinds] 'keybinds' RA command could not be registered (name already taken?).");
            }
            else
            {
                Logger.Info("[ServerKeybinds] Registered RA command 'keybinds' (alias 'skb').");
            }
        }
    }

    private static void Unsubscribe()
    {
        if (!_subscribed)
        {
            return;
        }

        _subscribed = false;
        if (ServerSpecificSettingsSync.SendOnJoinFilter == SuppressNativeJoinSend)
        {
            ServerSpecificSettingsSync.SendOnJoinFilter = _previousJoinFilter;
        }
        _previousJoinFilter = null;
        ServerSpecificSettingsSync.ServerOnSettingValueReceived -= OnSettingValueReceived;
        PlayerAuthenticationManager.OnInstanceModeChanged -= OnInstanceModeChanged;
        PlayerRoleManager.OnRoleChanged -= OnRoleChanged;
        RoundRestart.OnRestartTriggered -= OnRoundRestart;
        ReferenceHub.OnPlayerRemoved -= OnPlayerRemoved;
        Pressed.Clear();
        SentTwoButtonDefaults.Clear();
        SentPersonalizedDropdowns.Clear();
        SendGenerations.Clear();
        LastSendUtc.Clear();
        LastSendCount.Clear();
        JoinRetried.Clear();
        RefreshCoordinator.Clear();
        InterestIndex.Clear();
        _refreshPumpScheduled = false;
        _refreshPumpGeneration++;
    }

    private static void OnRoleChanged(ReferenceHub userHub, PlayerRoleBase prevRole, PlayerRoleBase newRole)
    {
        // A role change invalidates any held key: the client keybind state resets with the role, so a
        // latched press would otherwise never see its falling edge and block the next rising one.
        // Dispatch the release BEFORE dropping the latch - every delivered press must be followed by
        // exactly one release, or hold-to-act consumers without their own role hook stay stuck held.
        if (userHub == null || !Pressed.TryGetValue(userHub, out HashSet<int> pressed))
        {
            return;
        }

        foreach (int settingId in new List<int>(pressed))
        {
            if (ActiveBindings.TryGetValue(settingId, out ActiveBinding activeBinding))
            {
                if (PressTrace)
                {
                    Logger.Debug($"[ServerKeybinds] Trace: role change releases latched id {settingId} (player {userHub.PlayerId}).", PressTrace);
                }

                Invoke(activeBinding, userHub, released: true);
            }
        }

        Pressed.Remove(userHub);
    }

    private static void OnRoundRestart()
    {
        Pressed.Clear();
        JoinRetried.Clear();
    }

    private static void OnPlayerRemoved(ReferenceHub hub)
    {
        // Fires from ReferenceHub.OnDestroy while the hub is still valid, so removing the hub-keyed entry
        // here also closes the destroyed-hub-key hazard (ReferenceHub.GetHashCode derefs its gameObject).
        if (hub == null)
        {
            return;
        }

        Pressed.Remove(hub);
        SentTwoButtonDefaults.Remove(hub.PlayerId);
        SentPersonalizedDropdowns.Remove(hub.PlayerId);
        SendGenerations.Remove(hub.PlayerId);
        LastSendUtc.Remove(hub.PlayerId);
        LastSendCount.Remove(hub.PlayerId);
        JoinRetried.Remove(hub.netId);
        RefreshCoordinator.Remove(hub.PlayerId);
        InterestIndex.Untrack(hub.PlayerId);
    }

    private static void OnSettingValueReceived(ReferenceHub hub, ServerSpecificSettingBase setting)
    {
        if (ActiveValueSettings.TryGetValue(setting.SettingId, out ActiveValueSetting activeValue))
        {
            Player? valuePlayer = Player.Get(hub);
            if (valuePlayer == null || !activeValue.Block.IsVisibleTo(valuePlayer))
            {
                if (PressTrace)
                {
                    Logger.Debug($"[ServerKeybinds] Trace: value for id {setting.SettingId} from player {hub.PlayerId} swallowed: block '{activeValue.Block.Owner}' not visible.", PressTrace);
                }
                return;
            }

            try
            {
                activeValue.Setting.Invoke(valuePlayer, setting);
            }
            catch (Exception exception)
            {
                Logger.Warn($"[ServerKeybinds] '{activeValue.Setting.Label}' change handler threw: {exception.GetBaseException().Message}");
            }
            return;
        }

        if (setting is not SSKeybindSetting keybind)
        {
            return;
        }

        if (!ActiveBindings.TryGetValue(setting.SettingId, out ActiveBinding activeBinding))
        {
            if (PressTrace)
            {
                Logger.Debug($"[ServerKeybinds] Trace: no active binding for id {setting.SettingId} (player {hub.PlayerId}).", PressTrace);
            }
            return;
        }

        if (!Pressed.TryGetValue(hub, out HashSet<int> pressed))
        {
            pressed = new HashSet<int>();
            Pressed[hub] = pressed;
        }

        // Fires on both press and release. Act on the rising edge (press) and, for bindings that want it,
        // the falling edge (release) - so a handler can implement hold-to-act behavior.
        if (!keybind.SyncIsPressed)
        {
            if (pressed.Remove(setting.SettingId))
            {
                if (PressTrace)
                {
                    Logger.Debug($"[ServerKeybinds] Trace: release for id {setting.SettingId} (player {hub.PlayerId}).", PressTrace);
                }
                Invoke(activeBinding, hub, released: true);
            }
            else
            {
                if (PressTrace)
                {
                    Logger.Debug($"[ServerKeybinds] Trace: release with no latch for id {setting.SettingId} (player {hub.PlayerId}).", PressTrace);
                }
            }

            return;
        }

        if (!pressed.Add(setting.SettingId))
        {
            if (PressTrace)
            {
                Logger.Debug($"[ServerKeybinds] Trace: press already latched (ignored) for id {setting.SettingId} (player {hub.PlayerId}).", PressTrace);
            }
            return;
        }

        if (PressTrace)
        {
            Logger.Debug($"[ServerKeybinds] Trace: press latched for id {setting.SettingId} (player {hub.PlayerId}).", PressTrace);
        }
        Invoke(activeBinding, hub, released: false);
    }

    private static void Invoke(ActiveBinding active, ReferenceHub hub, bool released)
    {
        Player? player = Player.Get(hub);
        if (player == null)
        {
            return;
        }

        if (!active.Block.IsVisibleTo(player))
        {
            return;
        }

        KeybindBlock.Binding binding = active.Binding;
        if (PressTrace)
        {
            Logger.Debug($"[ServerKeybinds] Trace: routing {(released ? "release" : "press")} to owner='{active.Block.Owner}' binding='{binding.Label}' (player {hub.PlayerId}).", PressTrace);
        }
        Action<Player>? handler = released ? binding.OnReleased : binding.OnPressed;
        if (handler == null)
        {
            if (PressTrace)
            {
                Logger.Debug($"[ServerKeybinds] Trace: no handler for this edge on binding '{binding.Label}'.", PressTrace);
            }
            return;
        }

        try
        {
            handler(player);
        }
        catch (Exception exception)
        {
            Logger.Warn($"[ServerKeybinds] '{binding.Label}' {(released ? "release" : "press")} handler threw: {exception.GetBaseException().Message}");
        }
    }

    /// <summary>
    /// When a client finishes authenticating, re-send the FULL (additive) DefinedSettings to it a beat later.
    /// Some plugins push only their own settings to a joining player (replacing the client's whole view and
    /// dropping everyone else's entries); re-broadcasting the complete shared set restores all of them.
    /// </summary>
    private static void OnInstanceModeChanged(ReferenceHub hub, ClientInstanceMode mode)
    {
        if (mode != ClientInstanceMode.ReadyClient)
        {
            return;
        }

        ScheduleJoinSend(hub, 0.75f, isRetry: false);
    }

    private static void ScheduleJoinSend(ReferenceHub hub, float delaySeconds, bool isRetry)
    {
        if (_previousJoinFilter != null && !_previousJoinFilter(hub))
        {
            Logger.Debug($"[ServerKeybinds] Join send for netId {hub.netId} suppressed by a foreign SendOnJoinFilter.", Debug);
            return;
        }

        Timing.CallDelayed(delaySeconds, () =>
        {
            if (!_subscribed)
            {
                Logger.Debug("[ServerKeybinds] Join send skipped: registry torn down.", Debug);
                return;
            }

            if (hub == null)
            {
                Logger.Debug("[ServerKeybinds] Join send skipped: hub gone.", Debug);
                return;
            }

            if (hub.connectionToClient == null)
            {
                RetryJoinSendOrWarn(hub, isRetry, "no client connection yet");
                return;
            }

            try
            {
                Player? player = Player.Get(hub);
                if (player == null)
                {
                    RetryJoinSendOrWarn(hub, isRetry, "no Player wrapper yet");
                    return;
                }

                InterestIndex.Track(player.PlayerId);
                SendPersonalizedNow(player, isRetry ? "join-retry" : "join");
                if (isRetry)
                {
                    Logger.Info($"[ServerKeybinds] Join-send retry succeeded for {player.Nickname} ({player.PlayerId}).");
                }
            }
            catch (Exception exception)
            {
                Logger.Warn($"[ServerKeybinds] Failed to re-send settings to a player: {exception.GetBaseException().Message}");
            }
        });
    }

    /// <summary>
    /// A transient join-send skip retries exactly once per hub (keyed by netId); a second failure is a
    /// Warn because that player now has no Server-Specific Settings entries until the next Rebuild.
    /// </summary>
    private static void RetryJoinSendOrWarn(ReferenceHub hub, bool isRetry, string why)
    {
        if (!isRetry && JoinRetried.Add(hub.netId))
        {
            Logger.Debug($"[ServerKeybinds] Join send for netId {hub.netId} skipped ({why}); retrying once in 1.5s.", Debug);
            ScheduleJoinSend(hub, 1.5f, isRetry: true);
            return;
        }

        Logger.Warn($"[ServerKeybinds] Join send for netId {hub.netId} failed after retry ({why}); the player may have no settings menu.");
    }

    private static void LogBlock(KeybindBlock block)
    {
        // Every entry, not just keybinds: a block that registers only a toggle used to log an empty list,
        // which reads exactly like "the setting failed to register".
        IEnumerable<string> binds = block.Bindings.Values
            .Select(b => $"{block.BaseId + b.Local}:{b.Label}({b.DefaultKey})");
        IEnumerable<string> values = block.ValueSettings.Values
            .Select(v => $"{block.BaseId + v.Local}:{v.Label}");
        IEnumerable<string> texts = block.Texts
            .Select(t => $"{block.BaseId + t.Local}:<text>");
        string entries = string.Join(", ", texts.Concat(binds).Concat(values));
        Logger.Info(
            $"[ServerKeybinds] '{block.Owner}' enabled block {block.BaseId} " +
            $"under {block.Category} [{entries}].");
    }

    private static bool SuppressNativeJoinSend(ReferenceHub _) => false;

    private readonly struct ActiveBinding
    {
        public ActiveBinding(KeybindBlock block, KeybindBlock.Binding binding)
        {
            Block = block;
            Binding = binding;
        }

        public KeybindBlock Block { get; }

        public KeybindBlock.Binding Binding { get; }
    }

    private readonly struct ActiveValueSetting
    {
        public ActiveValueSetting(KeybindBlock block, KeybindBlock.ValueSetting setting)
        {
            Block = block;
            Setting = setting;
        }

        public KeybindBlock Block { get; }

        public KeybindBlock.ValueSetting Setting { get; }
    }

    private sealed class PersonalizedSnapshot
    {
        public PersonalizedSnapshot(Player player, ServerSpecificSettingBase[] settings, string fingerprint)
        {
            Player = player;
            Settings = settings;
            Fingerprint = fingerprint;
        }

        public Player Player { get; }

        public ServerSpecificSettingBase[] Settings { get; }

        public string Fingerprint { get; }
    }

    private sealed class SentDropdownState
    {
        public SentDropdownState(long generation, string[] options)
        {
            Generation = generation;
            Options = options;
        }

        public long Generation { get; }

        public string[] Options { get; }

        public PersonalizedDropdownResponseLatch Latch { get; } = new();
    }
}
