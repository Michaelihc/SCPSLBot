using CommandSystem;
using SCPSLBot.Warmup;
using System;

namespace SCPSLBot.Warmup.Commands
{
    [CommandHandler(typeof(RemoteAdminCommandHandler))]
    internal sealed class BotWarmupCommand : ICommand
    {
        public string Command => "bot_warmup";

        public string[] Aliases => new[] { "bot_mode", "warmup", "warmup_mode" };

        public string Description => "Gets or sets bot warmup mode: none, standard.";

        public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
        {
            if (arguments.Count < 1)
            {
                response = $"Current warmup mode is {WarmupManager.Instance.Mode}. Use: bot_warmup none|standard or warmup mode none|standard";
                return true;
            }

            var modeArgument = arguments.At(0);
            if (string.Equals(modeArgument, "mode", StringComparison.OrdinalIgnoreCase))
            {
                if (arguments.Count < 2)
                {
                    response = $"Current warmup mode is {WarmupManager.Instance.Mode}. Use: warmup mode none|standard";
                    return true;
                }

                modeArgument = arguments.At(1);
            }

            if (!sender.CheckPermission(PlayerPermissions.PlayersManagement, out response))
            {
                return false;
            }

            return WarmupManager.Instance.TrySetMode(modeArgument, out response);
        }
    }
}
