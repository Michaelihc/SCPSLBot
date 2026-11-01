using LabApi.Features.Wrappers;
using MapGeneration;
using PlayerRoles;
using PlayerRoles.PlayableScps.Scp939;
using PlaytestHarness.Core;
using PlaytestHarness.Probes;
using SCPSLBot.PlaytestScenarios.Harness;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace SCPSLBot.PlaytestScenarios.Scenarios;

/// <summary>
/// One uncontested bot per role explores from its native spawn. Each role's world trace has to show
/// real travel: several rooms, no long stop and no back-and-forth between two rooms, which is how a
/// bot turning around at a door presents. Roles that spawn on Surface also have to enter the facility.
/// SCP-939 waits at doors on purpose, so seconds spent holding native focus are not a stop; that hold
/// has its own limit, so a bot cannot pass by stalling in focus.
/// </summary>
public class AutonomousRoamScenario : Scenario
{
    private const float SoakSeconds = 150f;
    private const int MinimumRooms = 3;
    private const float MaximumStationarySeconds = 45f;
    private const float MaximumFocusHoldSeconds = 46f;
    private const float StationaryRadius = 3f;
    private const int MaximumAlternations = 6;

    private static readonly RoleTypeId[] AllRoles =
    {
        RoleTypeId.Scp096,
        RoleTypeId.Scp049,
        RoleTypeId.Scp939,
        RoleTypeId.FacilityGuard,
        RoleTypeId.NtfPrivate,
        RoleTypeId.ChaosRifleman,
    };

    protected virtual IReadOnlyList<RoleTypeId> Roles => AllRoles;

    public override string Name => "scpslbot-autonomous-roam";
    public override string Description => "Each role roams alone from its native spawn without stopping, door-looping or staying on Surface.";
    public override FidelityRange Supported => FidelityRange.Only(Fidelity.Standard);
    public override bool IncludeInRunAll => false;
    public override float TimeoutSeconds => 1020f;

    public override IEnumerator<float> Run(ScenarioContext ctx)
    {
        Issue2World.RequireReady(ctx);
        yield return ctx.EnsureRoundStarted();

        List<string> failures = new();
        foreach (RoleTypeId role in Roles)
        {
            int botId = Issue2World.AddBot(ctx);
            yield return ctx.Wait(1f); // A keycard loadout needs the dummy's identity, set after its first frame.
            Issue2World.Command(ctx, $"forcerole {botId} {role}");
            yield return ctx.WaitUntil(() => WarmupBotWorld.FindById(botId)?.Role == role, 5f, $"bot reached {role}");
            yield return ctx.Wait(1f);

            Player bot = WarmupBotWorld.FindById(botId)
                ?? throw new RequireException($"{role} bot vanished after spawning");
            ctx.Arrange("keep the observed bot alive for the whole soak", () => bot.IsGodModeEnabled = true);
            RoamTrace trace = new(bot.Position, bot.Room?.Zone == FacilityZone.Surface);
            float end = Time.realtimeSinceStartup + SoakSeconds;
            float nextDebug = 0f;
            while (Time.realtimeSinceStartup < end && bot.IsAlive)
            {
                trace.Sample(bot.Position, bot.Room, Time.realtimeSinceStartup, IsHoldingFocus(bot));
                if (Time.realtimeSinceStartup >= nextDebug)
                {
                    nextDebug = Time.realtimeSinceStartup + 30f;
                    ctx.Info($"ROAM_DEBUG role={role} pos={PlacementProbes.Format(bot.Position)} {NativeCommandAdapter.RemoteAdmin("bot_debug").Response}");
                }

                yield return ctx.Wait(1f);
            }

            string? verdict = trace.Failure(bot.IsAlive);
            ctx.Info($"ROAM role={role} spawn={PlacementProbes.Format(trace.Spawn)} end={PlacementProbes.Format(bot.Position)} {trace.Describe()} verdict={verdict ?? "PASS"}");
            if (verdict != null)
            {
                failures.Add($"{role}: {verdict} ({trace.Describe()})");
            }

            NativeCommandAdapter.RemoteAdmin($"dummies destroy {botId}");
            yield return ctx.Wait(1f);
        }

        ctx.Require(failures.Count == 0, $"every role roamed: {string.Join("; ", failures)}");
    }

