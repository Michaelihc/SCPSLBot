using CommandSystem;
using PlayerRoles;
using System;

namespace SCPSLBot.AI.Commands
{
    [CommandHandler(typeof(RemoteAdminCommandHandler))]
    internal class BotAddCommand : ICommand
    {
        public string Command => "bot_add";

        public string[] Aliases => new string[] { };

        public string Description => "Spawn an independent SCPSLBot dummy whose RA role is not reconciled.";

        public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
        {
            if (!sender.CheckPermission(PlayerPermissions.PlayersManagement, out response))
            {
                return false;
            }

            if (BotManager.Instance.BotPlayers.Count >= 10) { response = "Independent bot cap reached (10)."; return false; }
            var hub = BotManager.Instance.AddUnassignedBotPlayer("SCPSL Manual Bot");
            if (hub == null) { response = "Bot creation failed."; return false; }
            hub.roleManager.ServerSetRole(RoleTypeId.ChaosRifleman, RoleChangeReason.RemoteAdmin);
            response = $"Spawned independent bot player_id={hub.PlayerId}.";
            return true;
        }
    }
}
