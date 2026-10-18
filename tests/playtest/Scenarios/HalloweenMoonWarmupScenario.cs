using System.Collections.Generic;
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.Arguments.WarheadEvents;
using LabApi.Events.Handlers;
using MapGeneration.Holidays;
using Mirror;
using PlayerRoles;
using PlayerStatsSystem;
using PlaytestHarness.Actors;
using PlaytestHarness.Core;
using SCPSLBot.PlaytestScenarios.Harness;
using UnityEngine;

namespace SCPSLBot.PlaytestScenarios.Scenarios;

public sealed class HalloweenMoonWarmupScenario : Scenario
{
    public override string Name => "scpslbot-halloween-moon";
    public override string Description => "Pauses a native end-stage Halloween moon in warmup, then restores its real explosion path.";
    public override FidelityRange Supported => FidelityRange.Only(Fidelity.Quick);
    public override float TimeoutSeconds => 320f;

    public override IEnumerator<float> Run(ScenarioContext ctx)
    {
        string originalMode = BotStatusSnapshot.Read().Mode;
        GameObject moonObject = new("Warmup moon acceptance");
        moonObject.AddComponent<NetworkIdentity>();
        HubertMoon moon = moonObject.AddComponent<HubertMoon>();
        GameObject disabledObject = new("Already-disabled moon acceptance");
        disabledObject.AddComponent<NetworkIdentity>();
        HubertMoon disabledMoon = disabledObject.AddComponent<HubertMoon>();
        int explosionHits = 0;
        bool nativeDeath = false;
        int actorId = -1;
        bool expectMoonDeath = false;
        Actor? victim = null;

        void ObserveDamage(PlayerHurtingEventArgs ev)
        {
            if (ev.Player.PlayerId == actorId && ev.DamageHandler is ExplosionDamageHandler)
            {
                explosionHits++;
                if (expectMoonDeath)
                    victim!.ExpectNativeDeath();
            }
        }
        void ObserveDeath(PlayerDeathEventArgs ev)
        {
            if (ev.Player.PlayerId == actorId && ev.DamageHandler is ExplosionDamageHandler)
                nativeDeath = true;
        }
        void CancelWarhead(WarheadStartingEventArgs ev) => ev.IsAllowed = false;

        PlayerEvents.Hurting += ObserveDamage;
        PlayerEvents.Death += ObserveDeath;
        WarheadEvents.Starting += CancelWarhead;
        try
        {
            SetMode(ctx, "none");
            yield return ctx.EnsureRoundStarted();
            yield return ctx.WaitOneFrame();
            // Quick fixture: begin at the end of the native moon's descent. Its ending timers,
            // explosion cadence and damage run unchanged through Unity's actual Update loop.
            moon.Network_elapsedMovementTime = 1800f;
            disabledMoon.enabled = false;
            var actor = ctx.SpawnActor("moon-victim", RoleTypeId.Scp049, SpawnSpec.Native(), useMovementProvider: false);
            victim = actor;
            yield return actor.WaitReady();
            actorId = actor.PlayerId;
            SetMode(ctx, "standard");
            ctx.Require(!moon.enabled, "Standard warmup paused the active native moon");
            ctx.Require(!disabledMoon.enabled, "an already-disabled moon remains disabled");
            yield return ctx.Wait(142f);
            ctx.Require(explosionHits == 0 && !nativeDeath && actor.Health > 0f,
                "the native ending window passed without explosions or victim death in warmup");

            SetMode(ctx, "none");
            ctx.Require(moon.enabled, "leaving warmup restored the owned native controller");
            ctx.Require(!disabledMoon.enabled, "leaving warmup did not enable an unowned controller");
            expectMoonDeath = true;
            yield return ctx.WaitUntil(() => nativeDeath, 150f,
                "the restored native moon actually exploded and killed its SCP victim");
            ctx.Require(explosionHits > 0, "real native explosion damage was observed after restoration");
            expectMoonDeath = false;
            SetMode(ctx, "standard");
            ctx.Require(!moon.enabled, "re-entering warmup paused an active ending sequence");
            var survivor = ctx.SpawnActor("moon-survivor", RoleTypeId.Scp049, SpawnSpec.Native(), useMovementProvider: false);
            yield return survivor.WaitReady();
            actorId = survivor.PlayerId;
            int hitsBefore = explosionHits;
            yield return ctx.Wait(3f);
            ctx.Require(explosionHits == hitsBefore && survivor.Health > 0f,
                "the paused ending did not damage a fresh living victim");
            ctx.Info($"native moon walkthrough complete: restored explosion hits={explosionHits}, native death={nativeDeath}");
        }
        finally
        {
            PlayerEvents.Hurting -= ObserveDamage;
            PlayerEvents.Death -= ObserveDeath;
            WarheadEvents.Starting -= CancelWarhead;
            Object.Destroy(moonObject);
            Object.Destroy(disabledObject);
            NativeCommandAdapter.RemoteAdmin($"bot_warmup {originalMode.ToLowerInvariant()}");
        }
    }

    private static void SetMode(ScenarioContext ctx, string mode) =>
        WarmupPopulationRecoveryScenario.RequireCommand(ctx,
            NativeCommandAdapter.RemoteAdmin($"bot_warmup {mode}"), $"set warmup {mode}");
}
