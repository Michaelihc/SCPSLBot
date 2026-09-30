using CommandSystem;
using PlayerRoles;
using PlayerRoles.FirstPersonControl;
using SCPSLBot.Warmup;
using System;

namespace SCPSLBot.AI.Commands
{
    [CommandHandler(typeof(RemoteAdminCommandHandler))]
    internal class BotAddCommand : ICommand
    {
        public string Command => "bot_add";

        public string[] Aliases => new string[] { };

        public string Description => "Spawn independent SCPSLBot dummies whose RA role is not reconciled: bot_add [count] [role].";

        public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
        {
            if (!sender.CheckPermission(PlayerPermissions.PlayersManagement, out response))
            {
                return false;
            }

            if (!BotCommandParsing.TryParseAdd(arguments, out var count, out var roleToken, out response))
            {
                return false;
            }

            if (count == 0)
            {
                return WarmupManager.Instance.TryAddIndependentBot(out response);
            }

            var role = RoleTypeId.ChaosRifleman;
            if (roleToken != null && !TryParseBotRole(roleToken, out role))
            {
                response = $"Role '{roleToken}' is not a bot-compatible first-person role. {BotCommandParsing.AddUsage}";
                return false;
            }

            return WarmupManager.Instance.TryAddIndependentBots(count, role, out response);
        }

        private static bool TryParseBotRole(string token, out RoleTypeId role)
        {
            return Enum.TryParse(token, true, out role)
                   && Enum.IsDefined(typeof(RoleTypeId), role)
                   && role.TryGetRoleTemplate<FpcStandardRoleBase>(out _);
        }
    }
}
