using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.Handlers;
using LabApi.Features.Extensions;
using LabApi.Features.Wrappers;
using MapGeneration;
using MEC;
using PlayerRoles;
using SCPSLBot.AI;
using SCPSLBot.Warmup.Controls;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using LabLogger = LabApi.Features.Console.Logger;
using LabPlayer = LabApi.Features.Wrappers.Player;

namespace SCPSLBot.Warmup
{
    /// <summary>
    /// Owns the physical warmup arenas. Player role changes and bot reconciliation both route
    /// through this service so native role spawnpoints cannot silently move an actor out of the
    /// arena selected in Server-Specific Settings.
    /// </summary>
    internal sealed class WarmupArenaService
    {
        private readonly Dictionary<int, WarmupArena> playerArenas = new();
        private readonly Dictionary<int, float> lastArenaSwitchTimes = new();
        private readonly Dictionary<ReferenceHub, WarmupArena> botArenas = new();
        private readonly Dictionary<ReferenceHub, BotPopulationSpec> botSpecs = new();
        private readonly Dictionary<ReferenceHub, BotSpawnTransaction> pendingBotSpawns = new();
        private readonly HashSet<ReferenceHub> botsNeedingAssignmentPlacement = new();
        private readonly Dictionary<int, PlayerRoleSpawnOrigin> pendingRoleOrigins = new();
        private readonly Dictionary<int, ArenaSwitchTransaction> pendingArenaSwitches = new();
        private readonly Dictionary<int, WarmupArena> pendingSurfaceEvacuations = new();
        private readonly Dictionary<int, ArenaEntryTarget> pendingExplicitEvacuationTargets = new();
        private readonly NativeDoorArenaSpawnSelector arenaSpawnSelector = new();
        private BotPluginConfig config;
        private Func<bool> isStandardWarmup;
        private Func<int> currentGeneration;
        private Action wakePopulation;
        private bool initialized;
        private int scpRotation;
        private int nextBotSpawnToken;

        public void Init(
            BotPluginConfig pluginConfig,
            Func<bool> standardWarmupProvider,
            Func<int> generationProvider,
            Action populationWake)
        {
            if (initialized)
            {
                return;
            }

            config = pluginConfig ?? throw new ArgumentNullException(nameof(pluginConfig));
            isStandardWarmup = standardWarmupProvider ?? throw new ArgumentNullException(nameof(standardWarmupProvider));
            currentGeneration = generationProvider ?? throw new ArgumentNullException(nameof(generationProvider));
            wakePopulation = populationWake ?? throw new ArgumentNullException(nameof(populationWake));
            initialized = true;
            scpRotation++;

            PlayerEvents.Joined += OnPlayerJoined;
            PlayerEvents.Left += OnPlayerLeft;
            PlayerEvents.ChangingRole += OnPlayerChangingRole;
            PlayerEvents.Spawning += OnPlayerSpawning;
            PlayerEvents.Spawned += OnPlayerSpawned;
            ServerEvents.RoundRestarted += OnServerRoundRestarted;
        }

        public void Terminate()
        {
            if (!initialized)
            {
                return;
            }

            initialized = false;
            ServerEvents.RoundRestarted -= OnServerRoundRestarted;
            PlayerEvents.Spawned -= OnPlayerSpawned;
            PlayerEvents.Spawning -= OnPlayerSpawning;
            PlayerEvents.ChangingRole -= OnPlayerChangingRole;
            PlayerEvents.Left -= OnPlayerLeft;
            PlayerEvents.Joined -= OnPlayerJoined;
            playerArenas.Clear();
            lastArenaSwitchTimes.Clear();
            botArenas.Clear();
            botSpecs.Clear();
            pendingBotSpawns.Clear();
            botsNeedingAssignmentPlacement.Clear();
            pendingRoleOrigins.Clear();
            pendingArenaSwitches.Clear();
            pendingSurfaceEvacuations.Clear();
            pendingExplicitEvacuationTargets.Clear();
            arenaSpawnSelector.Reset();
            wakePopulation = null;
            currentGeneration = null;
            isStandardWarmup = null;
            config = null;
        }

        public void OnRoundRestarted()
        {
            botArenas.Clear();
            botSpecs.Clear();
            pendingBotSpawns.Clear();
            botsNeedingAssignmentPlacement.Clear();
            lastArenaSwitchTimes.Clear();
            pendingRoleOrigins.Clear();
            pendingArenaSwitches.Clear();
            pendingSurfaceEvacuations.Clear();
            pendingExplicitEvacuationTargets.Clear();
            arenaSpawnSelector.Reset();
            scpRotation++;
        }

        private void OnServerRoundRestarted() => OnRoundRestarted();

        public WarmupArena GetPlayerArena(int playerId) =>
            playerArenas.TryGetValue(playerId, out WarmupArena arena)
                ? arena
                : config?.DefaultWarmupArena ?? WarmupArena.SurfacePve;

        public string GetPlayerArenaId(int playerId) => ToId(GetPlayerArena(playerId));

