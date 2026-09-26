using System;
using CommandSystem;

namespace ScpslPluginStarter.Commands;

[CommandHandler(typeof(RemoteAdminCommandHandler))]
[CommandHandler(typeof(GameConsoleCommandHandler))]
public sealed class SafezoneStatusCommand : ICommand
{
    public string Command => "safezone";
    public string[] Aliases => Array.Empty<string>();
    public string Description => "Read-only WarmupSafezone status: safezone status";

    public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
    {
        if (arguments.Count < 1 || !string.Equals(arguments.At(0), "status", StringComparison.OrdinalIgnoreCase))
        {
            response = "Usage: safezone status";
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
            + $"visual_toys={plugin.VisualToyCount} scp914_gate={plugin.Scp914GateDescription} "
            + $"classd_cells_tile={plugin.ClassDCellsDescription}";
        return true;
    }
}
