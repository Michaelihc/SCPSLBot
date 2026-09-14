using LabApi.Features.Wrappers;
using MEC;
using PlayerRoles;
using SCPSLBot.Warmup.Policy;
using System;
using System.Collections.Generic;
using UnityEngine;
using Logger = LabApi.Features.Console.Logger;

namespace SCPSLBot.Warmup
{
    /// <summary>
    /// One round-owned real-player respawn scanner. It deliberately does not depend on join, death,
    /// spawn, or role-change callbacks, so an eligible Spectator is recovered even when an event is missed.
    /// </summary>
    internal sealed class WarmupRoundRespawnService
    {
        private readonly Dictionary<int, PlayerObservation> observations = new();
        private BotPluginConfig config;
        private Func<bool> isStandardWarmup;
        private int scheduleGeneration;
        private bool initialized;

        public void Init(BotPluginConfig pluginConfig, Func<bool> standardWarmupProvider)
        {
            if (initialized)
            {
                return;
            }

            config = pluginConfig ?? throw new ArgumentNullException(nameof(pluginConfig));
            isStandardWarmup = standardWarmupProvider ?? throw new ArgumentNullException(nameof(standardWarmupProvider));
            initialized = true;
            scheduleGeneration++;
            Logger.Info(
                $"[SCPSLBot] Global spectator respawn scanner started " +
                $"(interval={WarmupSpectatorRespawnPolicy.NormalizeScanInterval(config.RespawnScanIntervalSeconds):0.###}s).");
            ScheduleNextScan();
        }

        public void Terminate()
        {
            if (!initialized)
            {
                return;
            }

            initialized = false;
            scheduleGeneration++;
            observations.Clear();
            isStandardWarmup = null;
            config = null;
        }

        public void OnGenerationChanged()
        {
            observations.Clear();
        }

        public void ScanNow()
        {
            if (!initialized || !IsStandardWarmup())
            {
                observations.Clear();
                return;
            }

            ScanPlayers(Time.realtimeSinceStartup);
        }

        private void ScheduleNextScan()
        {
            int generation = scheduleGeneration;
            float interval = WarmupSpectatorRespawnPolicy.NormalizeScanInterval(config?.RespawnScanIntervalSeconds ?? 0.5f);
            Timing.CallDelayed(interval, () => ScanTick(generation));
        }

        private void ScanTick(int generation)
        {
            if (!initialized || generation != scheduleGeneration || config == null)
            {
                return;
            }

            try
            {
                ScanNow();
            }
            catch (Exception exception)
            {
                Logger.Error($"[SCPSLBot] Global spectator respawn scan recovered from: {exception}");
            }
            finally
            {
                if (initialized && generation == scheduleGeneration && config != null)
                {
                    ScheduleNextScan();
                }
            }
        }

        private void ScanPlayers(float now)
        {
            var seenPlayerIds = new HashSet<int>();
            foreach (Player player in Player.ReadyList)
            {
                if (!IsEligibleRealPlayer(player))
                {
                    continue;
                }

                int playerId = player.PlayerId;
                seenPlayerIds.Add(playerId);
                if (!observations.TryGetValue(playerId, out PlayerObservation observation)
                    || !ReferenceEquals(observation.ExpectedHub, player.ReferenceHub))
                {
                    observation = new PlayerObservation(player.ReferenceHub, player.Role);
                    observations[playerId] = observation;
                    ObserveInitialRole(player, observation, now);
                }
                else
                {
                    ObserveCurrentRole(player, observation, now);
                }

                TryRespawnEligibleSpectator(player, observation, now);
            }

            var observedPlayerIds = new List<int>(observations.Keys);
            foreach (int playerId in observedPlayerIds)
            {
                if (!seenPlayerIds.Contains(playerId))
                {
                    observations.Remove(playerId);
                }
            }
        }

        private void ObserveInitialRole(Player player, PlayerObservation observation, float now)
        {
            if (player.Role == RoleTypeId.Spectator)
            {
                ScheduleSpectator(observation, ResolveWarmupHumanRole(), SpectatorRespawnSource.JoinOrRecovery, now);
            }
            else if (IsRespawnRole(player.Role))
            {
                observation.LastPlayableRole = player.Role;
            }
        }

        private void ObserveCurrentRole(Player player, PlayerObservation observation, float now)
        {
            RoleTypeId currentRole = player.Role;
            RoleTypeId previousRole = observation.LastObservedRole;
            if (currentRole == RoleTypeId.Spectator)
            {
                SpectatorRespawnSource source = WarmupSpectatorRespawnPolicy.ClassifyTransition(
                    currentIsSpectator: true,
                    previousWasSpectator: previousRole == RoleTypeId.Spectator,
                    previousWasPlayable: IsRespawnRole(previousRole));
                if (source != SpectatorRespawnSource.None)
                {
                    RoleTypeId desiredRole = source == SpectatorRespawnSource.Death
                        ? previousRole
                        : ResolveRecoveryRole(observation);
                    ScheduleSpectator(observation, desiredRole, source, now);
                }
                else if (!observation.IsScheduled)
                {
                    ScheduleSpectator(observation, ResolveRecoveryRole(observation), SpectatorRespawnSource.JoinOrRecovery, now);
                }
            }
            else
            {
                observation.ClearSchedule();
                if (IsRespawnRole(currentRole))
                {
                    observation.LastPlayableRole = currentRole;
                }
            }

            observation.LastObservedRole = currentRole;
        }

