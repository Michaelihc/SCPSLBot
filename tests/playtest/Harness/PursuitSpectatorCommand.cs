using CommandSystem;
using LabApi.Features.Wrappers;
using PlayerRoles.Spectating;
using System;
using System.Linq;

namespace SCPSLBot.PlaytestScenarios.Harness;

/// <summary>Read native spectator replication; never select a target on the client's behalf.</summary>
[CommandHandler(typeof(GameConsoleCommandHandler))]
public sealed class PursuitSpectatorCommand : ICommand
{
    public string Command => "pursuitspectator";
    public string[] Aliases => Array.Empty<string>();
    public string Description => "Observe native spectator selection: <spectator id> <target id>.";

    public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
    {
        if (!sender.CheckPermission(PlayerPermissions.GameplayData, out response)) return false;
        if (arguments.Count != 2 || !int.TryParse(arguments.At(0), out var spectatorId)
            || !int.TryParse(arguments.At(1), out var targetId))
        {
            response = Description;
            return false;
        }
        var spectator = Player.List.FirstOrDefault(player => player.PlayerId == spectatorId);
        var target = Player.List.FirstOrDefault(player => player.PlayerId == targetId);
        var selected = spectator?.ReferenceHub.roleManager.CurrentRole is SpectatorRole role
            && target != null && role.SyncedSpectatedNetId == target.ReferenceHub.netId;
        response = $"PURSUIT_SPECTATOR selected={selected}";
        return true;
    }
}
