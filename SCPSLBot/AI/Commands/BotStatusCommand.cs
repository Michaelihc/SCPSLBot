using CommandSystem;
using SCPSLBot.AI.FirstPersonControl.Perception.Senses.Sight;
using SCPSLBot.Navigation;
using SCPSLBot.Warmup;
using System;

namespace SCPSLBot.AI.Commands
{
    [CommandHandler(typeof(RemoteAdminCommandHandler))]
    internal sealed class BotStatusCommand : ICommand
    {
        public string Command => "bot_status";
        public string[] Aliases => Array.Empty<string>();
        public string Description => "Show SCPSLBot lifecycle and readiness diagnostics.";

        public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
        {
            if (!sender.CheckPermission(PlayerPermissions.FacilityManagement, out response))
            {
                return false;
            }

            var population = WarmupManager.Instance.BotPopulation;
            var state = population.GetDiagnostics();
            var manager = BotManager.Instance;
            var sight = SightSense.Diagnostics;
            var navigation = NavigationSystem.Instance;

            response = $"mode={WarmupManager.Instance.Mode}; desired={state.DesiredCount}; desired_role={state.DesiredRole}; "
                       + $"tracked={state.TrackedCount}; owned={state.OwnedCount}; live={state.LiveCount}; states={state.States}; arenas={state.Arenas}; "
                       + $"network_ready={state.NetworkReady}; nav_ready={state.NavReady}; nav_generation={state.NavGeneration}; nav_ready_generation={state.NavReadyGeneration}; "
                       + $"last_reconcile={Format(population.LastReconcileUtc)}; last_spawn_error={Value(population.LastSpawnError)}; reconcile_fault={Value(population.LastReconcileFault)}; "
                       + $"ai_runner_running={manager.RunnerIsRunning}; ai_heartbeat={Format(manager.LastRunnerHeartbeatUtc == default ? null : manager.LastRunnerHeartbeatUtc)}; "
                       + $"ai_last_fault={Value(manager.LastRunnerFault)}; ai_last_fault_time={Format(manager.LastRunnerFaultUtc)}; parked={manager.ParkedBotCount}; "
                       + $"sight_senses={sight.ActiveSenseCount}; raycast_capacity={sight.TotalRaycastCapacity}; tracked_colliders={sight.TrackedColliderCount}; "
                       + $"nav_error={Value(navigation.LastLoadError)}; role_warning={Value(state.RoleWarning)}";
            return true;
        }

        private static string Format(DateTime? value)
            => value.HasValue ? value.Value.ToString("O") : "never";

        private static string Value(string value)
            => string.IsNullOrWhiteSpace(value) ? "none" : value.Replace(';', ',');
    }
}
