using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.Handlers;
using LabApi.Features.Console;
using LabApi.Features.Wrappers;
using PlayerRoles;
using SCPSLBot.AI;
using System;
using System.Linq;

namespace SCPSLBot.Warmup
{
    internal sealed class WarmupManager
    {
        public static WarmupManager Instance { get; } = new WarmupManager();

        private readonly BotPopulationController botPopulation = new();
        private readonly WarmupArenaService arenas = new();
        private readonly WarmupRoundRespawnService respawns = new();
        private readonly WarmupNativeWaveService nativeWaves = new();
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
                arenas.OnBotReady,
                arenas.OnBotReleased);
            respawns.Init(
                config,
                () => modeCoordinator.IsStandardWarmup);
            hazards.Init(config, () => modeCoordinator.IsStandardWarmup);
            nativeWaves.Init(config, () => modeCoordinator.IsStandardWarmup);
            modeCoordinator.Init(
                config,
                botPopulation,
                respawns,
                hazards,
                () => LabApiPlugin.Instance?.SaveSettings());
            playerSpawnProtection.Init(() => modeCoordinator.IsStandardWarmup);
            PlayerEvents.ChangedRole += OnPlayerChangedRole;
        }

        public void Terminate()
        {
            if (!initialized)
            {
                return;
            }

            PlayerEvents.ChangedRole -= OnPlayerChangedRole;
            playerSpawnProtection.Terminate();
            nativeWaves.Terminate();
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

        public bool TryAddIndependentBot(out string response)
        {
            if (config == null)
            {
                response = "SCPSLBot is not loaded.";
                return false;
            }

            int independentCount = Math.Max(0,
                BotManager.Instance.BotPlayers.Count - botPopulation.GetDiagnostics().OwnedCount);
            if (independentCount >= 10)
            {
                response = "Independent bot cap reached (10).";
                return false;
            }

            ReferenceHub hub = null;
            try
            {
                hub = BotManager.Instance.AddUnassignedBotPlayer($"SCPSL Manual Bot {independentCount + 1}");
                if (hub?.roleManager == null)
                {
                    if (hub != null)
                    {
                        BotManager.Instance.DespawnBot(hub);
                    }

                    response = "Failed to create an independent bot. Check the server log.";
                    return false;
                }

                hub.roleManager.ServerSetRole(RoleTypeId.ChaosRifleman, RoleChangeReason.RemoteAdmin);
            }
            catch (Exception exception)
            {
                if (hub != null)
                {
                    BotManager.Instance.DespawnBot(hub);
                }

                response = $"Failed to create an independent bot: {exception.GetType().Name}: {exception.Message}";
                return false;
            }

            response = $"Spawned independent bot player_id={hub.PlayerId} role={hub.roleManager.CurrentRole.RoleTypeId}.";
            return true;
        }

        public bool TryManageBot(int playerId, out string response)
        {
            if (!TryFindBot(playerId, out ReferenceHub hub, out response))
            {
                return false;
            }

            return botPopulation.TryManageBot(hub, out response);
        }

        public bool TryUnmanageBot(int playerId, out string response)
        {
            if (!TryFindBot(playerId, out ReferenceHub hub, out response))
            {
                return false;
            }

            return botPopulation.TryUnmanageBot(hub, out response);
        }

        public string GetPlayerArenaId(int playerId) => arenas.GetPlayerArenaId(playerId);

        public bool TrySetPlayerArena(int playerId, string arenaId, out string response) =>
            arenas.TrySetPlayerArena(playerId, arenaId, out response);

        public bool TrySetPlayerArena(Player player, string arenaId, out string response) =>
            arenas.TrySetPlayerArena(player, arenaId, out response);

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

        public void SynchronizePlayerArena(Player player) =>
            arenas.SynchronizePlayerArena(player);

        private void OnPlayerChangedRole(PlayerChangedRoleEventArgs ev)
        {
            if (!WarmupParticipation.IsRealPlayer(ev.Player) || WarmupParticipation.IsManagedRole(ev.NewRole.RoleTypeId))
            {
                return;
            }

            // Leaving participation relinquishes all player state; returning starts fresh.
            arenas.ForgetPlayer(ev.Player);
            respawns.ForgetPlayer(ev.Player);
            playerSpawnProtection.ForgetPlayer(ev.Player);
        }

        private WarmupManager()
        {
        }

        private static bool TryFindBot(int playerId, out ReferenceHub hub, out string response)
        {
            hub = BotManager.Instance.BotPlayers.Keys.FirstOrDefault(candidate => candidate?.PlayerId == playerId);
            if (hub != null)
            {
                response = string.Empty;
                return true;
            }

            response = $"Player {playerId} is not an SCPSLBot dummy.";
            return false;
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
