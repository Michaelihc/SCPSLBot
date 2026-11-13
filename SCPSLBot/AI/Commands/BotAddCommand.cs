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

            int maxBotCount = LabApiPlugin.Instance.Config.MaxBotCount;
            if (BotManager.Instance.BotPlayers.Count >= maxBotCount)
            {
                response = $"Independent bot cap reached ({maxBotCount}).";
                return false;
            }
            var hub = BotManager.Instance.AddUnassignedBotPlayer("SCPSL Manual Bot");
            if (hub == null) { response = "Bot creation failed."; return false; }
            hub.roleManager.ServerSetRole(RoleTypeId.ChaosRifleman, RoleChangeReason.RemoteAdmin, RoleIntro.Flags(RoleSpawnFlags.All, IntroIntent.Hidden));
            response = $"Spawned independent bot player_id={hub.PlayerId}.";
            return true;
        }
    }
}