        public bool TrySetPlayerArena(int playerId, string arenaId, out string response)
        {
            if (!IsStandardWarmup() || !TryParse(arenaId, out WarmupArena arena))
            {
                response = !IsStandardWarmup()
                    ? "Arena switching is only available during Standard warmup."
                    : "Unknown arena. Use surface, pvpve, or lcz.";
                return false;
            }

            if (!LabPlayer.TryGet(playerId, out LabPlayer player)
                || !IsRealPlayer(player))
            {
                response = "The player is no longer available.";
                return false;
            }

            WarmupArena previous = GetPlayerArena(playerId);
            bool isAlreadyActive = previous == arena;
            bool isExactSpectator = player.Role == RoleTypeId.Spectator;
            if (!WarmupArenaSelectionPolicy.RequiresNativeTransition(isAlreadyActive, isExactSpectator))
            {
                // A personalized dropdown's client baseline can be stale or forged. Treating the
                // active value as "reset" made an alternating stale index an unlimited native
                // role/inventory/health reset that never reached the arena-switch cooldown.
                response = $"Already in {GetDisplayName(arena)}.";
                return true;
            }

            float cooldown = Mathf.Max(0f, config.WarmupArenaSwitchCooldownSeconds);
            if (WarmupArenaSelectionPolicy.IsSwitchCooldownApplicable(isAlreadyActive)
                && lastArenaSwitchTimes.TryGetValue(playerId, out float last)
                && Time.realtimeSinceStartup - last < cooldown)
            {
                int remaining = Mathf.CeilToInt(cooldown - (Time.realtimeSinceStartup - last));
                response = $"Please wait {remaining}s before switching arenas again.";
                return false;
            }

            RoleTypeId defaultRole = DefaultPlayerRole(arena);
            if (!TryGetNativeArenaSpawn(arena, out Vector3 targetPosition))
            {
                response = $"A native spawnpoint is unavailable for {GetDisplayName(arena)}.";
                return false;
            }

            RoleTypeId originalRole = player.Role;
            Vector3 originalPosition = player.Position;
            Vector2 originalLookRotation = player.LookRotation;
            playerArenas[playerId] = arena;
            pendingArenaSwitches[playerId] = new ArenaSwitchTransaction(defaultRole, arena, targetPosition);
            try
            {
                player.SetRole(defaultRole, RoleChangeReason.RemoteAdmin, RoleSpawnFlags.All);
            }
            catch
            {
                // The verified postcondition and rollback below handle every native failure shape.
            }
            finally
            {
                pendingArenaSwitches.Remove(playerId);
                pendingRoleOrigins.Remove(playerId);
            }

            if (!IsRealPlayer(player)
                || player.Role != defaultRole
                || !IsPositionInArena(player.Position, arena))
            {
                bool restored = TryRollbackArenaSwitch(
                    player,
                    originalRole,
                    originalPosition,
                    originalLookRotation,
                    previous);
                response = restored
                    ? "Arena switch was cancelled or substituted; the previous state was restored."
                    : "Arena switch failed and exact rollback was unavailable; Surface safety was enforced.";
                return false;
            }

            lastArenaSwitchTimes[playerId] = Time.realtimeSinceStartup;
            SchedulePlayerPlacement(playerId, defaultRole, arena, 0.05f);
            SchedulePlayerPlacement(playerId, defaultRole, arena, 0.3f);
            wakePopulation();
            response = $"Switched to {GetDisplayName(arena)} as {defaultRole}.";
            return true;
        }

        public bool TryPreparePlayerRoleChange(
            LabPlayer player,
            RoleTypeId exactRole,
            out PlayerRoleArenaTransition transition)
        {
            transition = default;
            if (!IsStandardWarmup() || !IsRealPlayer(player))
            {
                return false;
            }

            WarmupArena previousArena = GetPlayerArena(player.PlayerId);
            bool foundPhysicalArena = TryGetPhysicalArena(player.Position, out WarmupArena physicalArena);
            WarmupArena originArena = Parse(WarmupRoleArenaRouting.ResolveRoleChangeOriginArenaId(
                player.Role == RoleTypeId.Spectator,
                ToId(previousArena),
                foundPhysicalArena ? ToId(physicalArena) : null,
                player.Zone == FacilityZone.Surface));
            bool isOnSurface = originArena == WarmupArena.SurfacePve;
            bool isSurfaceAllowedRole = WarmupRoleArenaRouting.IsSurfaceAllowedRole(exactRole.ToString());
            bool relocateFromSurface = isOnSurface && !isSurfaceAllowedRole;
            WarmupArena targetArena = isOnSurface
                ? Parse(WarmupRoleArenaRouting.ResolveSurfaceOriginArenaId(
                    isSurfaceAllowedRole,
                    exactRole.IsScp()))
                : originArena;

            transition = new PlayerRoleArenaTransition(
                player.PlayerId,
                previousArena,
                targetArena,
                player.Position,
                player.LookRotation,
                relocateFromSurface);
            playerArenas[player.PlayerId] = targetArena;
            if (relocateFromSurface)
            {
                pendingSurfaceEvacuations[player.PlayerId] = targetArena;
                if (TryGetNativeArenaSpawn(targetArena, out Vector3 evacuationPosition))
                {
                    pendingExplicitEvacuationTargets[player.PlayerId] = new ArenaEntryTarget(
                        targetArena,
                        evacuationPosition);
                }
                else
                {
                    pendingExplicitEvacuationTargets.Remove(player.PlayerId);
                }
            }
            else
            {
                pendingSurfaceEvacuations.Remove(player.PlayerId);
                pendingExplicitEvacuationTargets.Remove(player.PlayerId);
            }
            return true;
        }

