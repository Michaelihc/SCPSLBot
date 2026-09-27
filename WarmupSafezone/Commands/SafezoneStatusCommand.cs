using System;
using CommandSystem;
using LabApi.Features.Wrappers;

namespace ScpslPluginStarter.Commands;

[CommandHandler(typeof(RemoteAdminCommandHandler))]
[CommandHandler(typeof(GameConsoleCommandHandler))]
public sealed class SafezoneStatusCommand : ICommand
{
    public string Command => "safezone";
    public string[] Aliases => Array.Empty<string>();
    public string Description => "Read-only WarmupSafezone status: safezone status [playerId]";

    public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
    {
        if (arguments.Count < 1 || !string.Equals(arguments.At(0), "status", StringComparison.OrdinalIgnoreCase))
        {
            response = "Usage: safezone status [playerId]";
            return false;
        }

        WarmupSafezonePlugin? plugin = WarmupSafezonePlugin.Instance;
        if (plugin == null)
        {
            response = "WarmupSafezone is not enabled.";
            return false;
        }

        WarmupSafezoneConfig config = plugin.Config!;
        response = $"WarmupSafezone {plugin.Version} enabled={config.Enabled} scp914={config.Scp914SafezoneEnabled} "
            + $"classd_cells={config.ClassDCellsSafezoneEnabled} visuals={config.SafezoneVisualsEnabled} "
            + $"blocked_projectiles=[{string.Join(",", config.SafezoneBlockedProjectiles ?? new())}] "
            + $"blocked_drops=[{string.Join(",", config.SafezoneBlockedDrops ?? new())}] "
            + $"visual_toys={plugin.VisualToyCount} scp914_gate={plugin.Scp914GateDescription} "
            + $"classd_cells_tile={plugin.ClassDCellsDescription}";
        if (arguments.Count >= 2)
        {
            if (!int.TryParse(arguments.At(1), out int playerId) || !Player.TryGet(playerId, out Player? player) || player == null)
            {
                response += $" player={arguments.At(1)} membership=unknown-player";
                return false;
            }

            string membership = plugin.Volumes?.Resolve(player).ToString() ?? "unavailable";
            UnityEngine.Vector3 p = player.Position;
            response += FormattableString.Invariant($" player={playerId} role={player.Role} position=({p.x:0.##},{p.y:0.##},{p.z:0.##}) membership={membership}");
        }
        return true;
    }
}