        private void ScheduleSpectator(
            PlayerObservation observation,
            RoleTypeId desiredRole,
            SpectatorRespawnSource source,
            float now)
        {
            float delay = WarmupSpectatorRespawnPolicy.DelaySeconds(
                source,
                config.HumanRespawnDelayMs,
                config.SpectatorRespawnDelayMs);
            observation.Schedule(desiredRole, source, now + delay);
        }

        private void TryRespawnEligibleSpectator(Player player, PlayerObservation observation, float now)
        {
            if (!WarmupSpectatorRespawnPolicy.IsEligiblePlayerState(
                    isRealPlayer: IsEligibleRealPlayer(player),
                    isExactSpectator: player.Role == RoleTypeId.Spectator)
                || !observation.IsScheduled
                || now < observation.EligibleAt
                || !ReferenceEquals(observation.ExpectedHub, player.ReferenceHub))
            {
                return;
            }

            RoleTypeId requestedRole = IsRespawnRole(observation.DesiredRole)
                ? observation.DesiredRole
                : ResolveWarmupHumanRole();
            SpectatorRespawnSource source = observation.Source;
            try
            {
                player.SetRole(requestedRole, RoleChangeReason.Respawn, RoleSpawnFlags.All);
            }
            catch (Exception exception)
            {
                ScheduleRetry(player, observation, now, source, requestedRole, exception.Message);
                return;
            }

            RoleTypeId actualRole = player.Role;
            if (actualRole != RoleTypeId.Spectator)
            {
                observation.ClearSchedule();
                observation.LastObservedRole = actualRole;
                if (IsRespawnRole(actualRole))
                {
                    observation.LastPlayableRole = actualRole;
                }

                Logger.Info(
                    $"[SCPSLBot] Global spectator respawn completed for player {player.PlayerId} " +
                    $"(source={source}, requested={requestedRole}, actual={actualRole}, " +
                    $"zone={player.Zone}, position={player.Position}).");
                return;
            }

            ScheduleRetry(player, observation, now, source, requestedRole, "native role remained Spectator");
        }

        private void ScheduleRetry(
            Player player,
            PlayerObservation observation,
            float now,
            SpectatorRespawnSource source,
            RoleTypeId requestedRole,
            string detail)
        {
            observation.RetryAt(now + WarmupSpectatorRespawnPolicy.RetryDelaySeconds(config.RespawnScanIntervalSeconds));
            if (observation.AttemptCount == 1 || observation.AttemptCount % 20 == 0)
            {
                Logger.Warn(
                    $"[SCPSLBot] Global spectator respawn was rejected for player {player.PlayerId}; " +
                    $"retry {observation.AttemptCount} scheduled (source={source}, requested={requestedRole}, detail={detail}).");
            }
        }

        private RoleTypeId ResolveRecoveryRole(PlayerObservation observation)
        {
            return IsRespawnRole(observation.LastPlayableRole)
                ? observation.LastPlayableRole
                : ResolveWarmupHumanRole();
        }

        private RoleTypeId ResolveWarmupHumanRole()
        {
            return IsRespawnRole(config.WarmupHumanRole)
                ? config.WarmupHumanRole
                : RoleTypeId.NtfPrivate;
        }

        private bool IsStandardWarmup()
        {
            return initialized && isStandardWarmup != null && isStandardWarmup();
        }

        private static bool IsEligibleRealPlayer(Player player)
        {
            return player != null
                && !player.IsDestroyed
                && !player.IsHost
                && !player.IsDummy;
        }

        private static bool IsRespawnRole(RoleTypeId role)
        {
            return role is not RoleTypeId.None
                and not RoleTypeId.Spectator
                and not RoleTypeId.Overwatch
                and not RoleTypeId.Destroyed
                and not RoleTypeId.Filmmaker
                and not RoleTypeId.CustomRole;
        }

        private sealed class PlayerObservation
        {
            public PlayerObservation(ReferenceHub expectedHub, RoleTypeId initialRole)
            {
                ExpectedHub = expectedHub;
                LastObservedRole = initialRole;
                LastPlayableRole = RoleTypeId.None;
            }

            public ReferenceHub ExpectedHub { get; }
            public RoleTypeId LastObservedRole { get; set; }
            public RoleTypeId LastPlayableRole { get; set; }
            public RoleTypeId DesiredRole { get; private set; }
            public SpectatorRespawnSource Source { get; private set; }
            public float EligibleAt { get; private set; }
            public int AttemptCount { get; private set; }
            public bool IsScheduled { get; private set; }

            public void Schedule(RoleTypeId desiredRole, SpectatorRespawnSource source, float eligibleAt)
            {
                DesiredRole = desiredRole;
                Source = source;
                EligibleAt = eligibleAt;
                AttemptCount = 0;
                IsScheduled = true;
            }

            public void RetryAt(float eligibleAt)
            {
                EligibleAt = eligibleAt;
                AttemptCount++;
                IsScheduled = true;
            }

            public void ClearSchedule()
            {
                DesiredRole = RoleTypeId.None;
                Source = SpectatorRespawnSource.None;
                EligibleAt = 0f;
                AttemptCount = 0;
                IsScheduled = false;
            }
        }
    }
}