        public void CompletePlayerRoleChange(
            LabPlayer player,
            RoleTypeId exactRole,
            PlayerRoleArenaTransition transition)
        {
            if (!IsStandardWarmup() || !IsRealPlayer(player) || transition.PlayerId != player.PlayerId)
            {
                return;
            }

            if (transition.RelocateFromSurface)
            {
                // The exact-role executor uses the preset anchor. Replace it synchronously with a
                // rotated native entry so Surface evacuation cannot collapse onto one containment.
                if (pendingExplicitEvacuationTargets.TryGetValue(
                        player.PlayerId,
                        out ArenaEntryTarget evacuationTarget)
                    && evacuationTarget.Arena == transition.TargetArena)
                {
                    player.Position = evacuationTarget.Position;
                }
                else
                {
                    PlaceAtArenaEntry(player, transition.TargetArena);
                }
                pendingExplicitEvacuationTargets.Remove(player.PlayerId);
                SchedulePlayerPlacement(player.PlayerId, exactRole, transition.TargetArena, 0.05f);
                SchedulePlayerPlacement(player.PlayerId, exactRole, transition.TargetArena, 0.3f);
                ShowPendingSurfaceEvacuation(player);
            }
            else
            {
                RestoreRoleChangePosition(player, transition);
                ScheduleRoleChangePositionRestore(exactRole, transition, 0.05f);
                ScheduleRoleChangePositionRestore(exactRole, transition, 0.3f);
            }

            wakePopulation();
        }

        public void RestorePlayerArena(PlayerRoleArenaTransition transition)
        {
            if (!initialized)
            {
                return;
            }

            pendingSurfaceEvacuations.Remove(transition.PlayerId);
            pendingExplicitEvacuationTargets.Remove(transition.PlayerId);

            if (transition.PreviousArena == WarmupArena.SurfacePve
                && LabPlayer.TryGet(transition.PlayerId, out LabPlayer player)
                && IsRealPlayer(player)
                && !IsSurfaceAllowedRole(player.Role))
            {
                // If an exact-role rollback itself was substituted, never leave an arbitrary role
                // logically assigned to Surface. Preserve a resolved facility location or evacuate.
                if (TryGetPhysicalArena(player.Position, out WarmupArena physical)
                    && physical != WarmupArena.SurfacePve)
                {
                    playerArenas[transition.PlayerId] = physical;
                }
                else
                {
                    WarmupArena safeArena = ResolveRoleArena(player.Role);
                    playerArenas[transition.PlayerId] = safeArena;
                    if (TryGetNativeArenaSpawn(safeArena, out Vector3 safePosition))
                    {
                        player.Position = safePosition;
                    }
                }
            }
            else
            {
                playerArenas[transition.PlayerId] = transition.PreviousArena;
            }
            wakePopulation();
        }

        public IReadOnlyList<BotPopulationSpec> BuildDesiredBotSpecs()
        {
            if (!IsStandardWarmup() || config == null)
            {
                return Array.Empty<BotPopulationSpec>();
            }

            int surfacePlayers = 0;
            int pvpvePlayers = 0;
            int lczPlayers = 0;
            foreach (LabPlayer player in LabPlayer.ReadyList)
            {
                if (!IsRealPlayer(player))
                {
                    continue;
                }

                switch (GetPlayerArena(player.PlayerId))
                {
                    case WarmupArena.SurfacePve: surfacePlayers++; break;
                    case WarmupArena.LightContainmentScp: lczPlayers++; break;
                    default: pvpvePlayers++; break;
                }
            }

            IReadOnlyList<WarmupArenaPopulationEntry> planned = WarmupArenaPopulationPlanner.Build(
                new WarmupArenaPopulationRequest
                {
                    SurfacePlayers = surfacePlayers,
                    HeavyEntrancePlayers = pvpvePlayers,
                    LightContainmentPlayers = lczPlayers,
                    FallbackBotCount = config.WarmupBotCount,
                    SurfaceBotCap = config.SurfacePveMaxBotCount,
                    SurfaceBotFactor = config.SurfacePveBotFactor,
                    HeavyEntranceBotCount = Math.Max(2, config.HeavyEntrancePvpveBotCount),
                    LightContainmentScpBotCount = Math.Max(1, config.LightContainmentScpBotCount),
                    TotalBotCap = 10,
                    DefaultArenaId = ToId(config.DefaultWarmupArena),
                    FallbackRoleId = config.WarmupBotRole.ToString(),
                    ScpRotation = scpRotation,
                });

            return planned.Select(entry => new BotPopulationSpec(
                    entry.Key,
                    Parse(entry.ArenaId),
                    Enum.TryParse(entry.RoleId, true, out RoleTypeId role) ? role : RoleTypeId.ChaosRifleman))
                .ToArray();
        }