    private static bool IsHoldingFocus(Player bot) => bot.RoleBase is Scp939Role role
        && role.SubroutineModule.TryGetSubroutine(out Scp939FocusAbility focus)
        && focus.State > 0f;

    private sealed class RoamTrace
    {
        private readonly HashSet<string> rooms = new();
        private readonly List<string> visits = new();
        private readonly bool spawnedOnSurface;
        private Vector3 anchor;
        private float anchorTime = -1f;
        private float longestStop;
        private float focusSince = -1f;
        private float longestFocusHold;
        private bool enteredFacility;

        public RoamTrace(Vector3 spawn, bool onSurface)
        {
            Spawn = spawn;
            anchor = spawn;
            spawnedOnSurface = onSurface;
        }

        public Vector3 Spawn { get; }

        public void Sample(Vector3 position, Room? room, float now, bool holdingFocus)
        {
            if (anchorTime < 0f)
            {
                anchorTime = now;
            }

            focusSince = !holdingFocus ? -1f : focusSince < 0f ? now : focusSince;
            if (holdingFocus)
            {
                longestFocusHold = Mathf.Max(longestFocusHold, now - focusSince);
            }

            if (holdingFocus || Vector3.Distance(anchor, position) > StationaryRadius)
            {
                anchor = position;
                anchorTime = now;
            }

            longestStop = Mathf.Max(longestStop, now - anchorTime);
            if (room == null)
            {
                return;
            }

            enteredFacility |= room.Zone != FacilityZone.Surface;
            string name = $"{room.Name}@{room.Position.x:0}/{room.Position.z:0}";
            rooms.Add(name);
            if (visits.Count == 0 || visits[visits.Count - 1] != name)
            {
                visits.Add(name);
            }
        }

        public string? Failure(bool alive)
        {
            if (!alive)
            {
                return "died during the soak";
            }

            if (longestStop > MaximumStationarySeconds)
            {
                return $"stopped for {longestStop:0}s";
            }

            if (longestFocusHold > MaximumFocusHoldSeconds)
            {
                return $"held focus for {longestFocusHold:0}s";
            }

            if (LongestAlternation() > MaximumAlternations)
            {
                return $"alternated between two rooms {LongestAlternation()} times";
            }

            if (spawnedOnSurface && !enteredFacility)
            {
                return "never left Surface";
            }

            return !spawnedOnSurface && rooms.Count < MinimumRooms ? $"visited only {rooms.Count} rooms" : null;
        }

        public string Describe() => $"rooms={rooms.Count} visits={visits.Count} longestStop={longestStop:0}s focusHold={longestFocusHold:0}s alternation={LongestAlternation()} route={string.Join(">", visits.Take(14))}";

        private int LongestAlternation()
        {
            int longest = 0;
            int current = 0;
            for (int index = 2; index < visits.Count; index++)
            {
                current = visits[index] == visits[index - 2] ? current + 1 : 0;
                longest = Mathf.Max(longest, current);
            }

            return longest;
        }
    }
}

/// <summary>The same soak repeated for SCP-939 alone, so its containment-room exit can be sampled faster.</summary>
public sealed class Scp939AutonomousRoamScenario : AutonomousRoamScenario
{
    private static readonly RoleTypeId[] Repeats = { RoleTypeId.Scp939, RoleTypeId.Scp939, RoleTypeId.Scp939, RoleTypeId.Scp939 };

    protected override IReadOnlyList<RoleTypeId> Roles => Repeats;

    public override string Name => "scpslbot-autonomous-roam-939";
    public override string Description => "SCP-939 roams from its native spawn four times without stopping or staying in its containment room.";
    public override float TimeoutSeconds => 680f;
}
