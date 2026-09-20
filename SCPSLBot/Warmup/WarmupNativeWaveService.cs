using LabApi.Events.Arguments.ServerEvents;
using LabApi.Events.Handlers;
using System;

namespace SCPSLBot.Warmup
{
    internal sealed class WarmupNativeWaveService
    {
        private BotPluginConfig config;
        private Func<bool> isStandardWarmup;

        public void Init(BotPluginConfig pluginConfig, Func<bool> standardWarmupProvider)
        {
            if (config != null)
                return;

            config = pluginConfig ?? throw new ArgumentNullException(nameof(pluginConfig));
            isStandardWarmup = standardWarmupProvider ?? throw new ArgumentNullException(nameof(standardWarmupProvider));
            ServerEvents.WaveTeamSelecting += OnWaveTeamSelecting;
            ServerEvents.WaveRespawning += OnWaveRespawning;
        }

        public void Terminate()
        {
            if (config == null)
                return;

            ServerEvents.WaveTeamSelecting -= OnWaveTeamSelecting;
            ServerEvents.WaveRespawning -= OnWaveRespawning;
            isStandardWarmup = null;
            config = null;
        }

        private bool ShouldSuppress => config?.DisableNativeRespawnWavesInWarmup == true
            && isStandardWarmup();

        private void OnWaveTeamSelecting(WaveTeamSelectingEventArgs ev)
        {
            // Stop the normal wave path before its vehicle animation starts.
            if (ShouldSuppress)
                ev.IsAllowed = false;
        }

        private void OnWaveRespawning(WaveRespawningEventArgs ev)
        {
            // Also guard direct/forced spawns and waves selected before warmup was enabled.
            if (ShouldSuppress)
                ev.IsAllowed = false;
        }
    }
}