        public void OnBotPreparing(ReferenceHub hub, BotPopulationSpec spec)
        {
            if (hub == null || spec == null || !IsStandardWarmup())
            {
                return;
            }

            bool changed = !botSpecs.TryGetValue(hub, out BotPopulationSpec current)
                || current.Key != spec.Key
                || current.Arena != spec.Arena
                || current.Role != spec.Role;

            botSpecs[hub] = spec;
            botArenas[hub] = spec.Arena;
            if (changed)
            {
                pendingBotSpawns.Remove(hub);
                botsNeedingAssignmentPlacement.Add(hub);
            }
        }

        public void OnBotReady(ReferenceHub hub, BotPopulationSpec spec)
        {
            if (hub == null || spec == null || !IsStandardWarmup())
            {
                return;
            }

            OnBotPreparing(hub, spec);
            if (pendingBotSpawns.ContainsKey(hub))
            {
                return;
            }

            bool assignmentNeedsPlacement = botsNeedingAssignmentPlacement.Remove(hub);

            if ((assignmentNeedsPlacement || !IsPositionInArena(hub.transform.position, spec.Arena))
                && TryGetNativeBotArenaSpawn(spec.Arena, spec.Role, out Vector3 position))
            {
                hub.transform.position = position;
            }
        }

        public bool CanHubsFight(ReferenceHub left, ReferenceHub right)
        {
            if (!IsStandardWarmup() || left == null || right == null)
            {
                return true;
            }

            if (TryGetPhysicalArena(left.transform.position, out WarmupArena leftPhysical)
                && TryGetPhysicalArena(right.transform.position, out WarmupArena rightPhysical))
            {
                return leftPhysical == rightPhysical;
            }

            return GetHubArena(left) == GetHubArena(right);
        }

        public bool CanPlayersTeleportWithinArena(LabPlayer requester, LabPlayer target)
        {
            if (!IsStandardWarmup() || !IsRealPlayer(requester) || !IsRealPlayer(target))
            {
                return false;
            }

            return TryGetPhysicalArena(requester.Position, out WarmupArena requesterArena)
                && TryGetPhysicalArena(target.Position, out WarmupArena targetArena)
                && requesterArena == targetArena;
        }

        private void OnPlayerJoined(PlayerJoinedEventArgs ev)
        {
            if (IsRealPlayer(ev.Player))
            {
                playerArenas[ev.Player.PlayerId] = config.DefaultWarmupArena;
                wakePopulation();
            }
        }

        private void OnPlayerLeft(PlayerLeftEventArgs ev)
        {
            if (ev.Player == null)
            {
                return;
            }

            playerArenas.Remove(ev.Player.PlayerId);
            lastArenaSwitchTimes.Remove(ev.Player.PlayerId);
            botArenas.Remove(ev.Player.ReferenceHub);
            botSpecs.Remove(ev.Player.ReferenceHub);
            pendingBotSpawns.Remove(ev.Player.ReferenceHub);
            botsNeedingAssignmentPlacement.Remove(ev.Player.ReferenceHub);
            pendingRoleOrigins.Remove(ev.Player.PlayerId);
            pendingArenaSwitches.Remove(ev.Player.PlayerId);
            pendingSurfaceEvacuations.Remove(ev.Player.PlayerId);
            pendingExplicitEvacuationTargets.Remove(ev.Player.PlayerId);
            wakePopulation();
        }

        private void OnPlayerChangingRole(PlayerChangingRoleEventArgs ev)
        {
            if (!IsStandardWarmup() || !IsRealPlayer(ev.Player))
            {
                return;
            }

            WarmupArena logicalArena = GetPlayerArena(ev.Player.PlayerId);
            bool foundPhysicalArena = TryGetPhysicalArena(ev.Player.Position, out WarmupArena physicalArena);
            WarmupArena originArena = Parse(WarmupRoleArenaRouting.ResolveRoleChangeOriginArenaId(
                ev.Player.Role == RoleTypeId.Spectator,
                ToId(logicalArena),
                foundPhysicalArena ? ToId(physicalArena) : null,
                ev.Player.Zone == FacilityZone.Surface));

            pendingRoleOrigins[ev.Player.PlayerId] = new PlayerRoleSpawnOrigin(
                ev.Player.Position,
                ev.Player.LookRotation,
                ev.ChangeReason,
                originArena,
                foundPhysicalArena && ev.Player.Role != RoleTypeId.Spectator);
        }

