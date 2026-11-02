using System.Collections.Generic;
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.Handlers;
using LabApi.Features.Wrappers;
using PlayerRoles;
using PlayerStatsSystem;
using PlaytestHarness.Actors;
using PlaytestHarness.Core;
using SCPSLBot.PlaytestScenarios.Harness;

namespace SCPSLBot.PlaytestScenarios.Scenarios;

public sealed class WarmupWarheadSuppressionScenario : Scenario
{
    public override string Name => "scpslbot-warmup-warhead";
    public override string Description => "Rejects admin, automatic and DMS nukes in warmup, including existing final-seconds countdowns.";
    public override FidelityRange Supported => FidelityRange.Only(Fidelity.Quick);
    public override float TimeoutSeconds => 65f;

    public override IEnumerator<float> Run(ScenarioContext ctx)
    {
        string originalMode = BotStatusSnapshot.Read().Mode;
        int starts = 0, detonations = 0, dmsActivations = 0;
        int victimId = -1;
        bool nativeDeath = false;
        void Started(LabApi.Events.Arguments.WarheadEvents.WarheadStartedEventArgs _) => starts++;
        void Detonated(LabApi.Events.Arguments.WarheadEvents.WarheadDetonatedEventArgs _) => detonations++;
        void DmsActivated() => dmsActivations++;
        void Died(PlayerDeathEventArgs ev)
        {
            if (ev.Player.PlayerId == victimId && ev.DamageHandler is WarheadDamageHandler)
                nativeDeath = true;
        }

        WarheadEvents.Started += Started;
        WarheadEvents.Detonated += Detonated;
        ServerEvents.DeadmanSequenceActivated += DmsActivated;
        PlayerEvents.Death += Died;
        try
        {
            SetMode(ctx, "standard");
            yield return ctx.EnsureRoundStarted();
            var victim = ctx.SpawnActor("nuke-victim", RoleTypeId.Scientist, SpawnSpec.Native(), useMovementProvider: false);
            yield return victim.WaitReady();
            victimId = victim.PlayerId;
            foreach (string command in new[] { "warhead start", "SERVER_EVENT DETONATION_START",
                         "warhead instant", "SERVER_EVENT DETONATION_INSTANT", "dms force" })
            {
                Command(ctx, command);
                yield return ctx.Wait(0.5f);
                AssertDisabled(ctx, command);
            }

            Warhead.Start(isAutomatic: true);
            yield return ctx.WaitOneFrame();
            AssertDisabled(ctx, "automatic native start");
            Command(ctx, "dms toggle");
            Command(ctx, "dms set 0");
            yield return ctx.Wait(1f);
            AssertDisabled(ctx, "expired forced DMS countdown");
            ctx.Require(starts == 0 && detonations == 0 && dmsActivations == 0 && victim.Health > 0f,
                "no native Started, Detonated or DMS Activated event escaped warmup suppression");

            SetMode(ctx, "none");
            Command(ctx, "warhead start");
            ctx.Require(Warhead.IsDetonationInProgress && starts > 0, "native starts work outside warmup");
            SetMode(ctx, "standard");
            AssertDisabled(ctx, "enter warmup during a running countdown");

            SetMode(ctx, "none");
            Command(ctx, "warhead settime 5");
            ctx.Require(Warhead.IsDetonationInProgress && Warhead.DetonationTime <= 5.5f,
                "native countdown is inside its uncancellable final ten seconds");
            SetMode(ctx, "standard");
            AssertDisabled(ctx, "enter warmup in the final ten seconds");

            SetMode(ctx, "none");
            Command(ctx, "dms force");
            ctx.Require(DeadmanSwitch.IsSequenceActive, "native DMS activates outside warmup");
            SetMode(ctx, "standard");
            AssertDisabled(ctx, "enter warmup during active DMS");

            SetMode(ctx, "none");
            victim.ExpectNativeDeath();
            Command(ctx, "warhead instant");
            yield return ctx.WaitUntil(() => nativeDeath && detonations > 0, 5f,
                "normal-mode native instant detonation really killed the underground victim");
            ctx.Info($"warhead walkthrough complete: native starts={starts}, detonations={detonations}, DMS activations={dmsActivations}");
        }
        finally
        {
            WarheadEvents.Started -= Started;
            WarheadEvents.Detonated -= Detonated;
            ServerEvents.DeadmanSequenceActivated -= DmsActivated;
            PlayerEvents.Death -= Died;
            NativeCommandAdapter.RemoteAdmin($"bot_warmup {originalMode.ToLowerInvariant()}");
        }
    }

    private static void AssertDisabled(ScenarioContext ctx, string trigger) =>
        ctx.Require(!Warhead.IsDetonationInProgress && !Warhead.IsDetonated && !DeadmanSwitch.IsSequenceActive,
            $"nuke and DMS inactive after {trigger}");

    private static void Command(ScenarioContext ctx, string command) =>
        WarmupPopulationRecoveryScenario.RequireCommand(ctx, NativeCommandAdapter.RemoteAdmin(command), command);

    private static void SetMode(ScenarioContext ctx, string mode) => Command(ctx, $"bot_warmup {mode}");
}
