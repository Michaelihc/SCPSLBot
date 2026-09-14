using UnityEngine;

namespace ServerKeybinds;

/// <summary>
/// A read-only snapshot of one registered keybind, for diagnostics (e.g. a future "list keybinds"
/// admin command or the boot id-map log). Exposed via <see cref="KeybindRegistry.Registered"/>.
/// </summary>
public readonly struct KeybindInfo
{
    public KeybindInfo(int settingId, string label, KeyCode defaultKey, string owner)
    {
        SettingId = settingId;
        Label = label;
        DefaultKey = defaultKey;
        Owner = owner;
    }

    /// <summary>The absolute Server-Specific-Settings id (block base + local).</summary>
    public int SettingId { get; }

    /// <summary>The label shown in the Server-Specific Settings menu.</summary>
    public string Label { get; }

    /// <summary>The default key (players can rebind client-side).</summary>
    public KeyCode DefaultKey { get; }

    /// <summary>The owning plugin/block name passed to <see cref="KeybindRegistry.ClaimBlock"/>.</summary>
    public string Owner { get; }
}