        private void OnPlayerSpawning(PlayerSpawningEventArgs ev)
        {
            if (!IsStandardWarmup() || ev.Player == null || ev.Player.IsDestroyed)
            {
                return;
            }

            WarmupArena arena;
            if (BotManager.Instance.BotPlayers.ContainsKey(ev.Player.ReferenceHub))
            {
                ReferenceHub hub = ev.Player.ReferenceHub;
                if (!botSpecs.TryGetValue(hub, out BotPopulationSpec spec)
                    || spec.Role != ev.Role.RoleTypeId)
                {
                    return;
                }

                arena = spec.Arena;
                Vector3 botPosition;
                float botRotation;
                if (WarmupBotSpawnAnchorPolicy.UsesExactNativeRoleSpawn(
                        ToId(arena),
                        ev.Role.RoleTypeId.ToString())
                    && IsPositionInArena(ev.SpawnLocation, arena))
                {
                    // Keep the native, spawn-reason-aware position selected for this exact role.
                    botPosition = ev.SpawnLocation;
                    botRotation = ev.HorizontalRotation;
                }
                else if (!TryGetNativeBotArenaSpawn(arena, ev.Role.RoleTypeId, out botPosition))
                {
                    return;
                }
                else
                {
                    botRotation = 0f;
                }

                ev.SetSpawnpoint(botPosition, botRotation);
                int token = unchecked(++nextBotSpawnToken);
                pendingBotSpawns[hub] = new BotSpawnTransaction(
                    token,
                    spec.Key,
                    ev.Role.RoleTypeId,
                    arena,
                    botPosition);
                botsNeedingAssignmentPlacement.Remove(hub);

                return;
            }
            else if (IsRealPlayer(ev.Player))
            {
                int playerId = ev.Player.PlayerId;
                bool hasOrigin = pendingRoleOrigins.TryGetValue(playerId, out PlayerRoleSpawnOrigin origin);
                pendingRoleOrigins.Remove(playerId);

                if (pendingArenaSwitches.TryGetValue(playerId, out ArenaSwitchTransaction arenaSwitch))
                {
                    if (ev.Role.RoleTypeId != arenaSwitch.ExpectedRole)
                    {
                        // A later event handler substituted the requested role. Do not apply its
                        // spawn; the synchronous arena transaction verifies and rolls back next.
                        ev.IsAllowed = false;
                        return;
                    }

                    ev.SetSpawnpoint(arenaSwitch.TargetPosition);
                    return;
                }

                if (hasOrigin
                    && origin.OriginArena == WarmupArena.SurfacePve
                    && !IsSurfaceAllowedRole(ev.Role.RoleTypeId))
                {
                    // Surface permits Foundation human roles. This covers native item
                    // transformations such as SCP-1507 tape as well as direct role requests.
                    arena = Parse(WarmupRoleArenaRouting.ResolveSurfaceOriginArenaId(
                        isSurfaceAllowedRole: false,
                        ev.Role.RoleTypeId.IsScp()));
                    playerArenas[playerId] = arena;
                    pendingSurfaceEvacuations[playerId] = arena;
                    if (pendingExplicitEvacuationTargets.TryGetValue(
                            playerId,
                            out ArenaEntryTarget evacuationTarget)
                        && evacuationTarget.Arena == arena)
                    {
                        ev.SetSpawnpoint(evacuationTarget.Position);
                        return;
                    }
                }
                else if (hasOrigin
                    && origin.CanPreservePhysicalPosition
                    && origin.OriginArena != WarmupArena.SurfacePve
                    && IsPositionPreservingNativeRoleChange(origin.ChangeReason))
                {
                    playerArenas[playerId] = origin.OriginArena;
                    ev.SetSpawnpoint(origin.Position, origin.LookRotation.y);
                    return;
                }
                else
                {
                    arena = GetPlayerArena(playerId);
                }
            }
            else
            {
                return;
            }

            if (pendingExplicitEvacuationTargets.TryGetValue(
                    ev.Player.PlayerId,
                    out ArenaEntryTarget target)
                && target.Arena == arena)
            {
                ev.SetSpawnpoint(target.Position);
            }
            else if (TryGetNativeArenaSpawn(arena, out Vector3 position))
            {
                ev.SetSpawnpoint(position);
            }
        }

