using System;
using System.Linq;
using CommandSystem;
using LabApi.Features.Permissions;
using StatsBots.Services;

namespace StatsBots.Commands;

[CommandHandler(typeof(RemoteAdminCommandHandler))]
internal sealed class StatsBotsCommand : ICommand
{
    public string Command => "statsbots";
    public string[] Aliases => new[] { "sb" };
    public string Description => "Manage StatsBots warmup titles by exact full UserId.";
    public bool SanitizeResponse => false;

    public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
    {
        StatsBotsPlugin? plugin = StatsBotsPlugin.Instance;
        StatsBotsRuntime? runtime = plugin?.Runtime;
        if (plugin == null || runtime == null)
        {
            response = "StatsBots is not enabled.";
            return false;
        }
        if (!sender.HasPermission(plugin.Config.AdminPermission))
        {
            response = "Missing permission: " + plugin.Config.AdminPermission;
            return false;
        }
        if (arguments.Count < 2)
        {
            response = "Usage: statsbots status <fullUserId> | grant <fullUserId> <titleId> | revoke <fullUserId> <titleId>";
            return false;
        }

        string verb = arguments.At(0).ToLowerInvariant();
        string userId = arguments.At(1);
        switch (verb)
        {
            case "status":
                response = runtime.TitleStatus(userId);
                return response.IndexOf("provider=ready", StringComparison.Ordinal) >= 0;
            case "grant" when arguments.Count >= 3:
                return runtime.TryGrant(userId, arguments.At(2), out response) == Integration.ProviderState.Ready;
            case "revoke" when arguments.Count >= 3:
                return runtime.TryRevoke(userId, arguments.At(2), out response) == Integration.ProviderState.Ready;
            default:
                response = "Usage: statsbots status <fullUserId> | grant <fullUserId> <titleId> | revoke <fullUserId> <titleId>";
                return false;
        }
    }
}
