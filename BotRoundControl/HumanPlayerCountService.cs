using CentralAuth;
using CommandSystem.Commands.Shared;
using LabApi.Events.Arguments.ServerEvents;
using LabApi.Events.Handlers;
using System.Globalization;
using System.Text.RegularExpressions;

namespace SCPSLBot.Health;

/// <summary>Keeps the native roster header consistent with its non-dummy entries.</summary>
internal sealed class HumanPlayerCountService
{
    private static readonly Regex Header = new(@"\A(?<prefix><color=cyan>)?List of players \(\d+\):", RegexOptions.CultureInvariant);

    internal void Enable() => ServerEvents.CommandExecuted += OnCommandExecuted;
    internal void Disable() => ServerEvents.CommandExecuted -= OnCommandExecuted;

    private static void OnCommandExecuted(CommandExecutedEventArgs ev)
    {
        if (!ev.ExecutedSuccessfully || ev.Command is not PlayersCommand || ev.Response == null)
            return;

        // Count native hubs, including humans still authenticating. Nicknames may contain
        // newlines, so counting lines in the rendered roster could falsely report an empty server.
        int humans = 0;
        foreach (ReferenceHub hub in ReferenceHub.AllHubs)
            if (hub != null && hub.Mode is not ClientInstanceMode.DedicatedServer and not ClientInstanceMode.Dummy)
                humans++;

        ev.Response = Header.Replace(ev.Response,
            match => match.Groups["prefix"].Value + "List of players (" + humans.ToString(CultureInfo.InvariantCulture) + "):", 1);
    }
}