        private void OnPlayerSpawned(PlayerSpawnedEventArgs ev)
        {
            if (!IsStandardWarmup() || ev.Player == null || ev.Player.IsDestroyed)
            {
                return;
            }

            if (BotManager.Instance.BotPlayers.ContainsKey(ev.Player.ReferenceHub))
            {
                if (pendingBotSpawns.TryGetValue(ev.Player.ReferenceHub, out BotSpawnTransaction transaction)
                    && transaction.ExpectedRole == ev.Player.Role)
                {
                    ScheduleBotPlacement(ev.Player.ReferenceHub, transaction, 0.1f, removeTransaction: false);
                    ScheduleBotPlacement(ev.Player.ReferenceHub, transaction, 0.35f, removeTransaction: true);
                }
                return;
            }

            if (IsRealPlayer(ev.Player))
            {
                WarmupArena arena = GetPlayerArena(ev.Player.PlayerId);
                bool finishedOnSurface = (TryGetPhysicalArena(ev.Player.Position, out WarmupArena physicalArena)
                        && physicalArena == WarmupArena.SurfacePve)
                    || ev.Player.Zone == FacilityZone.Surface;
                if (finishedOnSurface && !IsSurfaceAllowedRole(ev.Player.Role))
                {
                    // Final invariant: even if a foreign/native path bypassed or confused the
                    // pre-spawn origin capture, disallowed real-player roles never finish on Surface.
                    arena = ResolveRoleArena(ev.Player.Role);
                    playerArenas[ev.Player.PlayerId] = arena;
                    if (pendingExplicitEvacuationTargets.TryGetValue(
                            ev.Player.PlayerId,
                            out ArenaEntryTarget evacuationTarget)
                        && evacuationTarget.Arena == arena)
                    {
                        ev.Player.Position = evacuationTarget.Position;
                    }
                    else
                    {
                        PlaceAtArenaEntry(ev.Player, arena);
                    }
                    pendingSurfaceEvacuations[ev.Player.PlayerId] = arena;
                    LabLogger.Warn(
                        $"[SCPSLBot] Corrected forbidden real-player Surface spawn: " +
                        $"player={ev.Player.PlayerId}, role={ev.Player.Role}, targetArena={arena}, " +
                        $"finalZone={ev.Player.Zone}, finalPosition={ev.Player.Position}.");
                }

                ShowPendingSurfaceEvacuation(ev.Player);

                SchedulePlayerPlacement(ev.Player.PlayerId, ev.Player.Role, arena, 0.1f);
                SchedulePlayerPlacement(ev.Player.PlayerId, ev.Player.Role, arena, 0.35f);
                wakePopulation();
            }
        }

        private void SchedulePlayerPlacement(int playerId, RoleTypeId role, WarmupArena arena, float delay)
        {
            int generation = currentGeneration();
            Timing.CallDelayed(delay, () =>
            {
                if (!initialized || generation != currentGeneration() || !IsStandardWarmup()
                    || !LabPlayer.TryGet(playerId, out LabPlayer player)
                    || !IsRealPlayer(player) || player.Role != role || GetPlayerArena(playerId) != arena)
                {
                    return;
                }

                PlaceInArena(player, arena);
            });
        }

        private void ScheduleRoleChangePositionRestore(
            RoleTypeId role,
            PlayerRoleArenaTransition transition,
            float delay)
        {
            int generation = currentGeneration();
            Timing.CallDelayed(delay, () =>
            {
                if (!initialized || generation != currentGeneration() || !IsStandardWarmup()
                    || !LabPlayer.TryGet(transition.PlayerId, out LabPlayer player)
                    || !IsRealPlayer(player) || player.Role != role
                    || GetPlayerArena(transition.PlayerId) != transition.TargetArena)
                {
                    return;
                }

                RestoreRoleChangePosition(player, transition);
            });
        }

        private static void RestoreRoleChangePosition(
            LabPlayer player,
            PlayerRoleArenaTransition transition)
        {
            player.Position = transition.OriginalPosition;
            player.LookRotation = transition.OriginalLookRotation;
        }

        private void ScheduleBotPlacement(
            ReferenceHub hub,
            BotSpawnTransaction transaction,
            float delay,
            bool removeTransaction)
        {
            int generation = currentGeneration();
            Timing.CallDelayed(delay, () =>
            {
                if (!initialized || generation != currentGeneration() || !IsStandardWarmup()
                    || hub == null || hub.GetRoleId() != transaction.ExpectedRole
                    || !botSpecs.TryGetValue(hub, out BotPopulationSpec currentSpec)
                    || currentSpec.Key != transaction.SpecKey
                    || currentSpec.Arena != transaction.Arena
                    || currentSpec.Role != transaction.ExpectedRole
                    || !pendingBotSpawns.TryGetValue(hub, out BotSpawnTransaction currentTransaction)
                    || currentTransaction.Token != transaction.Token)
                {
                    return;
                }

                hub.transform.position = transaction.Position;
                if (removeTransaction)
                {
                    pendingBotSpawns.Remove(hub);
                }
            });
        }

        private void PlaceInArena(LabPlayer player, WarmupArena arena)
        {
            if (IsPositionInArena(player.Position, arena))
            {
                return;
            }

            if (TryGetNativeArenaSpawn(arena, out Vector3 position))
            {
                player.Position = position;
            }
        }

        private void PlaceAtArenaEntry(LabPlayer player, WarmupArena arena)
        {
            if (TryGetNativeArenaSpawn(arena, out Vector3 position))
            {
                player.Position = position;
            }
        }

        private void ShowPendingSurfaceEvacuation(LabPlayer player)
        {
            if (player == null
                || !pendingSurfaceEvacuations.TryGetValue(player.PlayerId, out WarmupArena arena))
            {
                return;
            }

            pendingSurfaceEvacuations.Remove(player.PlayerId);
            int playerId = player.PlayerId;
            int generation = currentGeneration();
            ReferenceHub expectedHub = player.ReferenceHub;
            bool toLightContainment = arena == WarmupArena.LightContainmentScp;
            // An explicit Apply result is broadcast synchronously after role completion. Emit the
            // higher-priority evacuation on the next tick so it is the final immediately visible text.
            Timing.CallDelayed(0.05f, () =>
            {
                if (!initialized
                    || generation != currentGeneration()
                    || !IsStandardWarmup()
                    || !LabPlayer.TryGet(playerId, out LabPlayer current)
                    || !ReferenceEquals(current.ReferenceHub, expectedHub)
                    || !IsRealPlayer(current)
                    || GetPlayerArena(playerId) != arena)
                {
                    return;
                }

                LabApiPlugin.Instance?.Presentation?.ShowSurfaceEvacuation(
                    current,
                    toLightContainment);
            });
        }

