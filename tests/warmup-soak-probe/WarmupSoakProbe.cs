using CommandSystem;
using Interactables.Interobjects.DoorUtils;
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.Arguments.ServerEvents;
using LabApi.Events.Handlers;
using LabApi.Features;
using LabApi.Features.Wrappers;
using LabApi.Loader.Features.Plugins;
using MapGeneration;
using MEC;
using PlayerStatsSystem;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace WarmupSoakProbe;

public sealed class ProbeConfig
{
    /// <summary>Map seed for coverage beyond the host's fixed seed; zero keeps the host seed.</summary>
    public int MapSeed { get; set; }
}

/// <summary>
/// Test-only recorder of native player state and events. It has no reference to the plugins under
/// comparison, so baseline and candidate builds are recorded through identical native observations.
/// </summary>
public sealed class WarmupSoakProbePlugin : Plugin<ProbeConfig>
{
    private const float SampleSeconds = 0.25f;
    private static WarmupSoakProbePlugin? instance;
    private CoroutineHandle sampler;
    private StreamWriter? writer;

    public override string Name => "WarmupSoakProbe";
    public override string Description => "Native warmup population, combat and elevator recorder for comparison runs.";
    public override string Author => "metarepo";
    public override Version Version => new(1, 0, 0);
    public override Version RequiredApiVersion => new(LabApiProperties.CompiledVersion);

    public override void Enable()
    {
        instance = this;
        ServerEvents.MapGenerating += OnMapGenerating;
        PlayerEvents.Hurt += OnHurt;
        PlayerEvents.Death += OnDeath;
        PlayerEvents.Spawned += OnSpawned;
        PlayerEvents.ShotWeapon += OnShot;
        PlayerEvents.DryFiredWeapon += OnDryFired;
        PlayerEvents.ReloadingWeapon += OnReloading;
        PlayerEvents.ReloadedWeapon += OnReloaded;
        PlayerEvents.AimedWeapon += OnAimed;
        PlayerEvents.InteractedDoor += OnDoor;
        PlayerEvents.InteractedElevator += OnElevator;
        PlayerEvents.PickedUpItem += OnPickedUp;
        PlayerEvents.DroppedItem += OnDropped;
        PlayerEvents.UsedItem += OnUsed;
        ServerEvents.ElevatorSequenceChanged += OnElevatorSequence;
    }

    public override void Disable()
    {
        StopRecording();
        ServerEvents.ElevatorSequenceChanged -= OnElevatorSequence;
        PlayerEvents.UsedItem -= OnUsed;
        PlayerEvents.DroppedItem -= OnDropped;
        PlayerEvents.PickedUpItem -= OnPickedUp;
        PlayerEvents.InteractedElevator -= OnElevator;
        PlayerEvents.InteractedDoor -= OnDoor;
        PlayerEvents.AimedWeapon -= OnAimed;
        PlayerEvents.ReloadedWeapon -= OnReloaded;
        PlayerEvents.ReloadingWeapon -= OnReloading;
        PlayerEvents.DryFiredWeapon -= OnDryFired;
        PlayerEvents.ShotWeapon -= OnShot;
        PlayerEvents.Spawned -= OnSpawned;
        PlayerEvents.Death -= OnDeath;
        PlayerEvents.Hurt -= OnHurt;
        ServerEvents.MapGenerating -= OnMapGenerating;
        instance = null;
    }

    internal static string Start(string path)
    {
        if (instance == null) return "probe not enabled";
        instance.StopRecording();
        instance.writer = new StreamWriter(path, append: true, new UTF8Encoding(false)) { AutoFlush = true };
        instance.sampler = Timing.RunCoroutine(instance.Sample());
        instance.Write("{\"k\":\"start\"," + Clock() + ",\"plugins\":[" + string.Join(",",
            LabApi.Loader.PluginLoader.Plugins.Keys.Select(p => Quote(p.Name + " " + p.Version))) + "]}");
        return "recording " + path;
    }

    internal static string Stop()
    {
        instance?.StopRecording();
        return "stopped";
    }

