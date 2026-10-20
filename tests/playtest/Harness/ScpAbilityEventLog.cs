using LabApi.Events;
using LabApi.Events.Arguments.Interfaces;
using LabApi.Events.Handlers;
using LabApi.Features.Console;
using System;
using System.Collections.Generic;

namespace SCPSLBot.PlaytestScenarios.Harness;

/// <summary>
/// Test-only evidence: logs the native LabAPI events of completed SCP ability uses as
/// <c>[ScpAbilityEvent] event=… player=… role=… pos=…</c> so client recordings can be matched to
/// server-confirmed ability activations.
/// </summary>
internal static class ScpAbilityEventLog
{
    private static readonly List<Action> Unsubscribe = new();

    public static void Enable()
    {
        Disable();
        Add<LabApi.Events.Arguments.Scp049Events.Scp049UsedSenseEventArgs>(h => Scp049Events.UsedSense += h, h => Scp049Events.UsedSense -= h, "049.Sense");
        Add<LabApi.Events.Arguments.Scp049Events.Scp049UsedDoctorsCallEventArgs>(h => Scp049Events.UsedDoctorsCall += h, h => Scp049Events.UsedDoctorsCall -= h, "049.DoctorsCall");
        Add<LabApi.Events.Arguments.Scp049Events.Scp049AttackedEventArgs>(h => Scp049Events.Attacked += h, h => Scp049Events.Attacked -= h, "049.Attack");
        Add<LabApi.Events.Arguments.Scp049Events.Scp049ResurrectedBodyEventArgs>(h => Scp049Events.ResurrectedBody += h, h => Scp049Events.ResurrectedBody -= h, "049.Resurrected");
        Add<LabApi.Events.Arguments.Scp0492Events.Scp0492ConsumedCorpseEventArgs>(h => Scp0492Events.ConsumedCorpse += h, h => Scp0492Events.ConsumedCorpse -= h, "0492.ConsumedCorpse");
        Add<LabApi.Events.Arguments.Scp096Events.Scp096EnragedEventArgs>(h => Scp096Events.Enraged += h, h => Scp096Events.Enraged -= h, "096.Enraged");
        Add<LabApi.Events.Arguments.Scp096Events.Scp096ChargedEventArgs>(h => Scp096Events.Charged += h, h => Scp096Events.Charged -= h, "096.Charged");
        Add<LabApi.Events.Arguments.Scp096Events.Scp096PriedGateEventArgs>(h => Scp096Events.PriedGate += h, h => Scp096Events.PriedGate -= h, "096.PriedGate");
        Add<LabApi.Events.Arguments.Scp106Events.Scp106ChangedStalkModeEventArgs>(h => Scp106Events.ChangedStalkMode += h, h => Scp106Events.ChangedStalkMode -= h, "106.StalkMode");
        Add<LabApi.Events.Arguments.Scp106Events.Scp106UsedHunterAtlasEventArgs>(h => Scp106Events.UsedHunterAtlas += h, h => Scp106Events.UsedHunterAtlas -= h, "106.HunterAtlas");
        Add<LabApi.Events.Arguments.Scp106Events.Scp106TeleportedPlayerEvent>(h => Scp106Events.TeleportedPlayer += h, h => Scp106Events.TeleportedPlayer -= h, "106.PocketTeleport");
        Add<LabApi.Events.Arguments.Scp939Events.Scp939FocusedEventArgs>(h => Scp939Events.Focused += h, h => Scp939Events.Focused -= h, "939.Focused");
        Add<LabApi.Events.Arguments.Scp939Events.Scp939LungedEventArgs>(h => Scp939Events.Lunged += h, h => Scp939Events.Lunged -= h, "939.Lunged");
        Add<LabApi.Events.Arguments.Scp939Events.Scp939CreatedAmnesticCloudEventArgs>(h => Scp939Events.CreatedAmnesticCloud += h, h => Scp939Events.CreatedAmnesticCloud -= h, "939.AmnesticCloud");
        Add<LabApi.Events.Arguments.Scp939Events.Scp939AttackedEventArgs>(h => Scp939Events.Attacked += h, h => Scp939Events.Attacked -= h, "939.Claw");
        Add<LabApi.Events.Arguments.Scp3114Events.Scp3114DisguisedEventArgs>(h => Scp3114Events.Disguised += h, h => Scp3114Events.Disguised -= h, "3114.Disguised");
        Add<LabApi.Events.Arguments.Scp3114Events.Scp3114RevealedEventArgs>(h => Scp3114Events.Revealed += h, h => Scp3114Events.Revealed -= h, "3114.Revealed");
        Add<LabApi.Events.Arguments.Scp3114Events.Scp3114StrangleStartedEventArgs>(h => Scp3114Events.StrangleStarted += h, h => Scp3114Events.StrangleStarted -= h, "3114.StrangleStarted");
        Add<LabApi.Events.Arguments.Scp173Events.Scp173SnappedEventArgs>(h => Scp173Events.Snapped += h, h => Scp173Events.Snapped -= h, "173.Snapped");
        Add<LabApi.Events.Arguments.Scp173Events.Scp173TeleportedEventArgs>(h => Scp173Events.Teleported += h, h => Scp173Events.Teleported -= h, "173.Blink");
        Add<LabApi.Events.Arguments.Scp173Events.Scp173CreatedTantrumEventArgs>(h => Scp173Events.CreatedTantrum += h, h => Scp173Events.CreatedTantrum -= h, "173.Tantrum");
        Add<LabApi.Events.Arguments.Scp173Events.Scp173BreakneckSpeedChangedEventArgs>(h => Scp173Events.BreakneckSpeedChanged += h, h => Scp173Events.BreakneckSpeedChanged -= h, "173.Breakneck");
        Logger.Info($"[ScpAbilityEvent] logging {Unsubscribe.Count} native SCP ability events");
    }

    public static void Disable()
    {
        foreach (var unsubscribe in Unsubscribe)
        {
            unsubscribe();
        }

        Unsubscribe.Clear();
    }

    private static void Add<T>(Action<LabEventHandler<T>> subscribe, Action<LabEventHandler<T>> unsubscribe, string name) where T : EventArgs
    {
        LabEventHandler<T> handler = ev => Log(name, ev);
        subscribe(handler);
        Unsubscribe.Add(() => unsubscribe(handler));
    }

    private static void Log(string name, object ev)
    {
        try
        {
            var player = (ev as IPlayerEvent)?.Player;
            var target = (ev as ITargetEvent)?.Target;
            var position = player?.Position ?? default;
            Logger.Info($"[ScpAbilityEvent] event={name} player={player?.Nickname ?? "none"} role={player?.Role.ToString() ?? "none"}"
                + $" target={target?.Nickname ?? "none"} pos=({position.x:F1},{position.y:F1},{position.z:F1})");
        }
        catch (Exception exception)
        {
            Logger.Error($"[ScpAbilityEvent] {name} log failed: {exception.Message}");
        }
    }
}