        private bool TryGetNativeArenaSpawn(WarmupArena arena, out Vector3 position)
        {
            if (arena == WarmupArena.HeavyEntrancePvpve
                && arenaSpawnSelector.TryGetNextHeavyEntranceSpawn(out position))
            {
                return true;
            }

            RoleTypeId nativeAnchorRole = arena switch
            {
                WarmupArena.SurfacePve => RoleTypeId.NtfPrivate,
                WarmupArena.LightContainmentScp => RoleTypeId.ClassD,
                _ => RoleTypeId.Scp939,
            };

            return TryGetValidatedNativeSpawn(nativeAnchorRole, arena, out position);
        }

        private bool TryGetNativeBotArenaSpawn(
            WarmupArena arena,
            RoleTypeId exactBotRole,
            out Vector3 position)
        {
            if (arena == WarmupArena.HeavyEntrancePvpve)
            {
                return TryGetNativeArenaSpawn(arena, out position);
            }

            position = default;
            string anchorRoleId = WarmupBotSpawnAnchorPolicy.ResolveAnchorRoleId(
                ToId(arena),
                exactBotRole.ToString());
            return Enum.TryParse(anchorRoleId, true, out RoleTypeId anchorRole)
                && Enum.IsDefined(typeof(RoleTypeId), anchorRole)
                && TryGetValidatedNativeSpawn(anchorRole, arena, out position);
        }

        private static bool TryGetValidatedNativeSpawn(
            RoleTypeId nativeAnchorRole,
            WarmupArena arena,
            out Vector3 position)
        {
            return nativeAnchorRole.TryGetRandomSpawnPoint(out position, out _)
                && RoomUtils.TryGetRoom(position, out var room)
                && IsArenaZone(arena, room.Zone);
        }

        private WarmupArena GetHubArena(ReferenceHub hub)
        {
            if (botArenas.TryGetValue(hub, out WarmupArena botArena))
            {
                return botArena;
            }

            return GetPlayerArena(hub.PlayerId);
        }

        private bool IsStandardWarmup() => initialized && isStandardWarmup != null && isStandardWarmup();

        private static bool IsRealPlayer(LabPlayer player) =>
            player != null && !player.IsDestroyed && player.IsReady && player.IsPlayer
            && !player.IsDummy && !player.IsHost && !string.IsNullOrWhiteSpace(player.UserId);

        private static RoleTypeId DefaultPlayerRole(WarmupArena arena) =>
            arena == WarmupArena.LightContainmentScp ? RoleTypeId.ClassD : RoleTypeId.NtfPrivate;

        private static bool IsPositionInArena(Vector3 position, WarmupArena arena) =>
            RoomUtils.TryGetRoom(position, out RoomIdentifier room) && IsArenaZone(arena, room.Zone);

        private static bool TryGetPhysicalArena(Vector3 position, out WarmupArena arena)
        {
            if (RoomUtils.TryGetRoom(position, out RoomIdentifier room))
            {
                arena = room.Zone switch
                {
                    FacilityZone.Surface => WarmupArena.SurfacePve,
                    FacilityZone.LightContainment => WarmupArena.LightContainmentScp,
                    _ => WarmupArena.HeavyEntrancePvpve,
                };
                return room.Zone is FacilityZone.Surface or FacilityZone.LightContainment
                    or FacilityZone.HeavyContainment or FacilityZone.Entrance;
            }

            arena = default;
            return false;
        }

        private bool TryRollbackArenaSwitch(
            LabPlayer player,
            RoleTypeId originalRole,
            Vector3 originalPosition,
            Vector2 originalLookRotation,
            WarmupArena previousArena)
        {
            if (!IsRealPlayer(player))
            {
                return false;
            }

            playerArenas[player.PlayerId] = previousArena;
            try
            {
                if (player.Role != originalRole)
                {
                    player.SetRole(originalRole, RoleChangeReason.RemoteAdmin, RoleSpawnFlags.All);
                }

                pendingRoleOrigins.Remove(player.PlayerId);
                if (player.Role == originalRole)
                {
                    player.Position = originalPosition;
                    player.LookRotation = originalLookRotation;
                    wakePopulation();
                    return true;
                }
            }
            catch
            {
                // Fall through to the Surface fail-safe below.
            }

            pendingRoleOrigins.Remove(player.PlayerId);
            if (IsRealPlayer(player)
                && !IsSurfaceAllowedRole(player.Role)
                && ((TryGetPhysicalArena(player.Position, out WarmupArena physical)
                        && physical == WarmupArena.SurfacePve)
                    || player.Zone == FacilityZone.Surface))
            {
                WarmupArena safeArena = ResolveRoleArena(player.Role);
                playerArenas[player.PlayerId] = safeArena;
                if (TryGetNativeArenaSpawn(safeArena, out Vector3 safePosition))
                {
                    player.Position = safePosition;
                }
            }

            wakePopulation();
            return false;
        }

