using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.Handlers;
using LabApi.Features.Wrappers;
using PlayerRoles;
using PlaytestHarness.Core;
using SCPSLBot.PlaytestScenarios.Harness;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SCPSLBot.PlaytestScenarios.Scenarios;

public sealed class WarmupRoleDeathRecoveryScenario : Scenario
{
    public override string Name => "scpslbot-role-death-recovery";
    public override string[] Aliases => ["bot-role-death"];
    public override string[] Suites => ["scpslbot-lifecycle"];
    public override string Description => "Cancels one real bot repair role event, proves retry healing, then kills and observes maintained respawn.";
    public override FidelityRange Supported => FidelityRange.Only(Fidelity.Standard);
    public override float TimeoutSeconds => 80f;

    public override IEnumerator<float> Run(ScenarioContext ctx)
    {
        BotStatusSnapshot initial = BotStatusSnapshot.Read();
        string originalMode = initial.Mode;
        int selectedPlayerId = -1;
        int canceledRepairs = 0;
        bool deathObserved = false;
        RoleTypeId desiredRole = initial.DesiredRole;

        void CancelOneRepair(PlayerChangingRoleEventArgs ev)
        {
            if (canceledRepairs == 0
                && ev.Player != null
                && ev.Player.PlayerId == selectedPlayerId
                && ev.NewRole == desiredRole)
            {
                ev.IsAllowed = false;
                canceledRepairs++;
            }
        }

        void ObserveDeath(PlayerDeathEventArgs ev)
        {
            if (ev.Player != null && ev.Player.PlayerId == selectedPlayerId)
            {
                deathObserved = true;
            }
        }

        try
        {
            WarmupPopulationRecoveryScenario.RequireCommand(ctx,
                NativeCommandAdapter.RemoteAdmin("bot_warmup standard"), "enable Standard warmup");
            ThrottledCondition readyPopulation = new(IsReadyPopulation);
            yield return ctx.WaitUntil(readyPopulation.Check, 12f, "population ready before recovery probes");

            BotStatusSnapshot ready = BotStatusSnapshot.Read();
            desiredRole = ready.DesiredRole;
            Player selected = WarmupBotWorld.Snapshot().First();
            selectedPlayerId = selected.PlayerId;
            ctx.Info($"selected managed world dummy id={selectedPlayerId} nickname='{selected.Nickname}' role={selected.Role}");

            PlayerEvents.ChangingRole += CancelOneRepair;
            WarmupPopulationRecoveryScenario.RequireCommand(ctx,
                NativeCommandAdapter.RemoteAdmin($"forcerole {selectedPlayerId} Spectator"),
                "force selected bot to Spectator through native RA");

            yield return ctx.WaitUntil(() => canceledRepairs == 1, 5f,
                "temporary LabAPI handler canceled exactly one desired-role repair");
            ThrottledCondition roleHealed = new(() => SelectedIsAliveDesired(selectedPlayerId, desiredRole));
            yield return ctx.WaitUntil(roleHealed.Check, 12f,
                "reconciler retried canceled bot role repair until healthy");
            PlayerEvents.ChangingRole -= CancelOneRepair;

            ctx.Require(canceledRepairs == 1,
                $"exactly one desired-role transition was canceled (actual {canceledRepairs})");
            BotStatusSnapshot healed = BotStatusSnapshot.Read();
            WarmupBotWorld.AssertExactInitializedPopulation(ctx, healed);

            PlayerEvents.Death += ObserveDeath;
            WarmupPopulationRecoveryScenario.RequireCommand(ctx,
                NativeCommandAdapter.RemoteAdmin($"kill {selectedPlayerId}"),
                "kill selected managed bot through native RA damage");
            yield return ctx.WaitUntil(() => deathObserved, 3f,
                "LabAPI Death observed the native RA kill");
            ThrottledCondition deathHealed = new(() => SelectedIsAliveDesired(selectedPlayerId, desiredRole));
            yield return ctx.WaitUntil(deathHealed.Check, 12f,
                "dead maintained bot respawned alive with desired role");
            PlayerEvents.Death -= ObserveDeath;

            ctx.Require(deathObserved, "death was observed on the selected maintained bot identity");
            BotStatusSnapshot respawned = BotStatusSnapshot.Read();
            WarmupBotWorld.AssertExactInitializedPopulation(ctx, respawned);
            Dictionary<int, UnityEngine.Vector3> before = WarmupBotWorld.CapturePositions();
            yield return ctx.Wait(1.25f);
            WarmupBotWorld.AssertNoFallsAfterSettling(ctx, before);
            ctx.Info($"role/death recovery final status: {respawned.Raw}");
        }
        finally
        {
            PlayerEvents.ChangingRole -= CancelOneRepair;
            PlayerEvents.Death -= ObserveDeath;
            NativeCommandAdapter.RemoteAdmin($"bot_warmup {originalMode.ToLowerInvariant()}");
        }
    }

    private static bool IsReadyPopulation()
    {
        try
        {
            BotStatusSnapshot status = BotStatusSnapshot.Read();
            return status.Desired > 0
                   && status.Owned == status.Desired
                   && status.Live == status.Desired
                   && status.NetworkReady
                   && status.NavReady
                   && WarmupBotWorld.Snapshot().Length == status.Desired;
        }
        catch
        {
            return false;
        }
    }

    private static bool SelectedIsAliveDesired(int playerId, RoleTypeId desiredRole)
    {
        Player? player = WarmupBotWorld.FindById(playerId);
        if (player == null || player.IsDestroyed || !player.IsAlive || player.Role != desiredRole)
        {
            return false;
        }

        try
        {
            BotStatusSnapshot status = BotStatusSnapshot.Read();
            return status.Live == status.Desired && status.Owned == status.Desired;
        }
        catch
        {
            return false;
        }
    }
}
