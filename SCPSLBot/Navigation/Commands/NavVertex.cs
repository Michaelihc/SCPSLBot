using CommandSystem;
using System;
using System.Linq;

namespace SCPSLBot.Navigation.Commands
{
    [CommandHandler(typeof(Nav))]
    internal class NavVertex : ParentCommand
    {
        public override string Command { get; } = "vertex";

        public override string[] Aliases { get; } = new string[] { };

        public override string Description { get; } = "Manipulates navigation mesh vertices.";

        public override void LoadGeneratedCommands()
        {
            this.RegisterCommand(new NavVertexCreateCommand());
            this.RegisterCommand(new NavVertexDeleteCommand());
        }

        protected override bool ExecuteParent(ArraySegment<string> arguments, ICommandSender sender, out string response)
        {
            if (!sender.CheckPermission(PlayerPermissions.GameplayData, out response))
            {
                return false;
            }

            var subCommands = Commands.Keys.ToArray();
            response = $"Please specify a valid subcommand. ({string.Join("/", subCommands)})";
            return false;
        }
    }
}
