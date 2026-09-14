using CommandSystem;
using SCPSLBot.Tests.Navigation;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SCPSLBot.Tests.Commands
{
    [CommandHandler(typeof(GameConsoleCommandHandler))]
    internal class TestsCommand : ParentCommand
    {
        public override string Command { get; } = "tests";

        public override string[] Aliases { get; } = new string[] { };

        public override string Description { get; } = "Run tests.";

        public override void LoadGeneratedCommands()
        {
            RegisterCommand(new NavigationSystemTests());
        }

        protected override bool ExecuteParent(ArraySegment<string> arguments, ICommandSender sender, out string response)
        {
            response = $"Please specify a valid test command. ({string.Join("/", Commands.Keys.ToArray())})";
            return false;
        }

        public TestsCommand()
        {
            LoadGeneratedCommands();
        }
    }
}
