using CommandSystem;
using SCPSLBot.AI.FirstPersonControl.Combat;
using System;

namespace SCPSLBot.AI.Commands
{
    [CommandHandler(typeof(RemoteAdminCommandHandler))]
    internal class BotDifficultyCommand : ICommand
    {
        public string Command => "bot_difficulty";

        public string[] Aliases => new[] { "bot_diff" };

        public string Description => "Sets bot combat difficulty: easy, normal, hard, hardest.";

        public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
        {
            if (arguments.Count < 1)
            {
                response = $"Current bot difficulty is {FpcBotCombat.Difficulty}. Use: bot_difficulty easy|normal|hard|hardest";
                return true;
            }

            if (!sender.CheckPermission(PlayerPermissions.PlayersManagement, out response))
            {
                return false;
            }

            if (!Enum.TryParse(arguments.At(0), true, out BotCombatDifficulty difficulty))
            {
                response = "Unknown difficulty. Use: easy, normal, hard, hardest.";
                return false;
            }

            FpcBotCombat.Difficulty = difficulty;
            response = $"Bot difficulty set to {difficulty}.";
            return true;
        }
    }
}
