using MEC;
using Mirror;
using PlayerRoles;
using PlayerRoles.FirstPersonControl;
using SCPSLBot.AI;
using SCPSLBot.Navigation;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace SCPSLBot.Warmup
{
    internal sealed class BotPopulationController
    {
        private const float ReconcileIntervalSeconds = 0.25f;
        private const int MaxRoleAttempts = 6;

        private readonly Dictionary<ReferenceHub, BotPopulationEntry> entries = new();
        private CoroutineHandle reconcileHandle;
        private BotPluginConfig config;
        private Func<bool> isEnabled;
        private Func<IReadOnlyList<BotPopulationSpec>> desiredSpecs;
        private Action<ReferenceHub, BotPopulationSpec> onBotPreparing;
        private Action<ReferenceHub, BotPopulationSpec> onBotReady;
        private bool initialized;
        private float nextSpawnAttemptAt;
        private int consecutiveSpawnFailures;

        public DateTime? LastReconcileUtc { get; private set; }
        public string LastSpawnError { get; private set; } = string.Empty;
        public string LastReconcileFault { get; private set; } = string.Empty;

        public void Init(
            BotPluginConfig pluginConfig,
            Func<bool> enabledProvider,
            Func<IReadOnlyList<BotPopulationSpec>> desiredSpecsProvider,
            Action<ReferenceHub, BotPopulationSpec> botPreparing,
            Action<ReferenceHub, BotPopulationSpec> botReady)
        {
            if (initialized)
            {
                return;
            }

            config = pluginConfig ?? throw new ArgumentNullException(nameof(pluginConfig));
            isEnabled = enabledProvider ?? throw new ArgumentNullException(nameof(enabledProvider));
            desiredSpecs = desiredSpecsProvider ?? throw new ArgumentNullException(nameof(desiredSpecsProvider));
            onBotPreparing = botPreparing ?? throw new ArgumentNullException(nameof(botPreparing));
            onBotReady = botReady ?? throw new ArgumentNullException(nameof(botReady));
            initialized = true;
            reconcileHandle = Timing.RunCoroutine(RunReconciler());
        }

        public void Terminate()
        {
            if (!initialized)
            {
                return;
            }

            initialized = false;
            if (reconcileHandle.IsRunning)
            {
                Timing.KillCoroutines(reconcileHandle);
            }

            DespawnAllOwnedBots();
            entries.Clear();
            config = null;
            isEnabled = null;
            desiredSpecs = null;
            onBotPreparing = null;
            onBotReady = null;
        }

        public void Wake()
        {
            nextSpawnAttemptAt = 0f;
        }

        public void OnRoundRestarted()
        {
            entries.Clear();
            consecutiveSpawnFailures = 0;
            nextSpawnAttemptAt = 0f;
        }

        public void OnBotDeath(ReferenceHub hub, float respawnDelaySeconds)
        {
            if (hub == null || !entries.TryGetValue(hub, out var entry))
            {
                return;
            }

            entry.State = BotPopulationState.Dead;
            entry.RoleAttempts = 0;
            entry.NextActionAt = Time.realtimeSinceStartup + Mathf.Max(0.05f, respawnDelaySeconds);
            entry.FailureReason = string.Empty;
        }

        public BotPopulationDiagnostics GetDiagnostics()
        {
            PruneMissingEntries();
            IReadOnlyList<BotPopulationSpec> desired = GetDesiredSpecs();
            RoleTypeId desiredRole = desired.FirstOrDefault()?.Role ?? ResolveDesiredRole(out _);
            string roleWarning = string.Empty;
            var live = entries.Values.Count(entry => entry.Spec != null && IsHealthy(entry, entry.Spec.Role));
            var states = entries.Values
                .GroupBy(entry => entry.State)
                .OrderBy(group => group.Key)
                .Select(group => $"{group.Key.ToString().ToLowerInvariant()}={group.Count()}")
                .ToArray();
            string arenaSummary = desired.Count == 0
                ? "none"
                : string.Join(",", desired
                    .GroupBy(spec => WarmupArenaService.ToId(spec.Arena), StringComparer.Ordinal)
                    .OrderBy(group => group.Key, StringComparer.Ordinal)
                    .Select(group => $"{group.Key}:{group.Count()}"));

            return new BotPopulationDiagnostics(
                desiredCount: desired.Count,
                trackedCount: BotManager.Instance.BotPlayers.Count,
                ownedCount: entries.Count,
                liveCount: live,
                networkReady: BotManager.Instance.CanSpawnBot(),
                navReady: NavigationSystem.Instance.IsReadyForCurrentMap,
                navGeneration: NavigationSystem.Instance.MapGeneration,
                navReadyGeneration: NavigationSystem.Instance.ReadyGeneration,
                desiredRole: desiredRole,
                roleWarning: roleWarning,
                states: states.Length == 0 ? "none" : string.Join(",", states),
                arenas: arenaSummary);
        }

        private IEnumerator<float> RunReconciler()
        {
            while (initialized)
            {
                try
                {
                    Reconcile();
                    LastReconcileFault = string.Empty;
                }
                catch (Exception exception)
                {
                    LastReconcileFault = $"{exception.GetType().Name}: {exception.Message}";
                    Debug.LogError($"SCPSLBot population reconciler recovered from a fault: {LastReconcileFault}");
                    Debug.LogException(exception);
                }

                yield return Timing.WaitForSeconds(ReconcileIntervalSeconds);
            }
        }

        private void Reconcile()
        {
            LastReconcileUtc = DateTime.UtcNow;
            PruneMissingEntries();

            if (isEnabled == null || !isEnabled())
            {
                DespawnAllOwnedBots();
                return;
            }

            if (!BotManager.Instance.CanSpawnBot() || !NavigationSystem.Instance.IsReadyForCurrentMap)
            {
                return;
            }

            IReadOnlyList<BotPopulationSpec> desired = GetDesiredSpecs();
            AssignSpecs(desired);
            int targetCount = desired.Count;
            TrimExcessBots(targetCount);

            var failedEntries = new List<BotPopulationEntry>();
            foreach (var entry in entries.Values.ToArray())
            {
                if (entry.Spec == null)
                {
                    continue;
                }

                // Publish the complete desired assignment before any ServerSetRole call below.
                // Spawning is synchronous, so registering it afterward is too late for spawn routing.
                onBotPreparing(entry.Hub, entry.Spec);

                if (IsHealthy(entry, entry.Spec.Role))
                {
                    entry.State = BotPopulationState.Alive;
                    entry.RoleAttempts = 0;
                    entry.FailureReason = string.Empty;
                    onBotReady(entry.Hub, entry.Spec);
                    continue;
                }

                RepairRole(entry, entry.Spec.Role);
                if (entry.State == BotPopulationState.Failed)
                {
                    failedEntries.Add(entry);
                }
            }

            foreach (var entry in failedEntries)
            {
                LastSpawnError = entry.FailureReason;
                Despawn(entry);
            }

            SpawnMissingBots(targetCount);
        }

        private IReadOnlyList<BotPopulationSpec> GetDesiredSpecs()
        {
            try
            {
                return (desiredSpecs?.Invoke() ?? Array.Empty<BotPopulationSpec>())
                    .Where(spec => spec != null)
                    .GroupBy(spec => spec.Key, StringComparer.Ordinal)
                    .Select(group => group.First())
                    .Take(10)
                    .ToArray();
            }
            catch (Exception exception)
            {
                LastSpawnError = $"Desired population planning failed: {exception.GetType().Name}: {exception.Message}";
                return Array.Empty<BotPopulationSpec>();
            }
        }

        private void AssignSpecs(IReadOnlyList<BotPopulationSpec> desired)
        {
            var desiredByKey = desired.ToDictionary(spec => spec.Key, StringComparer.Ordinal);
            var claimed = new HashSet<string>(StringComparer.Ordinal);

            foreach (BotPopulationEntry entry in entries.Values)
            {
                if (entry.Spec != null && desiredByKey.TryGetValue(entry.Spec.Key, out BotPopulationSpec current))
                {
                    if (entry.Spec.Role != current.Role || entry.Spec.Arena != current.Arena)
                    {
                        entry.RoleAttempts = 0;
                        entry.NextActionAt = 0f;
                    }
                    entry.Spec = current;
                    claimed.Add(current.Key);
                }
                else
                {
                    entry.Spec = null;
                }
            }

            Queue<BotPopulationSpec> unclaimed = new(desired.Where(spec => !claimed.Contains(spec.Key)));
            foreach (BotPopulationEntry entry in entries.Values
                         .Where(entry => entry.Spec == null)
                         .OrderBy(entry => entry.Hub?.PlayerId ?? int.MaxValue))
            {
                if (unclaimed.Count == 0)
                {
                    break;
                }

                entry.Spec = unclaimed.Dequeue();
                entry.RoleAttempts = 0;
                entry.NextActionAt = 0f;
            }
        }

        private void SpawnMissingBots(int targetCount)
        {
            if (entries.Count >= targetCount || Time.realtimeSinceStartup < nextSpawnAttemptAt)
            {
                return;
            }

            while (entries.Count < targetCount)
            {
                ReferenceHub hub;
                try
                {
                    hub = BotManager.Instance.AddUnassignedBotPlayer($"SCPSL Warmup Bot {entries.Count + 1}");
                }
                catch (Exception exception)
                {
                    RegisterSpawnFailure($"{exception.GetType().Name}: {exception.Message}");
                    return;
                }

                if (hub == null)
                {
                    RegisterSpawnFailure("BotManager rejected or failed native dummy construction.");
                    return;
                }

                entries.Add(hub, new BotPopulationEntry(hub));
                consecutiveSpawnFailures = 0;
                nextSpawnAttemptAt = 0f;
                LastSpawnError = string.Empty;
            }
        }

        private void RegisterSpawnFailure(string reason)
        {
            consecutiveSpawnFailures = Math.Min(consecutiveSpawnFailures + 1, 6);
            var delay = Mathf.Min(4f, 0.25f * Mathf.Pow(2f, consecutiveSpawnFailures - 1));
            nextSpawnAttemptAt = Time.realtimeSinceStartup + delay;
            LastSpawnError = reason;
            Debug.LogWarning($"SCPSLBot warmup bot spawn failed; retrying in {delay:F2}s: {reason}");
        }

        private void RepairRole(BotPopulationEntry entry, RoleTypeId desiredRole)
        {
            if (entry.Hub == null
                || entry.Hub.roleManager == null
                || !BotManager.Instance.BotPlayers.TryGetValue(entry.Hub, out var managedBot))
            {
                entry.State = BotPopulationState.Failed;
                entry.FailureReason = "Tracked warmup bot lost its managed/native role graph.";
                return;
            }

            var currentRole = entry.Hub.roleManager.CurrentRole;
            if (currentRole?.RoleTypeId == desiredRole && currentRole is FpcStandardRoleBase)
            {
                if (managedBot.CurrentBotPlayer == null && !managedBot.IsParked)
                {
                    managedBot.OnRoleChanged(currentRole, currentRole);
                }

                if (IsHealthy(entry, desiredRole))
                {
                    entry.State = BotPopulationState.Alive;
                    entry.RoleAttempts = 0;
                    entry.FailureReason = string.Empty;
                }

                return;
            }

            if (Time.realtimeSinceStartup < entry.NextActionAt)
            {
                return;
            }

            if (entry.RoleAttempts >= MaxRoleAttempts)
            {
                entry.State = BotPopulationState.Failed;
                entry.FailureReason = $"Role {desiredRole} was not established after {MaxRoleAttempts} attempts; actual={currentRole?.RoleTypeId.ToString() ?? "null"}.";
                return;
            }

            entry.State = currentRole == null || !entry.Hub.IsAlive()
                ? BotPopulationState.Respawning
                : BotPopulationState.Initializing;
            entry.RoleAttempts++;

            try
            {
                var reason = entry.Hub.IsAlive() ? RoleChangeReason.RemoteAdmin : RoleChangeReason.Respawn;
                entry.Hub.roleManager.ServerSetRole(desiredRole, reason);
            }
            catch (Exception exception)
            {
                entry.FailureReason = $"Role attempt {entry.RoleAttempts} threw {exception.GetType().Name}: {exception.Message}";
            }

            var retryDelay = Mathf.Min(4f, 0.25f * Mathf.Pow(2f, entry.RoleAttempts - 1));
            entry.NextActionAt = Time.realtimeSinceStartup + retryDelay;
        }

        private void TrimExcessBots(int targetCount)
        {
            if (entries.Count <= targetCount)
            {
                return;
            }

            var excess = entries.Values
                .OrderBy(entry => entry.Spec == null ? 0 : 1)
                .ThenByDescending(entry => entry.Hub == null ? int.MaxValue : entry.Hub.PlayerId)
                .Take(entries.Count - targetCount)
                .ToArray();

            foreach (var entry in excess)
            {
                Despawn(entry);
            }
        }

        private void Despawn(BotPopulationEntry entry)
        {
            entry.State = BotPopulationState.Despawning;
            entries.Remove(entry.Hub);
            if (entry.Hub != null)
            {
                BotManager.Instance.DespawnBot(entry.Hub);
            }
        }

        private void DespawnAllOwnedBots()
        {
            foreach (var entry in entries.Values.ToArray())
            {
                Despawn(entry);
            }
        }

        private void PruneMissingEntries()
        {
            foreach (var pair in entries.ToArray())
            {
                if (pair.Key == null || !BotManager.Instance.BotPlayers.ContainsKey(pair.Key))
                {
                    entries.Remove(pair.Key);
                }
            }
        }

        private RoleTypeId ResolveDesiredRole(out string warning)
        {
            var configured = config?.WarmupBotRole ?? RoleTypeId.ChaosRifleman;
            if (configured.TryGetRoleTemplate<FpcStandardRoleBase>(out _))
            {
                warning = string.Empty;
                return configured;
            }

            warning = $"Configured WarmupBotRole {configured} is not a bot-compatible FPC role; using ChaosRifleman.";
            return RoleTypeId.ChaosRifleman;
        }

        private static bool IsHealthy(BotPopulationEntry entry, RoleTypeId desiredRole)
        {
            if (entry?.Hub == null
                || !entry.Hub.IsDummy
                || !ReferenceHub.AllHubs.Contains(entry.Hub)
                || !NetworkServer.active
                || !ReferenceHub.TryGetHubNetID(entry.Hub.netId, out var registeredHub)
                || registeredHub != entry.Hub
                || !entry.Hub.IsAlive()
                || entry.Hub.roleManager?.CurrentRole is not FpcStandardRoleBase role
                || role.RoleTypeId != desiredRole
                || !BotManager.Instance.BotPlayers.TryGetValue(entry.Hub, out var managedBot))
            {
                return false;
            }

            return !managedBot.IsDisposed && managedBot.CurrentBotPlayer != null;
        }
    }

    internal sealed class BotPopulationEntry
    {
        public BotPopulationEntry(ReferenceHub hub)
        {
            Hub = hub;
            State = BotPopulationState.Initializing;
        }

        public ReferenceHub Hub { get; }
        public BotPopulationSpec Spec { get; set; }
        public BotPopulationState State { get; set; }
        public int RoleAttempts { get; set; }
        public float NextActionAt { get; set; }
        public string FailureReason { get; set; } = string.Empty;
    }

    internal sealed class BotPopulationSpec
    {
        public BotPopulationSpec(string key, WarmupArena arena, RoleTypeId role)
        {
            Key = string.IsNullOrWhiteSpace(key) ? throw new ArgumentException("A stable population key is required.", nameof(key)) : key;
            Arena = arena;
            Role = role;
        }

        public string Key { get; }
        public WarmupArena Arena { get; }
        public RoleTypeId Role { get; }
    }

    internal enum BotPopulationState
    {
        Initializing,
        Alive,
        Dead,
        Respawning,
        Failed,
        Despawning,
    }

    internal readonly struct BotPopulationDiagnostics
    {
        public BotPopulationDiagnostics(
            int desiredCount,
            int trackedCount,
            int ownedCount,
            int liveCount,
            bool networkReady,
            bool navReady,
            int navGeneration,
            int navReadyGeneration,
            RoleTypeId desiredRole,
            string roleWarning,
            string states,
            string arenas)
        {
            DesiredCount = desiredCount;
            TrackedCount = trackedCount;
            OwnedCount = ownedCount;
            LiveCount = liveCount;
            NetworkReady = networkReady;
            NavReady = navReady;
            NavGeneration = navGeneration;
            NavReadyGeneration = navReadyGeneration;
            DesiredRole = desiredRole;
            RoleWarning = roleWarning;
            States = states;
            Arenas = arenas;
        }

        public int DesiredCount { get; }
        public int TrackedCount { get; }
        public int OwnedCount { get; }
        public int LiveCount { get; }
        public bool NetworkReady { get; }
        public bool NavReady { get; }
        public int NavGeneration { get; }
        public int NavReadyGeneration { get; }
        public RoleTypeId DesiredRole { get; }
        public string RoleWarning { get; }
        public string States { get; }
        public string Arenas { get; }
    }
}
