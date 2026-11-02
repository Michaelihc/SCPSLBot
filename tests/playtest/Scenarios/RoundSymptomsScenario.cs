using LabApi.Features.Wrappers;
using MapGeneration;
using PlayerRoles;
using PlayerRoles.PlayableScps.Scp939;
using PlaytestHarness.Core;
using PlaytestHarness.Probes;
using SCPSLBot.PlaytestScenarios.Harness;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;

namespace SCPSLBot.PlaytestScenarios.Scenarios;

/// <summary>
/// A native normal round observed the way a player sees it. Ten round bots receive native roles, the
/// SCP slots are forced to the roles under test straight after spawn, and the round plays on without
/// orders. Humans are then released and replaced by a native NTF wave and a native Chaos wave. Every
/// life is traced from world state; <c>bot_debug</c> only labels what a stopped bot was doing.
/// </summary>
public class RoundSymptomsScenario : Scenario
{
    private const int RoundBots = 10;
    private const float PopulationSeconds = 360f;
    private const float WaveSeconds = 180f;
    private const float SampleSeconds = 2f;
    private const float StateLogSeconds = 10f;

    protected virtual IReadOnlyList<RoleTypeId> ScpRoles => new[] { RoleTypeId.Scp106, RoleTypeId.Scp939, RoleTypeId.Scp049 };

    public override string Name => "scpslbot-round-symptoms";
    public override string Description => "Native round and waves: reported role travel, elevator and SCP stall symptoms, per life.";
    public override FidelityRange Supported => FidelityRange.Only(Fidelity.Standard);
    public override bool IncludeInRunAll => false;
    public override float TimeoutSeconds => PopulationSeconds + 2f * WaveSeconds + 90f;

    public override IEnumerator<float> Run(ScenarioContext ctx)
    {
        ctx.Require(!Round.IsRoundStarted, "the observer begins before native role allocation");
        yield return ctx.WaitUntil(() => NormalRoundWorld.Bots().Length == RoundBots, 15f, "all round bots exist");
        Player[] bots = NormalRoundWorld.Bots();
        ctx.Require(!NativeCommandAdapter.RemoteAdmin("bot_server_status").Success, "optional management is absent");
        yield return ctx.EnsureRoundStarted();
        yield return ctx.WaitUntil(() => bots.All(bot => bot.IsAlive), 10f, "native allocation spawned every bot");
        ctx.Info($"SYMPTOM_NATIVE_ROLES {string.Join(" ", bots.Select(bot => $"{bot.PlayerId}={bot.Role}"))}");

        Player[] scps = bots.Where(bot => bot.Team == Team.SCPs).ToArray();
        ctx.Require(scps.Length >= ScpRoles.Count, $"the queue allocated {ScpRoles.Count} SCP slots: {scps.Length}");
        for (int index = 0; index < ScpRoles.Count; index++)
            Issue2World.Command(ctx, $"forcerole {scps[index].PlayerId} {ScpRoles[index]}");
        yield return ctx.WaitUntil(() => Enumerable.Range(0, ScpRoles.Count).All(index => scps[index].Role == ScpRoles[index]),
            5f, "the SCP slots reached the roles under test");
        ctx.Info($"SYMPTOM_ROLES {string.Join(" ", bots.Select(bot => $"{bot.PlayerId}={bot.Role}@{bot.Room?.Name}"))}");

        LifeBook book = new(ctx, bots);
        double nextState = 0d;
        foreach ((string label, float seconds) in new[] { ("population", PopulationSeconds), ("NTF", WaveSeconds), ("Chaos", WaveSeconds) })
        {
            if (label != "population")
            {
                foreach (Player bot in bots.Where(bot => bot.IsAlive && bot.Team != Team.SCPs))
                    Issue2World.Command(ctx, $"forcerole {bot.PlayerId} Spectator");
                yield return ctx.Wait(1f);
                book.Sample(label);
                ctx.Info($"SYMPTOM_WAVE {label} spectators={bots.Count(bot => bot.Role == RoleTypeId.Spectator)} response={Issue2World.Command(ctx, $"wave spawn {label} true").Response}");
            }

            double end = ctx.ElapsedSeconds + seconds;
            while (ctx.ElapsedSeconds < end)
            {
                book.Sample(label);
                if (ctx.ElapsedSeconds >= nextState)
                {
                    nextState = ctx.ElapsedSeconds + StateLogSeconds;
                    ctx.Info($"SYMPTOM_STATE t={ctx.ElapsedSeconds:F0} {NativeCommandAdapter.RemoteAdmin("bot_debug").Response}");
                }

                yield return ctx.Wait(SampleSeconds);
            }
        }

        book.Close();
        foreach (string verdict in book.Verdicts)
            ctx.Info($"SYMPTOM_VERDICT {verdict}");
        ctx.Require(book.Verdicts.Count == 0, $"no reported symptom appeared: {string.Join("; ", book.Verdicts)}");
    }
}

