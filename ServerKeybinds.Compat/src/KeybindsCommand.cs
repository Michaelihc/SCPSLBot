using System;
using System.Linq;
using CommandSystem;
using LabApi.Features.Wrappers;
using UserSettings.ServerSpecific;

namespace ServerKeybinds;

/// <summary>
/// RA diagnostics probe for the shared settings registry. LabAPI only scans PLUGIN assemblies for
/// <c>[CommandHandler]</c> types, so this dependency library registers the command itself from
/// <see cref="KeybindRegistry"/> when the first block is enabled.
/// </summary>
public sealed class KeybindsCommand : ICommand
{
    public string Command => "keybinds";

    public string[] Aliases => ["skb"];

    public string Description => "ServerKeybinds diagnostics: status <id|name> | resend <id|name> | trace on|off";

    private const string Usage = "Usage: keybinds status <id|name> | resend <id|name> | trace on|off";

    public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
    {
        if (!sender.CheckPermission(PlayerPermissions.ServerConsoleCommands, out _))
        {
            response = "Permission denied: requires ServerConsoleCommands.";
            return false;
        }

        string action = arguments.Count > 0 ? arguments.At(0).Trim().ToLowerInvariant() : string.Empty;
        switch (action)
        {
            case "status":
                return Status(arguments, out response);
            case "resend":
                return Resend(arguments, out response);
            case "trace":
                return Trace(arguments, out response);
            default:
                response = Usage;
                return false;
        }
    }

    private static bool Status(ArraySegment<string> arguments, out string response)
    {
        if (!TryResolveTarget(arguments, "status", out Player? player, out response))
        {
            return false;
        }

        string audit = KeybindRegistry.TryGetSendAudit(player!, out DateTime lastSendUtc, out int sentCount)
            ? $"last send {lastSendUtc:HH:mm:ss} UTC with {sentCount} entries"
            : "no send recorded";
        string pressed = string.Join(", ", KeybindRegistry.PressedFor(player!));
        if (pressed.Length == 0)
        {
            pressed = "none";
        }

        ReferenceHub hub = player!.ReferenceHub;
        // GetUserVersion == 0 means the client never acknowledged ANY settings pack - that is what
        // separates "we never sent" from "we sent and something overwrote it afterwards".
        int userVersion = ServerSpecificSettingsSync.GetUserVersion(hub);
        bool tabOpen = ServerSpecificSettingsSync.IsTabOpenForUser(hub);
        int wouldReceive = KeybindRegistry.PersonalizedEntryCountFor(player);
        string refresh = KeybindRegistry.TryGetRefreshDiagnostics(player, out SssRefreshPlayerDiagnostics refreshAudit)
            ? $"fingerprint {Short(refreshAudit.Fingerprint)}, rolling sends {refreshAudit.SendsInRollingMinute}/6, pending {refreshAudit.Pending}"
            : "no refresh-budget state";
        SssRefreshCounters counters = KeybindRegistry.RefreshCounters;

        response = $"{player.Nickname} ({player.PlayerId}): would receive {wouldReceive} entries now; {audit}; " +
            $"pressed latches: [{pressed}]; client ack version {userVersion} (0 = never acknowledged); " +
            $"settings tab open: {tabOpen}; {refresh}; process refresh counters " +
            $"requested={counters.Requested}, sent={counters.Sent}, coalesced={counters.Coalesced}, " +
            $"rate-limited={counters.RateLimited}, identical={counters.IdenticalSnapshots}.";
        return true;
    }

    private static bool Resend(ArraySegment<string> arguments, out string response)
    {
        if (!TryResolveTarget(arguments, "resend", out Player? player, out response))
        {
            return false;
        }

        KeybindRegistry.RefreshPlayer(player!);
        response = $"Queued a personalized settings refresh for {player!.Nickname} ({player.PlayerId}). " +
            "(Debounced and budgeted; check the audit via 'keybinds status'.)";
        return true;
    }

    private static bool Trace(ArraySegment<string> arguments, out string response)
    {
        string value = arguments.Count > 1 ? arguments.At(1).Trim().ToLowerInvariant() : string.Empty;
        switch (value)
        {
            case "on":
                KeybindRegistry.PressTrace = true;
                break;
            case "off":
                KeybindRegistry.PressTrace = false;
                break;
            default:
                response = "Usage: keybinds trace on|off";
                return false;
        }

        response = $"ServerKeybinds press trace is now {(KeybindRegistry.PressTrace ? "ON" : "OFF")}.";
        return true;
    }

    private static bool TryResolveTarget(ArraySegment<string> arguments, string subcommand, out Player? player, out string response)
    {
        player = null;
        if (arguments.Count < 2)
        {
            response = $"Usage: keybinds {subcommand} <id|name>";
            return false;
        }

        player = ResolvePlayer(arguments.At(1));
        if (player == null)
        {
            response = $"Player '{arguments.At(1)}' not found.";
            return false;
        }

        response = string.Empty;
        return true;
    }

    private static Player? ResolvePlayer(string value)
    {
        if (int.TryParse(value, out int playerId))
        {
            Player? byId = Player.Get(playerId);
            if (byId != null)
            {
                return byId;
            }
        }

        return Player.Get(value) ?? Player.GetByNickname(value, requireFullMatch: false);
    }

    private static string Short(string value) => string.IsNullOrEmpty(value)
        ? "none"
        : value.Substring(0, Math.Min(value.Length, 12));
}
