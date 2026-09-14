namespace ServerKeybinds;

/// <summary>
/// The single source of truth for Server-Specific-Settings id allocation across the whole server.
/// Every plugin owns one fixed, 1000-wide block: id = <c>base + local</c>, where local 0 is the
/// group header and 1..999 are settings. Because <see cref="UserSettings.ServerSpecific.ServerSpecificSettingsSync.DefinedSettings"/>
/// is a single static array shared by EVERY plugin, blocks must never overlap; <see cref="KeybindRegistry.ClaimBlock"/>
/// enforces that at load time. Add a new plugin here rather than picking a bare id inline.
/// </summary>
/// <summary>
/// The logical section a block renders under in the player's Server-Specific-Settings menu.
///
/// Before API 3 the menu order was <c>Dictionary&lt;int, KeybindBlock&gt;</c> enumeration order — i.e. plugin
/// load order — so the list read as an unsorted pile. <see cref="KeybindRegistry"/> now emits ONE
/// <see cref="UserSettings.ServerSpecific.SSGroupHeader"/> per category, in the numeric order below, and
/// demotes each block's own header to a reduced-padding sub-header underneath it.
///
/// The numbers ARE the display order, so leave gaps when inserting. <see cref="Other"/> is deliberately last
/// and is what an un-migrated block falls into, so forgetting <c>InCategory</c> costs placement, not
/// visibility.
/// </summary>
public enum SettingsCategory
{
    /// <summary>Ability keybinds and anything that changes what the player can DO.</summary>
    Gameplay = 0,

    /// <summary>HUD, nametag and other presentation toggles.</summary>
    Display = 10,

    /// <summary>Opt-in/opt-out switches for notices, music and guidance the server pushes at the player.</summary>
    Announcements = 20,

    /// <summary>Staff, observer and authoring controls. Usually paired with a <c>VisibleTo</c> filter.</summary>
    Tools = 30,

    /// <summary>Uncategorised. Renders last; the fallback for a block that never called <c>InCategory</c>.</summary>
    Other = 100,
}

public static class SssIdBlocks
{
    /// <summary>Width of every plugin's reserved id block. Bases must be a multiple of this.</summary>
    public const int BlockWidth = 1000;

    // --- Custom-item / ability plugins (7-digit convention already in use) ---

    /// <summary>"SCP Enhacements" / scp-106 wiki abilities (was 1060100-1060106).</summary>
    public const int Scp106 = 1060000;

    /// <summary>reinforcements-system: SRA deploy (was 1090100/01). The plugin owns this block + <see cref="ReinforcementsMedic"/>.</summary>
    public const int Reinforcements = 1090000;

    /// <summary>reinforcements-system Medic field-heal (was 1090200/01). A separate block so the SRA and Medic services stay independent.</summary>
    public const int ReinforcementsMedic = 1091000;

    /// <summary>reinforcements-system Serpent's Hand abilities (Mouse0/Mouse1). Its own block so Serpent abilities work even when SRA/Medic are disabled (a 1000-aligned base can be Enable()d by only one KeybindBlock).</summary>
    public const int SerpentsHand = 1092000;

    /// <summary>goc-nuke: reserved for future ability keybinds (none today).</summary>
    public const int GocNuke = 1100000;

    /// <summary>SpinBot observer-only spin toggle and tuning controls.</summary>
    public const int SpinBot = 1110000;

    /// <summary>InvincibleWarMark active ability keybind.</summary>
    public const int InvincibleWarMark = 1120000;

    /// <summary>SCPSLBot personalized warmup gameplay controls.</summary>
    public const int ScpslBotWarmup = 1130000;

    /// <summary>StatsBots display preferences and personalized title selection.</summary>
    public const int StatsBots = 1131000;

    /// <summary>SCPSLBot permission-gated diagnostics, navigation authoring, and force-role tools.</summary>
    public const int ScpslBotTools = 1132000;

    // --- The registry's own reserved block ---

    /// <summary>
    /// Reserved for <see cref="KeybindRegistry"/> itself: the per-<see cref="SettingsCategory"/> group
    /// headers it synthesises live at <c>RegistryHeaders + (int)category</c>. They need real, stable ids
    /// inside the allocation scheme — an id-less <c>SSGroupHeader</c> derives its id from a hash of its
    /// label, which would drift with the label and could land on a plugin's id.
    /// </summary>
    public const int RegistryHeaders = 23000;

    // --- Config-driven toggle plugins (documented here so new blocks never land on them) ---

    /// <summary>global-music-player mute toggle (currently 24000/24001).</summary>
    public const int GlobalMusic = 24000;

    /// <summary>MvpSystem music toggle (currently the bare id 300; re-home here when migrated).</summary>
    public const int MvpSystem = 25000;

    /// <summary>EffectDisplay time-effect toggle (currently 2030/2031; re-home here when migrated).</summary>
    public const int EffectDisplay = 26000;

    /// <summary>
    /// The new-player guide's single opt-out toggle, DEFINED by SB_WelcomeMessage and READ by
    /// reinforcements-system through <c>ServerSpecificSettingsSync.TryGetSettingOfUser</c>. One id so the
    /// player sees one switch covering every surface of the guide, not one per plugin.
    /// </summary>
    public const int NewPlayerGuide = 27000;

    /// <summary>CustomizableUIMeow HUD toggles (already based at 530210; the block covers its full span).</summary>
    public const int CustomizableUi = 530000;

    /// <summary>Every declared category, in display order. Used to strip stale synthesised headers.</summary>
    public static readonly SettingsCategory[] AllCategories =
    {
        SettingsCategory.Gameplay,
        SettingsCategory.Display,
        SettingsCategory.Announcements,
        SettingsCategory.Tools,
        SettingsCategory.Other,
    };

    /// <summary>The stable SSS id of the synthesised group header for <paramref name="category"/>.</summary>
    public static int CategoryHeaderId(SettingsCategory category) => RegistryHeaders + (int)category;

    /// <summary>True if <paramref name="settingId"/> falls inside the 1000-wide block at <paramref name="baseId"/>.</summary>
    public static bool Contains(int baseId, int settingId) =>
        settingId >= baseId && settingId < baseId + BlockWidth;

    /// <summary>The 1000-aligned block base that would own <paramref name="settingId"/>.</summary>
    public static int BaseOf(int settingId) => settingId - Mod(settingId, BlockWidth);

    private static int Mod(int value, int modulus)
    {
        int r = value % modulus;
        return r < 0 ? r + modulus : r;
    }
}