/// <summary>Controls for the same observer: SCPs the report does not name.</summary>
public sealed class RoundSymptomsControlScenario : RoundSymptomsScenario
{
    protected override IReadOnlyList<RoleTypeId> ScpRoles => new[] { RoleTypeId.Scp096, RoleTypeId.Scp173, RoleTypeId.Scp3114 };

    public override string Name => "scpslbot-round-symptoms-control";
}

internal sealed class LifeBook
{
    private readonly ScenarioContext ctx;
    private readonly Player[] bots;
    private readonly Dictionary<Player, Life> current = new();
    private readonly List<Life> finished = new();

    public LifeBook(ScenarioContext ctx, Player[] bots)
    {
        this.ctx = ctx;
        this.bots = bots;
    }

    public List<string> Verdicts { get; } = new();

    public void Sample(string phase)
    {
        Dictionary<int, string> states = ParseStates(NativeCommandAdapter.RemoteAdmin("bot_debug").Response);
        foreach (Player bot in bots)
        {
            current.TryGetValue(bot, out Life? life);
            if (life != null && (!bot.IsAlive || bot.Role != life.Role))
            {
                Finish(bot, life);
                life = null;
            }

            if (!bot.IsAlive)
                continue;

            if (life == null)
            {
                life = new Life(bot, phase, ctx.ElapsedSeconds);
                current[bot] = life;
                ctx.Info($"SYMPTOM_SPAWN {life.Label} room={bot.Room?.Name} pos={PlacementProbes.Format(bot.Position)}");
            }

            states.TryGetValue(bot.PlayerId, out string? state);
            string? stopped = life.Sample(bot, ctx.ElapsedSeconds, state ?? "unknown");
            if (stopped != null)
                ctx.Info($"SYMPTOM_STOP {life.Label} {stopped}");
        }
    }

    public void Close()
    {
        foreach (KeyValuePair<Player, Life> entry in current.ToArray())
            Finish(entry.Key, entry.Value);
    }

    private void Finish(Player bot, Life life)
    {
        current.Remove(bot);
        finished.Add(life);
        ctx.Info($"SYMPTOM_LIFE {life.Describe(ctx.ElapsedSeconds)}");
        Verdicts.AddRange(life.Verdicts(ctx.ElapsedSeconds));
    }

    private static Dictionary<int, string> ParseStates(string response)
    {
        Dictionary<int, string> states = new();
        foreach (string line in response.Split('\n'))
        {
            Match match = Regex.Match(line, @"^(\d+) .*? behavior=(\S*?)(?::.*?)? combat=(.*?) loco=(\S+) nav=(\S+?)(?:/\d+)?(?: wp=\S+)? stuck=(\S+)");
            if (match.Success)
            {
                string combat = Regex.Replace(match.Groups[3].Value, @"->.*\((los|memory)\)$", "/$1");
                states[int.Parse(match.Groups[1].Value)] = $"behavior={match.Groups[2].Value} combat={combat} loco={match.Groups[4].Value} nav={match.Groups[5].Value}";
            }
        }

        return states;
    }

