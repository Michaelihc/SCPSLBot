using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.Handlers;
using LabApi.Features.Wrappers;
using MapGeneration;
using PlayerRoles;
using PlaytestHarness.Core;
using SCPSLBot.PlaytestScenarios.Harness;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace SCPSLBot.PlaytestScenarios.Scenarios;

public sealed class NormalRoundPopulationScenario : Scenario
{
    public override string Name => "scpslbot-normal-round";
    public override string Description => "Independent spectators receive native round roles and retain admin changes without population repair.";
    public override FidelityRange Supported => FidelityRange.Only(Fidelity.Standard);
    public override bool IncludeInRunAll => false;

    public override IEnumerator<float> Run(ScenarioContext ctx)
    {
        ctx.Require(!Round.IsRoundStarted, "test begins before native role allocation");
        yield return ctx.WaitUntil(() => NormalRoundWorld.Bots().Length == 3, 10f, "three configured round bots exist");
        Player[] bots = NormalRoundWorld.Bots();
        ctx.Require(bots.All(bot => bot.Role == RoleTypeId.Spectator), "all configured bots start as spectators");
        BotStatusSnapshot before = BotStatusSnapshot.Read();
        ctx.Require(before.Owned == 0 && before.Independent == 3 && before.Mode == "None", "spectators are independent with warmup off");
        yield return ctx.EnsureRoundStarted();
        yield return ctx.WaitUntil(() => bots.All(bot => bot.IsAlive), 5f, "native round allocation spawned every bot");
        ctx.Require(bots.Select(bot => bot.Role).Distinct().Count() == 3,
            "native 1/3/4 team queue assigned Scientist, ClassD and FacilityGuard");
        foreach (Player bot in bots)
        {
            ctx.Info($"native role bot={bot.PlayerId} role={bot.Role} position={bot.Position}");
            WarmupBotWorld.ProbeGroundAndSurroundings(ctx, bot);
        }
        Player selected = bots[0];
        WarmupPopulationRecoveryScenario.RequireCommand(ctx,
            NativeCommandAdapter.RemoteAdmin($"forcerole {selected.PlayerId} Spectator"), "admin releases one bot to spectator");
        yield return ctx.Wait(4f);
        ctx.Require(selected.Role == RoleTypeId.Spectator, "bot stayed spectator past delayed role callbacks and warmup respawn timing");
        ctx.Require(NormalRoundWorld.Bots().Length == 3 && BotStatusSnapshot.Read().Owned == 0,
            "no maintained replacement or role repair appeared");
    }
}

public sealed class AutonomousEscapeScenario : Scenario
{
    public override string Name => "scpslbot-autonomous-escape";
    public override string Description => "Native round spawn, autonomous gear acquisition and walked facility escape, observed through native events.";
    public override FidelityRange Supported => FidelityRange.Only(Fidelity.EndToEnd);
    public override bool IncludeInRunAll => false;
    public override float TimeoutSeconds => 660f;

    public override IEnumerator<float> Run(ScenarioContext ctx)
    {
        ctx.Require(!Round.IsRoundStarted, "escape begins before native round spawn");
        yield return ctx.WaitUntil(() => NormalRoundWorld.Bots().Length == 1, 10f, "one configured spectator exists");
        Player bot = NormalRoundWorld.Bots().Single();
        ctx.Require(bot.Role == RoleTypeId.Spectator && BotStatusSnapshot.Read().Owned == 0,
            "candidate is an unmanaged spectator");
        bool escaped = false;
        RoleTypeId escapedFrom = RoleTypeId.None;
        int pickups = 0;
        HashSet<FacilityZone> zones = new();
        void OnEscaped(PlayerEscapedEventArgs ev)
        {
            if (!ReferenceEquals(ev.Player.ReferenceHub, bot.ReferenceHub)) return;
            escapedFrom = ev.OldRole;
            escaped = true;
            ctx.Info($"native escape bot={bot.PlayerId} oldRole={ev.OldRole} newRole={ev.NewRole} scenario={ev.EscapeScenarioType}");
        }
        void OnPickup(PlayerPickedUpItemEventArgs ev)
        {
            if (!ReferenceEquals(ev.Player.ReferenceHub, bot.ReferenceHub)) return;
            pickups++;
            ctx.Info($"native pickup bot={bot.PlayerId} item={ev.Item.Type}");
        }
        PlayerEvents.Escaped += OnEscaped;
        PlayerEvents.PickedUpItem += OnPickup;
        try
        {
            yield return ctx.EnsureRoundStarted();
            yield return ctx.WaitUntil(() => bot.Role == RoleTypeId.Scientist, 5f, "native test queue spawned a Scientist");
            WarmupBotWorld.ProbeGroundAndSurroundings(ctx, bot);
            Vector3 start = bot.Position;
            double deadline = ctx.ElapsedSeconds + 600f;
            float walked = 0f;
            Vector3 previous = start;
            int sample = 0;
            while (!escaped && ctx.ElapsedSeconds < deadline)
            {
                ctx.Require(bot.IsAlive, "escaping bot remains alive");
                zones.Add(bot.Zone);
                float displacement = Vector3.Distance(previous, bot.Position);
                // Elevator travel changes floors natively; count only local walking here.
                if (displacement < 10f) walked += displacement;
                previous = bot.Position;
                if (sample++ % 10 == 0)
                    ctx.Info($"escape progress t={ctx.ElapsedSeconds:F1} bot={bot.PlayerId} role={bot.Role} zone={bot.Zone} "
                        + $"room={bot.Room?.Name} pos={bot.Position} walked={walked:F1} pickups={pickups} items={string.Join(",", bot.Items.Select(item => item.Type))}");
                yield return ctx.Wait(0.5f);
            }
            ctx.Require(escaped, $"native escape completed within 600s; zones={string.Join(",", zones)} pickups={pickups} walked={walked:F1} final={bot.Position}");
            ctx.Require(escapedFrom == RoleTypeId.Scientist, "native escape originated from the Scientist role");
            ctx.Require(zones.Contains(FacilityZone.LightContainment) && zones.Contains(FacilityZone.HeavyContainment)
                && zones.Contains(FacilityZone.Entrance) && zones.Contains(FacilityZone.Surface), "bot traversed every facility zone");
            ctx.Require(pickups > 0 && walked > 30f, "bot acquired gear natively and walked through the facility");
            ctx.Require(bot.Role == RoleTypeId.NtfSpecialist, "native Scientist escape changed the role to NtfSpecialist");
        }
        finally
        {
            PlayerEvents.Escaped -= OnEscaped;
            PlayerEvents.PickedUpItem -= OnPickup;
        }
    }
}

internal static class NormalRoundWorld
{
    public static Player[] Bots() => Player.ReadyList.Where(player => player.IsDummy && !player.IsDestroyed
        && player.Nickname.StartsWith("SCPSL Round Bot ", StringComparison.Ordinal)).ToArray();
}
