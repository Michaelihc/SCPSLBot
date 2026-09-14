using LabApi.Events.Arguments.ServerEvents;
using LabApi.Events.Handlers;
using MEC;
using System;
using LabRound = LabApi.Features.Wrappers.Round;
using Logger = LabApi.Features.Console.Logger;

namespace SCPSLBot.Warmup
{
    internal sealed class WarmupModeCoordinator
    {
        private BotPluginConfig config;
        private BotPopulationController botPopulation;
        private WarmupRoundRespawnService respawns;
        private WarmupHazardService hazards;
        private Action persistConfig;
        private bool initialized;
        private int generation;

        public WarmupMode Mode => config?.WarmupMode ?? WarmupMode.None;
        public bool IsStandardWarmup => Mode == WarmupMode.Standard;
        public int Generation => generation;

        public void Init(
            BotPluginConfig pluginConfig,
            BotPopulationController populationController,
            WarmupRoundRespawnService respawnService,
            WarmupHazardService hazardService,
            Action persist)
        {
            if (initialized)
            {
                return;
            }

            config = pluginConfig ?? throw new ArgumentNullException(nameof(pluginConfig));
            botPopulation = populationController ?? throw new ArgumentNullException(nameof(populationController));
            respawns = respawnService ?? throw new ArgumentNullException(nameof(respawnService));
            hazards = hazardService ?? throw new ArgumentNullException(nameof(hazardService));
            persistConfig = persist ?? throw new ArgumentNullException(nameof(persist));
            NormalizeConfiguredMode();
            generation++;
            initialized = true;

            ServerEvents.RoundStarted += OnRoundStarted;
            ServerEvents.RoundRestarted += OnRoundRestarted;
            ServerEvents.WaitingForPlayers += OnWaitingForPlayers;
            ServerEvents.RoundEndingConditionsCheck += OnRoundEndingConditionsCheck;

            if (IsStandardWarmup)
            {
                ActivateStandardWarmup("startup");
            }
            else
            {
                hazards.OnWarmupDisabled();
            }
        }

        public void Terminate()
        {
            if (!initialized)
            {
                return;
            }

            initialized = false;
            generation++;
            ServerEvents.RoundEndingConditionsCheck -= OnRoundEndingConditionsCheck;
            ServerEvents.WaitingForPlayers -= OnWaitingForPlayers;
            ServerEvents.RoundRestarted -= OnRoundRestarted;
            ServerEvents.RoundStarted -= OnRoundStarted;
            respawns?.OnGenerationChanged();
            hazards?.OnWarmupDisabled();
            persistConfig = null;
            hazards = null;
            respawns = null;
            botPopulation = null;
            config = null;
        }

        public bool TrySetMode(string modeName, out string response)
        {
            if (!Enum.TryParse(modeName, true, out WarmupMode mode)
                || !Enum.IsDefined(typeof(WarmupMode), mode))
            {
                response = "Unknown warmup mode. Use: none, standard.";
                return false;
            }

            SetMode(mode);
            response = $"Warmup mode set to {mode}.";
            return true;
        }

        public void SetMode(WarmupMode mode)
        {
            if (config == null || !Enum.IsDefined(typeof(WarmupMode), mode))
            {
                return;
            }

            config.WarmupMode = mode;
            generation++;
            respawns.OnGenerationChanged();
            persistConfig();

            if (mode == WarmupMode.Standard)
            {
                ActivateStandardWarmup("mode switch");
            }
            else
            {
                hazards.OnWarmupDisabled();
                Logger.Info("[SCPSLBot] Warmup disabled.");
            }

            botPopulation.Wake();
        }

        private void NormalizeConfiguredMode()
        {
            if (Enum.IsDefined(typeof(WarmupMode), config.WarmupMode))
            {
                return;
            }

            // The persisted runtime selection wins. DefaultWarmupMode is only recovery for an
            // invalid enum value left by a newer or manually edited configuration.
            config.WarmupMode = Enum.IsDefined(typeof(WarmupMode), config.DefaultWarmupMode)
                ? config.DefaultWarmupMode
                : WarmupMode.None;
        }

        private void ActivateStandardWarmup(string reason)
        {
            var scheduleGeneration = generation;
            ApplyStandardWarmup();
            Timing.CallDelayed(0.5f, () => RetryStandardWarmupActivation(scheduleGeneration));
            Timing.CallDelayed(1.5f, () => RetryStandardWarmupActivation(scheduleGeneration));
            Timing.CallDelayed(3f, () => RetryStandardWarmupActivation(scheduleGeneration));
            Timing.CallDelayed(5f, () => RetryStandardWarmupActivation(scheduleGeneration));
            Logger.Info($"[SCPSLBot] Standard warmup enabled ({reason}): rounds locked and deaths will respawn.");
        }

        private void RetryStandardWarmupActivation(int scheduleGeneration)
        {
            if (!initialized || scheduleGeneration != generation || !IsStandardWarmup)
            {
                return;
            }

            ApplyStandardWarmup();
        }

        private void ApplyStandardWarmup()
        {
            StartRoundIfNeeded();
            hazards.ApplyWarmupPolicies();
            respawns.ScanNow();
            botPopulation.Wake();
        }

        private void OnRoundStarted()
        {
            if (IsStandardWarmup)
            {
                ApplyStandardWarmup();
            }
        }

        private void OnRoundRestarted()
        {
            generation++;
            respawns.OnGenerationChanged();
            botPopulation.OnRoundRestarted();
            if (IsStandardWarmup)
            {
                var restartGeneration = generation;
                Timing.CallDelayed(0.5f, () => RetryStandardWarmupActivation(restartGeneration));
            }
        }

        private void OnWaitingForPlayers()
        {
            botPopulation.Wake();
            if (IsStandardWarmup)
            {
                var waitingGeneration = generation;
                Timing.CallDelayed(1f, () => RetryStandardWarmupActivation(waitingGeneration));
            }
        }

        private void OnRoundEndingConditionsCheck(RoundEndingConditionsCheckEventArgs ev)
        {
            if (IsStandardWarmup)
            {
                ev.CanEnd = false;
            }
        }

        private void StartRoundIfNeeded()
        {
            if (!IsStandardWarmup || LabRound.IsRoundStarted)
            {
                return;
            }

            try
            {
                LabRound.Start();
            }
            catch (NullReferenceException ex)
            {
                Logger.Warn($"[SCPSLBot] Standard warmup round start was not ready yet: {ex.Message}");
            }
        }
    }
}
