#nullable enable

using System;
using System.Collections.Generic;
using LabApi.Features.Wrappers;

namespace SCPSLBot.Warmup.Controls.Panel;

/// <summary>A stable server-side choice. Id is submitted to policy; labels are presentation only.</summary>
public sealed class WarmupPanelChoice
{
    public WarmupPanelChoice(string id, string englishLabel, string chineseLabel)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("A stable, non-empty choice ID is required.", nameof(id));
        }

        Id = id;
        EnglishLabel = string.IsNullOrWhiteSpace(englishLabel) ? id : englishLabel;
        ChineseLabel = string.IsNullOrWhiteSpace(chineseLabel) ? id : chineseLabel;
    }

    public string Id { get; }

    public string EnglishLabel { get; }

    public string ChineseLabel { get; }
}

/// <summary>
/// Integration boundary for actions not owned by the exact-role and item-grant policy services.
/// Every Try method must recapture live warmup/player state, compare <paramref name="expectedFullUserId"/>
/// with the current full UserId, and commit only the exact requested stable ID. Availability methods are
/// called again immediately before every Try method, so they must be current and side-effect free.
/// </summary>
public interface IWarmupPanelActions
{
    bool IsWarmupActive { get; }

    /// <summary>Returns null when the dedicated server cannot determine the client's language.</summary>
    string? GetClientLanguage(Player player);

    IReadOnlyList<WarmupPanelChoice> GetAvailableLoadouts(Player player, string expectedFullUserId);

    ControlResult TryEquipLoadout(Player player, string expectedFullUserId, string loadoutId);

    IReadOnlyList<WarmupPanelChoice> GetAvailableTeleportDestinations(
        Player player,
        string expectedFullUserId);

    ControlResult TryTeleport(Player player, string expectedFullUserId, string destinationId);

    bool IsArenaPresetAvailable(Player player, string expectedFullUserId, string presetId);

    string GetActiveArenaPresetId(Player player, string expectedFullUserId);

    ControlResult TryActivateArenaPreset(Player player, string expectedFullUserId, string presetId);

    bool IsBotDiagnosticsEnabled(Player player);

    ControlResult TrySetBotDiagnostics(Player player, string expectedFullUserId, bool enabled);

    bool IsNavAuthoringEnabled(Player player);

    ControlResult TrySetNavAuthoring(Player player, string expectedFullUserId, bool enabled);
}
