using CommandSystem;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SCPSLBot.Navigation.Commands
{
    [CommandHandler(typeof(RemoteAdminCommandHandler))]
    internal class Nav : ParentCommand
    {
        public override string Command { get; } = "nav";

        public override string[] Aliases { get; } = new string[] { };

        public override string Description { get; } = "Navigation diagnostics (status/rebuild/probe/path) and the authored cell editor.";

        public override void LoadGeneratedCommands()
        {
            this.RegisterCommand(new NavStatusCommand());
            this.RegisterCommand(new NavRebuildCommand());
            this.RegisterCommand(new NavProbeCommand());
            this.RegisterCommand(new NavPathCommand());
            // Authored-backend cell editor (kept for one release behind navigation.backend = authored).
            this.RegisterCommand(new NavEditCommand());
            this.RegisterCommand(new NavLoadCommand());
            this.RegisterCommand(new NavSaveCommand());
            this.RegisterCommand(new NavVertex());
            //this.RegisterCommand(new NavCell());
        }

        protected override bool ExecuteParent(ArraySegment<string> arguments, ICommandSender sender, out string response)
        {
            if (!sender.CheckPermission(PlayerPermissions.GameplayData, out response))
            {
                return false;
            }

            response = $"Please specify a valid subcommand. ({string.Join("/", Commands.Keys.ToArray())})";
            return false;
        }
    }
}
