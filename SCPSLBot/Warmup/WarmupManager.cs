using LabApi.Features.Console;
using LabApi.Features.Wrappers;
using PlayerRoles;
using System;

namespace SCPSLBot.Warmup
{
    internal sealed class WarmupManager
    {
        public static WarmupManager Instance { get; } = new WarmupManager();

        private readonly BotPopulationController botPopulation = new();
        private readonly WarmupArenaService arenas = new();
        private readonly WarmupRoundRespawnService respawns = new();
        private readonly WarmupPlayerSpawnProtectionService playerSpawnProtection = new();
        private readonly WarmupHazardService hazards = new();
        private readonly WarmupModeCoordinator modeCoordinator = new();
        private BotPluginConfig config;
        private bool initialized;

        public WarmupMode Mode => modeCoordinator.Mode;
        public bool IsStandardWarmup => modeCoordinator.IsStandardWarmup;
        internal BotPopulationController BotPopulation => botPopulation;
        internal event Action<WarmupMode> ModeChanged;

        public void Init(BotPluginConfig pluginConfig)
        {
            if (initialized)
            {
                return;
            }

            config = pluginConfig ?? throw new ArgumentNullException(nameof(pluginConfig));
            initialized = true;

            foreach (var normalizedSetting in config.Normalize())
            {
                Logger.Warn($"[SCPSLBot] Normalized config value: {normalizedSetting}");
            }

            arenas.Init(
                config,
                () => modeCoordinator.IsStandardWarmup,
                () => modeCoordinator.Generation,
                botPopulation.Wake);
            botPopulation.Init(
                config,
                () => modeCoordinator.IsStandardWarmup,
                arenas.BuildDesiredBotSpecs,
                arenas.OnBotPreparing,
                arenas.OnBotReady);
            respawns.Init(
                config,
                () => modeCoordinator.IsStandardWarmup);
            hazards.Init(config, () => modeCoordinator.IsStandardWarmup);
            modeCoordinator.Init(
                config,
                botPopulation,
                respawns,
                hazards,
                () => LabApiPlugin.Instance?.SaveSettings());
            playerSpawnProtection.Init(() => modeCoordinator.IsStandardWarmup);
        }

        public void Terminate()
        {
            if (!initialized)
            {
                return;
            }

            playerSpawnProtection.Terminate();
            modeCoordinator.Terminate();
            hazards.Terminate();
            respawns.Terminate();
            arenas.Terminate();
            botPopulation.Terminate();
            config = null;
            initialized = false;
        }

        public bool TrySetMode(string modeName, out string response)
        {
            WarmupMode before = Mode;
            bool changed = modeCoordinator.TrySetMode(modeName, out response);
            NotifyModeChanged(before);
            return changed;
        }

        public void SetMode(WarmupMode mode)
        {
            WarmupMode before = Mode;
            modeCoordinator.SetMode(mode);
            NotifyModeChanged(before);
        }

        public bool TrySetBotCount(int targetCount, int maxBotCount, out string response)
        {
            if (config == null)
            {
                response = "SCPSLBot warmup config is not loaded.";
                return false;
            }

            int max = Math.Max(0, Math.Min(maxBotCount, 10));
            int target = Math.Max(0, Math.Min(targetCount, max));
            config.WarmupBotCount = target;
            LabApiPlugin.Instance?.SaveSettings();

            if (IsStandardWarmup)
            {
                botPopulation.Wake();
            }

            response = $"Warmup bot count set to {target} (max {max}).";
            return true;
        }

        public bool TryAddMaintainedBot(out string response)
        {
            if (config == null)
            {
                response = "SCPSLBot warmup config is not loaded.";
                return false;
            }

            if (!IsStandardWarmup)
            {
                response = "bot_add maintains the Standard warmup population. Enable Standard mode first.";
                return false;
            }

            if (config.WarmupBotCount >= 10)
            {
                response = "Maintained warmup bot cap reached (10).";
                return false;
            }

            return TrySetBotCount(config.WarmupBotCount + 1, 10, out response);
        }

        public string GetPlayerArenaId(int playerId) => arenas.GetPlayerArenaId(playerId);

        public bool TrySetPlayerArena(int playerId, string arenaId, out string response) =>
            arenas.TrySetPlayerArena(playerId, arenaId, out response);

        public bool TryPreparePlayerRoleChange(
            Player player,
            RoleTypeId exactRole,
            out PlayerRoleArenaTransition transition) =>
            arenas.TryPreparePlayerRoleChange(player, exactRole, out transition);

        public void CompletePlayerRoleChange(
            Player player,
            RoleTypeId exactRole,
            PlayerRoleArenaTransition transition) =>
            arenas.CompletePlayerRoleChange(player, exactRole, transition);

        public void RestorePlayerArena(PlayerRoleArenaTransition transition) =>
            arenas.RestorePlayerArena(transition);

        public bool CanHubsFightInWarmup(ReferenceHub left, ReferenceHub right) =>
            arenas.CanHubsFight(left, right);

        public bool CanPlayersTeleportWithinArena(Player requester, Player target) =>
            arenas.CanPlayersTeleportWithinArena(requester, target);

        private WarmupManager()
        {
        }

        private void NotifyModeChanged(WarmupMode before)
        {
            if (before != Mode)
            {
                ModeChanged?.Invoke(Mode);
            }
        }
    }
}
