using System;
using CommandSystem;
using SCPSLBot.Navigation;

namespace SCPSLBot.AI.Commands;

[CommandHandler(typeof(RemoteAdminCommandHandler))]
internal sealed class BotStatusCommand : ICommand
{
    public string Command => "bot_status";
    public string[] Aliases => Array.Empty<string>();
    public string Description => "Show bot AI and navigation readiness.";
    public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
    {
        if (!sender.CheckPermission(PlayerPermissions.FacilityManagement, out response)) return false;
        var manager = BotManager.Instance;
        var navigation = NavigationSystem.Instance;
        response = (Api.BotServerPolicy.PopulationStatus?.Invoke() ?? $"tracked={manager.BotPlayers.Count}; ")
            + $"ai_runner_running={manager.RunnerIsRunning}; "
            + $"nav_ready={navigation.IsReadyForCurrentMap}; nav_backend={navigation.Backend?.Name ?? "none"}; "
            + $"nav_error={navigation.LastLoadError ?? "none"}; ai_last_fault={manager.LastRunnerFault ?? "none"}; "
            + (navigation.Backend?.Diagnostics ?? "nav_diagnostics=none");
        return true;
    }
}