    internal static string Mark(string text)
    {
        instance?.Write("{\"k\":\"mark\"," + Clock() + ",\"text\":" + Quote(text) + "}");
        return "marked";
    }

    private void StopRecording()
    {
        if (sampler.IsRunning) Timing.KillCoroutines(sampler);
        writer?.Dispose();
        writer = null;
    }

    private void Write(string line)
    {
        try
        {
            writer?.WriteLine(line);
        }
        catch (Exception ex)
        {
            LabApi.Features.Console.Logger.Error($"[WarmupSoakProbe] write failed: {ex.Message}");
            writer = null;
        }
    }

    private IEnumerator<float> Sample()
    {
        while (writer != null)
        {
            try
            {
                var players = Player.List.Where(p => !p.IsHost).Select(SampleJson);
                var lifts = Elevator.List.Select(e => "{\"g\":\"" + e.Group + "\",\"seq\":\"" + e.CurrentSequence +
                    "\",\"lvl\":" + e.CurrentDestinationLevel + ",\"lock\":" + (int)e.AnyDoorLockedReason + "}");
                Write("{\"k\":\"s\"," + Clock() + ",\"p\":[" + string.Join(",", players) + "],\"e\":[" + string.Join(",", lifts) + "]}");
            }
            catch (Exception ex)
            {
                Write("{\"k\":\"err\"," + Clock() + ",\"text\":" + Quote(ex.Message) + "}");
            }

            yield return Timing.WaitForSeconds(SampleSeconds);
        }
    }

    private static string SampleJson(Player p)
    {
        Vector3 pos = p.Position;
        bool inRoom = RoomUtils.TryGetRoom(pos, out RoomIdentifier room) && room != null;
        string zone = inRoom ? room.Zone.ToString() : "None";
        string roomName = inRoom ? room.Name.ToString() : "None";
        string lift = Elevator.List.FirstOrDefault(e => e.WorldSpaceBounds.Contains(pos))?.Group.ToString() ?? string.Empty;
        FirearmItem? gun = p.CurrentItem as FirearmItem;
        string held = p.CurrentItem == null ? "None" : p.CurrentItem.Type.ToString();
        return FormattableString.Invariant(
            $"{{\"id\":{p.PlayerId},\"n\":{Quote(p.Nickname)},\"d\":{Bool(p.IsDummy)},\"r\":\"{p.Role}\",\"l\":{p.LifeId},\"z\":\"{zone}\",\"rm\":\"{roomName}\",\"x\":{pos.x:F2},\"y\":{pos.y:F2},\"w\":{pos.z:F2},\"h\":{p.Health:F0},\"held\":\"{held}\",\"ammo\":{(gun == null ? -1 : gun.StoredAmmo)},\"rel\":{Bool(gun != null && gun.IsReloading)},\"lift\":\"{lift}\"}}");
    }

    private void OnMapGenerating(MapGeneratingEventArgs ev)
    {
        if (Config != null && Config.MapSeed > 0) ev.Seed = Config.MapSeed;
    }

    private void OnHurt(PlayerHurtEventArgs ev)
    {
        float dealt = ev.DamageHandler is StandardDamageHandler standard
            ? standard.DealtHealthDamage + standard.AbsorbedAhpDamage + standard.AbsorbedHumeDamage
            : 0f;
        Event("hurt", ev.Player, FormattableString.Invariant(
            $"\"by\":{Id(ev.Attacker)},\"dmg\":{dealt:F1},\"type\":\"{ev.DamageHandler.GetType().Name}\",\"hp\":{ev.Player.Health:F0}"));
    }

