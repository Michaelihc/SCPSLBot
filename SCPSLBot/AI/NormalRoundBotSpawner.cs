using LabApi.Events.Handlers;
using LabApi.Features.Console;
using MEC;
using PlayerRoles;
using PlayerRoles.RoleAssign;
using SCPSLBot.Api;
using System.Collections.Generic;
using LabRound = LabApi.Features.Wrappers.Round;

namespace SCPSLBot.AI
{
    /// <summary>Creates independent spectators once per round; native systems own their roles and lives.</summary>
    internal sealed class NormalRoundBotSpawner
    {
        private BotRuntimeConfig config;
        private CoroutineHandle handle;
        private bool initialized;
        private int generation;
        private int created;

        public void Init(BotRuntimeConfig pluginConfig)
        {
            if (initialized)
                return;

            config = pluginConfig;
            created = 0;
            initialized = true;
            ServerEvents.WaitingForPlayers += BeginWaiting;
            ServerEvents.RoundRestarted += OnRoundRestarted;
            RoleAssigner.OnBeforePlayersSpawned += BeforeRoleAssignment;
            BotServerPolicy.ModeChanged += BeginWaiting;
            BeginWaiting();
        }

        public void Terminate()
        {
            if (!initialized)
                return;

            initialized = false;
            generation++;
            Timing.KillCoroutines(handle);
            ServerEvents.WaitingForPlayers -= BeginWaiting;
            ServerEvents.RoundRestarted -= OnRoundRestarted;
            RoleAssigner.OnBeforePlayersSpawned -= BeforeRoleAssignment;
            BotServerPolicy.ModeChanged -= BeginWaiting;
            config = null;
            created = 0;
        }

        private bool IsNormalRound => initialized && !BotServerPolicy.IsStandardWarmup;

        private void BeginWaiting()
        {
            Timing.KillCoroutines(handle);
            int waitingGeneration = ++generation;
            if (IsNormalRound && !LabRound.IsRoundStarted && created < config.NormalRoundBotCount)
                handle = Timing.RunCoroutine(SpawnWhileWaiting(waitingGeneration));
        }

        private IEnumerator<float> SpawnWhileWaiting(int waitingGeneration)
        {
            // Let optional management finish enabling before creating normal-round bots.
            yield return Timing.WaitForSeconds(0.5f);
            while (IsNormalRound && generation == waitingGeneration && !LabRound.IsRoundStarted
                && created < config.NormalRoundBotCount)
            {
                SpawnMissingSpectators();
                yield return Timing.WaitForSeconds(0.5f);
            }
        }

        private void BeforeRoleAssignment()
        {
            // Last chance for a short countdown; RoundStarted is too late for native allocation.
            if (IsNormalRound)
                SpawnMissingSpectators();
        }

        private void SpawnMissingSpectators()
        {
            if (!BotManager.Instance.CanSpawnBot())
                return;

            while (created < config.NormalRoundBotCount)
            {
                // No requested-role callback: it would overwrite the game's role assignment later.
                ReferenceHub hub = BotManager.Instance.AddUnassignedBotPlayer($"SCPSL Round Bot {created + 1}");
                if (hub == null)
                    return;

                try
                {
                    hub.roleManager.ServerSetRole(RoleTypeId.Spectator, RoleChangeReason.RemoteAdmin);
                    created++;
                    Logger.Info($"[SCPSLBot] NORMAL_ROUND_BOT_CREATED player_id={hub.PlayerId} slot={created} role=Spectator");
                }
                catch (System.Exception exception)
                {
                    BotManager.Instance.DespawnBot(hub);
                    Logger.Error($"[SCPSLBot] Failed to create a normal-round spectator: {exception}");
                    return;
                }
            }
        }

        private void OnRoundRestarted()
        {
            generation++;
            Timing.KillCoroutines(handle);
            created = 0;
        }

    }
}
