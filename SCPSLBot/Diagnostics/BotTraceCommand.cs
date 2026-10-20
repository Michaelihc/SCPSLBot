using CommandSystem;
using System;

namespace SCPSLBot.Diagnostics
{
    [CommandHandler(typeof(RemoteAdminCommandHandler))]
    internal sealed class BotTraceCommand : ICommand
    {
        public string Command => "bot_trace";
        public string[] Aliases => Array.Empty<string>();
        public string Description => "Record the round to a JSONL trace for tools/round-map: bot_trace start|stop|status.";

        public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
        {
            if (!sender.CheckPermission(PlayerPermissions.FacilityManagement, out response))
                return false;

            string action = arguments.Count > 0 ? arguments.At(0).ToLowerInvariant() : "status";
            var recorder = RoundTraceRecorder.Instance;
            switch (action)
            {
                case "start":
                    response = recorder.Start();
                    return true;
                case "stop":
                    response = recorder.Stop();
                    return true;
                case "status":
                    response = recorder.Status();
                    return true;
                default:
                    response = "Usage: bot_trace start|stop|status";
                    return false;
            }
        }
    }
}