    private void OnDeath(PlayerDeathEventArgs ev) => Event("death", ev.Player, $"\"by\":{Id(ev.Attacker)},\"type\":\"{ev.DamageHandler.GetType().Name}\"");
    private void OnSpawned(PlayerSpawnedEventArgs ev) => Event("spawn", ev.Player, $"\"role\":\"{ev.Role.RoleTypeId}\"");
    private void OnShot(PlayerShotWeaponEventArgs ev) => Event("shot", ev.Player, $"\"gun\":\"{ev.FirearmItem?.Type}\",\"ammo\":{ev.FirearmItem?.StoredAmmo ?? -1}");
    private void OnDryFired(PlayerDryFiredWeaponEventArgs ev) => Event("dry", ev.Player, "\"gun\":\"\"");
    private void OnReloading(PlayerReloadingWeaponEventArgs ev) => Event("reloading", ev.Player, $"\"ammo\":{ev.FirearmItem?.StoredAmmo ?? -1}");
    private void OnReloaded(PlayerReloadedWeaponEventArgs ev) => Event("reloaded", ev.Player, $"\"ammo\":{ev.FirearmItem?.StoredAmmo ?? -1}");
    private void OnAimed(PlayerAimedWeaponEventArgs ev) => Event("aim", ev.Player, $"\"on\":{Bool(ev.Aiming)}");
    private void OnDoor(PlayerInteractedDoorEventArgs ev) => Event("door", ev.Player, $"\"door\":\"{ev.Door?.DoorName}\",\"can\":{Bool(ev.CanOpen)},\"open\":{Bool(ev.Door?.IsOpened ?? false)},\"lock\":{(int)(ev.Door?.LockReason ?? DoorLockReason.None)}");
    private void OnElevator(PlayerInteractedElevatorEventArgs ev) => Event("lift", ev.Player, $"\"g\":\"{ev.Elevator?.Group}\"");
    private void OnPickedUp(PlayerPickedUpItemEventArgs ev) => Event("pickup", ev.Player, $"\"item\":\"{ev.Item?.Type}\"");
    private void OnDropped(PlayerDroppedItemEventArgs ev) => Event("drop", ev.Player, $"\"item\":\"{ev.Pickup?.Type}\"");
    private void OnUsed(PlayerUsedItemEventArgs ev) => Event("used", ev.Player, $"\"item\":\"{ev.UsableItem?.Type}\"");

    private void OnElevatorSequence(ElevatorSequenceChangedEventArgs ev)
    {
        var riders = Player.List
            .Where(p => !p.IsHost && ev.Elevator.WorldSpaceBounds.Contains(p.Position))
            .Select(p => p.PlayerId.ToString(CultureInfo.InvariantCulture));
        Write("{\"k\":\"liftseq\"," + Clock() + ",\"g\":\"" + ev.Elevator.Group + "\",\"from\":\"" + ev.OldSequence +
            "\",\"to\":\"" + ev.NewSequence + "\",\"riders\":[" + string.Join(",", riders) + "]}");
    }

    private void Event(string kind, Player? player, string fields)
    {
        if (writer == null || player == null) return;
        Vector3 pos = player.Position;
        Write("{\"k\":\"" + kind + "\"," + Clock() + ",\"id\":" + player.PlayerId + ",\"r\":\"" + player.Role + "\"," +
            FormattableString.Invariant($"\"x\":{pos.x:F2},\"y\":{pos.y:F2},\"w\":{pos.z:F2},") + fields + "}");
    }

    private static string Id(Player? player) => player == null ? "0" : player.PlayerId.ToString(CultureInfo.InvariantCulture);
    private static string Clock() => "\"t\":" + Time.realtimeSinceStartup.ToString("F3", CultureInfo.InvariantCulture);
    private static string Bool(bool value) => value ? "true" : "false";
    private static string Quote(string? value) => "\"" + (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}

[CommandHandler(typeof(RemoteAdminCommandHandler))]
public sealed class SoakProbeCommand : ICommand
{
    public string Command => "soakprobe";
    public string[] Aliases => Array.Empty<string>();
    public string Description => "start <path> | stop | mark <text>";

    public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
    {
        string verb = arguments.Count > 0 ? arguments.At(0) : string.Empty;
        string rest = string.Join(" ", arguments.Skip(1));
        response = verb switch
        {
            "start" => WarmupSoakProbePlugin.Start(rest),
            "stop" => WarmupSoakProbePlugin.Stop(),
            "mark" => WarmupSoakProbePlugin.Mark(rest),
            _ => Description,
        };
        return true;
    }
}