    private sealed class Life
    {
        // Thresholds are fixed before the run: each states what a watching player would call the symptom.
        private const float StopRadius = 3f;
        private const float ReportedStopSeconds = 20f;
        private const float SurfaceExitSeconds = 120f;
        private const float EscapeMeters = 10f;
        private const float EscapeLoiterSeconds = 10f;
        private const float CellExitSeconds = 60f;
        private const float RoamingLifeSeconds = 240f;

        private readonly Dictionary<FacilityZone, float> zoneSeconds = new();
        private readonly HashSet<string> rooms = new();
        private readonly List<string> route = new();
        private readonly Dictionary<string, int> stopStates = new();
        private readonly string spawnRoom;
        private readonly FacilityZone spawnZone;
        private readonly double spawnedAt;
        private readonly string phase;
        private double lastSampleAt;
        private Vector3 anchor;
        private double anchorAt;
        private double longestStop;
        private string longestStopStates = "none";
        private double longestFocus;
        private double focusSince = -1d;
        private int lastFloor;
        private int rides;
        private double leftRoomAt = -1d;
        private double leftSurfaceAt = -1d;
        private double nearestEscape = double.PositiveInfinity;
        private double nearEscapeSeconds;
        private bool stopReported;

        public Life(Player bot, string phase, double now)
        {
            Role = bot.Role;
            Label = $"{Role}#{bot.PlayerId}/{phase}";
            this.phase = phase;
            spawnedAt = now;
            lastSampleAt = now;
            anchor = bot.Position;
            anchorAt = now;
            lastFloor = Floor(bot.Position.y);
            spawnRoom = RoomKey(bot.Room);
            spawnZone = bot.Room?.Zone ?? FacilityZone.None;
        }

        public RoleTypeId Role { get; }
        public string Label { get; }

        /// <summary>Returns a description the first time a stop passes the reported length.</summary>
        public string? Sample(Player bot, double now, string state)
        {
            float step = (float)(now - lastSampleAt);
            lastSampleAt = now;
            Vector3 position = bot.Position;
            Room? room = bot.Room;
            if (room != null)
            {
                string key = RoomKey(room);
                rooms.Add(key);
                if (route.Count == 0 || route[route.Count - 1] != key)
                    route.Add(key);
                if (leftRoomAt < 0d && key != spawnRoom)
                    leftRoomAt = now;
                zoneSeconds[room.Zone] = zoneSeconds.TryGetValue(room.Zone, out float seconds) ? seconds + step : step;
            }

            int floor = Floor(position.y);
            if (floor != lastFloor)
            {
                rides++;
                lastFloor = floor;
            }

            if (leftSurfaceAt < 0d && floor != SurfaceFloor)
                leftSurfaceAt = now;

            if (floor == SurfaceFloor)
            {
                Bounds zone = Escape.DefaultEscapeZone;
                double distance = new Vector2(Mathf.Max(Mathf.Abs(position.x - zone.center.x) - zone.extents.x, 0f),
                    Mathf.Max(Mathf.Abs(position.z - zone.center.z) - zone.extents.z, 0f)).magnitude;
                nearestEscape = Math.Min(nearestEscape, distance);
                if (distance <= EscapeMeters)
                    nearEscapeSeconds += step;
            }

            bool focused = bot.RoleBase is Scp939Role scp939 && scp939.SubroutineModule.TryGetSubroutine(out Scp939FocusAbility focus) && focus.State > 0f;
            focusSince = !focused ? -1d : focusSince < 0d ? now : focusSince;
            if (focused)
                longestFocus = Math.Max(longestFocus, now - focusSince);

            if (focused || Vector3.Distance(anchor, position) > StopRadius)
            {
                anchor = position;
                anchorAt = now;
                stopStates.Clear();
                stopReported = false;
                return null;
            }

            stopStates[state] = stopStates.TryGetValue(state, out int count) ? count + 1 : 1;
            double stopped = now - anchorAt;
            if (stopped > longestStop)
            {
                longestStop = stopped;
                longestStopStates = string.Join(" | ", stopStates.OrderByDescending(entry => entry.Value).Take(3).Select(entry => $"{entry.Value}x {entry.Key}"));
            }

            if (stopReported || stopped < ReportedStopSeconds)
                return null;

            stopReported = true;
            return $"room={room?.Name} pos={PlacementProbes.Format(position)} since={anchorAt - spawnedAt:F0}s state={state}";
        }