        private static bool IsPositionPreservingNativeRoleChange(RoleChangeReason reason) =>
            reason is RoleChangeReason.ItemUsage or RoleChangeReason.Revived or RoleChangeReason.Resurrected;

        private static WarmupArena ResolveRoleArena(RoleTypeId role) =>
            Parse(WarmupRoleArenaRouting.ResolveArenaId(role.IsScp()));

        private static bool IsSurfaceAllowedRole(RoleTypeId role) =>
            WarmupRoleArenaRouting.IsSurfaceAllowedRole(role.ToString());

        public static bool IsArenaZone(WarmupArena arena, FacilityZone zone) => arena switch
        {
            WarmupArena.SurfacePve => zone == FacilityZone.Surface,
            WarmupArena.LightContainmentScp => zone == FacilityZone.LightContainment,
            _ => zone is FacilityZone.HeavyContainment or FacilityZone.Entrance,
        };

        public static string GetDisplayName(WarmupArena arena) => arena switch
        {
            WarmupArena.SurfacePve => "Surface PvE",
            WarmupArena.LightContainmentScp => "LCZ SCP arena",
            _ => "HCZ / EZ PvPvE",
        };

        public static string ToId(WarmupArena arena) => arena switch
        {
            WarmupArena.SurfacePve => "surface",
            WarmupArena.LightContainmentScp => "lcz",
            _ => "pvpve",
        };

        public static bool TryParse(string value, out WarmupArena arena)
        {
            switch ((value ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "surface": case "surfacepve": arena = WarmupArena.SurfacePve; return true;
                case "lcz": case "lcscp": case "lightcontainmentscp": arena = WarmupArena.LightContainmentScp; return true;
                case "pvpve": case "hcz": case "ez": case "heavyentrancepvpve": arena = WarmupArena.HeavyEntrancePvpve; return true;
                default: return Enum.TryParse(value, true, out arena);
            }
        }

        private static WarmupArena Parse(string value) =>
            TryParse(value, out WarmupArena arena) ? arena : WarmupArena.HeavyEntrancePvpve;
    }

    internal readonly struct PlayerRoleArenaTransition
    {
        public PlayerRoleArenaTransition(
            int playerId,
            WarmupArena previousArena,
            WarmupArena targetArena,
            Vector3 originalPosition,
            Vector2 originalLookRotation,
            bool relocateFromSurface)
        {
            PlayerId = playerId;
            PreviousArena = previousArena;
            TargetArena = targetArena;
            OriginalPosition = originalPosition;
            OriginalLookRotation = originalLookRotation;
            RelocateFromSurface = relocateFromSurface;
        }

        public int PlayerId { get; }
        public WarmupArena PreviousArena { get; }
        public WarmupArena TargetArena { get; }
        public Vector3 OriginalPosition { get; }
        public Vector2 OriginalLookRotation { get; }
        public bool RelocateFromSurface { get; }
    }

    internal readonly struct PlayerRoleSpawnOrigin
    {
        public PlayerRoleSpawnOrigin(
            Vector3 position,
            Vector2 lookRotation,
            RoleChangeReason changeReason,
            WarmupArena originArena,
            bool canPreservePhysicalPosition)
        {
            Position = position;
            LookRotation = lookRotation;
            ChangeReason = changeReason;
            OriginArena = originArena;
            CanPreservePhysicalPosition = canPreservePhysicalPosition;
        }

        public Vector3 Position { get; }
        public Vector2 LookRotation { get; }
        public RoleChangeReason ChangeReason { get; }
        public WarmupArena OriginArena { get; }
        public bool CanPreservePhysicalPosition { get; }
    }

    internal readonly struct ArenaSwitchTransaction
    {
        public ArenaSwitchTransaction(RoleTypeId expectedRole, WarmupArena targetArena, Vector3 targetPosition)
        {
            ExpectedRole = expectedRole;
            TargetArena = targetArena;
            TargetPosition = targetPosition;
        }

        public RoleTypeId ExpectedRole { get; }
        public WarmupArena TargetArena { get; }
        public Vector3 TargetPosition { get; }
    }

    internal readonly struct ArenaEntryTarget
    {
        public ArenaEntryTarget(WarmupArena arena, Vector3 position)
        {
            Arena = arena;
            Position = position;
        }

        public WarmupArena Arena { get; }
        public Vector3 Position { get; }
    }

    internal readonly struct BotSpawnTransaction
    {
        public BotSpawnTransaction(
            int token,
            string specKey,
            RoleTypeId expectedRole,
            WarmupArena arena,
            Vector3 position)
        {
            Token = token;
            SpecKey = specKey;
            ExpectedRole = expectedRole;
            Arena = arena;
            Position = position;
        }

        public int Token { get; }
        public string SpecKey { get; }
        public RoleTypeId ExpectedRole { get; }
        public WarmupArena Arena { get; }
        public Vector3 Position { get; }
    }
}