        public string Describe(double now) =>
            $"{Label} lived={now - spawnedAt:F0}s spawnRoom={spawnRoom} leftRoom={Since(leftRoomAt)} rooms={rooms.Count} "
            + $"zones={string.Join(",", zoneSeconds.OrderByDescending(entry => entry.Value).Select(entry => $"{entry.Key}:{entry.Value:F0}"))} "
            + $"rides={rides} leftSurface={Since(leftSurfaceAt)} nearestEscape={(double.IsInfinity(nearestEscape) ? "n/a" : nearestEscape.ToString("F0"))} "
            + $"nearEscape={nearEscapeSeconds:F0}s longestStop={longestStop:F0}s focus={longestFocus:F0}s stopStates=[{longestStopStates}] "
            + $"route={string.Join(">", route.Take(18))}";

        public IEnumerable<string> Verdicts(double now)
        {
            double lived = now - spawnedAt;
            bool scp = Role is RoleTypeId.Scp106 or RoleTypeId.Scp939 or RoleTypeId.Scp049 or RoleTypeId.Scp096 or RoleTypeId.Scp173 or RoleTypeId.Scp3114 or RoleTypeId.Scp0492;
            bool wave = phase != "population";
            if (wave && spawnZone == FacilityZone.Surface)
            {
                if (lived >= SurfaceExitSeconds && (leftSurfaceAt < 0d || leftSurfaceAt - spawnedAt > SurfaceExitSeconds))
                    yield return $"S1 {Label} left Surface after {Since(leftSurfaceAt)}";
                if (nearEscapeSeconds >= EscapeLoiterSeconds)
                    yield return $"S1 {Label} spent {nearEscapeSeconds:F0}s within {EscapeMeters:F0}m of the escape";
            }

            if (scp && longestStop >= ReportedStopSeconds)
                yield return $"S3 {Label} stopped {longestStop:F0}s [{longestStopStates}]";

            if (Role == RoleTypeId.FacilityGuard && lived >= RoamingLifeSeconds && zoneSeconds.Keys.All(zone => zone == spawnZone))
                yield return $"S4 {Label} stayed in {spawnZone} for {lived:F0}s";

            if (Role is RoleTypeId.ClassD or RoleTypeId.Scientist && lived >= RoamingLifeSeconds && zoneSeconds.Keys.All(zone => zone == spawnZone))
                yield return $"S5 {Label} stayed in {spawnZone} for {lived:F0}s";

            if (scp && lived >= CellExitSeconds && (leftRoomAt < 0d || leftRoomAt - spawnedAt > CellExitSeconds))
                yield return $"S6 {Label} left {spawnRoom} after {Since(leftRoomAt)}";
        }

        private string Since(double at) => at < 0d ? "never" : $"{at - spawnedAt:F0}s";

        private const int SurfaceFloor = 3;

        // Elevators are the only way between these heights, so a change of band is a ride.
        private static int Floor(float y) => y < -50f ? 0 : y < 50f ? 1 : y < 200f ? 2 : SurfaceFloor;

        private static string RoomKey(Room? room) => room == null ? "none" : $"{room.Name}@{room.Position.x:0}/{room.Position.z:0}";
    }
}
