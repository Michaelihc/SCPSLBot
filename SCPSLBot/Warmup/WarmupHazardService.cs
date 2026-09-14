using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.Arguments.WarheadEvents;
using LabApi.Events.Handlers;
using Interactables.Interobjects;
using Interactables.Interobjects.DoorUtils;
using LightContainmentZoneDecontamination;
using System;
using System.Collections.Generic;
using System.Linq;
using LabElevatorDoor = LabApi.Features.Wrappers.ElevatorDoor;
using LabPlayer = LabApi.Features.Wrappers.Player;
using LabWarheadEvents = LabApi.Events.Handlers.WarheadEvents;
using Logger = LabApi.Features.Console.Logger;

namespace SCPSLBot.Warmup
{
    internal sealed class WarmupHazardService
    {
        private static readonly HashSet<ElevatorGroup> SurfaceElevatorGroups = new()
        {
            ElevatorGroup.GateA01,
            ElevatorGroup.GateA02,
            ElevatorGroup.GateB,
        };

        private readonly HashSet<LabElevatorDoor> ownedSurfaceElevatorLocks = new();
        private BotPluginConfig config;
        private Func<bool> isStandardWarmup;
        private bool initialized;
        private bool surfaceElevatorLockLogged;
        private int adminWarheadOverrideUntilTick;

        public void Init(BotPluginConfig pluginConfig, Func<bool> standardWarmupProvider)
        {
            if (initialized)
            {
                return;
            }

            config = pluginConfig ?? throw new ArgumentNullException(nameof(pluginConfig));
            isStandardWarmup = standardWarmupProvider ?? throw new ArgumentNullException(nameof(standardWarmupProvider));
            initialized = true;

            PlayerEvents.Cuffing += OnPlayerCuffing;
            PlayerEvents.UnlockingWarheadButton += OnPlayerUnlockingWarheadButton;
            PlayerEvents.InteractingWarheadLever += OnPlayerInteractingWarheadLever;
            LabWarheadEvents.Starting += OnWarheadStarting;
            LabWarheadEvents.Detonating += OnWarheadDetonating;
        }

        public void Terminate()
        {
            if (!initialized)
            {
                return;
            }

            LabWarheadEvents.Detonating -= OnWarheadDetonating;
            LabWarheadEvents.Starting -= OnWarheadStarting;
            PlayerEvents.InteractingWarheadLever -= OnPlayerInteractingWarheadLever;
            PlayerEvents.UnlockingWarheadButton -= OnPlayerUnlockingWarheadButton;
            PlayerEvents.Cuffing -= OnPlayerCuffing;
            adminWarheadOverrideUntilTick = 0;
            RestoreSurfaceElevators();
            RestoreLczDecontaminationIfOwned();
            initialized = false;
            isStandardWarmup = null;
            config = null;
        }

        public void ApplyWarmupPolicies()
        {
            LockSurfaceElevators();
            DisableLczDecontaminationIfNeeded();
            DisableWarheadIfNeeded();
        }

        public void OnWarmupDisabled()
        {
            adminWarheadOverrideUntilTick = 0;
            RestoreSurfaceElevators();
            RestoreLczDecontaminationIfOwned();
        }

        private void LockSurfaceElevators()
        {
            if (!IsStandardWarmup())
            {
                return;
            }

            ownedSurfaceElevatorLocks.RemoveWhere(door => door == null || door.IsDestroyed);
            foreach (LabElevatorDoor door in LabElevatorDoor.List.Where(
                         door => door != null && !door.IsDestroyed && SurfaceElevatorGroups.Contains(door.Group)))
            {
                if ((door.LockReason & DoorLockReason.AdminCommand) != 0)
                {
                    continue;
                }

                door.Lock(DoorLockReason.AdminCommand, true);
                ownedSurfaceElevatorLocks.Add(door);
            }

            if (!surfaceElevatorLockLogged && ownedSurfaceElevatorLocks.Count > 0)
            {
                surfaceElevatorLockLogged = true;
                Logger.Info($"[SCPSLBot] Locked {ownedSurfaceElevatorLocks.Count} Surface elevator doors; other doors retain native state.");
            }
        }

        private void RestoreSurfaceElevators()
        {
            foreach (LabElevatorDoor door in ownedSurfaceElevatorLocks)
            {
                if (door != null && !door.IsDestroyed)
                {
                    door.Lock(DoorLockReason.AdminCommand, false);
                }
            }

            ownedSurfaceElevatorLocks.Clear();
            surfaceElevatorLockLogged = false;
        }

        private void OnPlayerCuffing(PlayerCuffingEventArgs ev)
        {
            if (!ShouldBlockDisarming())
            {
                return;
            }

            ev.IsAllowed = false;
            LabApiPlugin.Instance?.Presentation?.ShowDisarmingDisabled(ev.Player);
        }

        private void OnPlayerUnlockingWarheadButton(PlayerUnlockingWarheadButtonEventArgs ev)
        {
            if (!ShouldBlockWarhead())
            {
                return;
            }

            ev.IsAllowed = false;
            DisableWarheadIfNeeded();
            LabApiPlugin.Instance?.Presentation?.ShowWarheadDisabled(ev.Player);
        }

        private void OnPlayerInteractingWarheadLever(PlayerInteractingWarheadLeverEventArgs ev)
        {
            if (!ShouldBlockWarhead())
            {
                return;
            }

            ev.IsAllowed = false;
            DisableWarheadIfNeeded();
            LabApiPlugin.Instance?.Presentation?.ShowWarheadDisabled(ev.Player);
        }

        private void OnWarheadStarting(WarheadStartingEventArgs ev)
        {
            if (!ShouldBlockWarhead())
            {
                return;
            }

            if (!ev.IsAutomatic && IsAdminWarheadStart(ev.Player))
            {
                adminWarheadOverrideUntilTick = unchecked(Environment.TickCount + 10 * 60 * 1000);
                Logger.Info("[SCPSLBot] Admin warhead start allowed in warmup.");
                return;
            }

            ev.IsAllowed = false;
            adminWarheadOverrideUntilTick = 0;
            DisableWarheadIfNeeded();
            Logger.Info("[SCPSLBot] Warhead start blocked in warmup.");
        }

        private void OnWarheadDetonating(WarheadDetonatingEventArgs ev)
        {
            if (!ShouldBlockWarhead())
            {
                return;
            }

            if (IsAdminWarheadOverrideActive() || IsAdminWarheadStart(ev.Player))
            {
                adminWarheadOverrideUntilTick = 0;
                Logger.Info("[SCPSLBot] Admin warhead detonation allowed in warmup.");
                return;
            }

            ev.IsAllowed = false;
            adminWarheadOverrideUntilTick = 0;
            DisableWarheadIfNeeded();
            Logger.Info("[SCPSLBot] Warhead detonation blocked in warmup.");
        }

        private bool ShouldBlockWarhead()
        {
            return IsStandardWarmup() && config.DisableWarheadInWarmup;
        }

        private bool ShouldDisableLczDecontamination()
        {
            return IsStandardWarmup() && config.DisableLczDecontaminationInWarmup;
        }

        private bool ShouldBlockDisarming()
        {
            return IsStandardWarmup() && config.DisableDisarmingInWarmup;
        }

        private bool IsStandardWarmup()
        {
            return initialized && isStandardWarmup != null && isStandardWarmup();
        }

        private void DisableWarheadIfNeeded()
        {
            if (!ShouldBlockWarhead())
            {
                return;
            }

            try
            {
                if (!LabApi.Features.Wrappers.Warhead.Exists)
                {
                    return;
                }

                if (LabApi.Features.Wrappers.Warhead.IsDetonationInProgress)
                {
                    LabApi.Features.Wrappers.Warhead.Stop(null);
                }

                LabApi.Features.Wrappers.Warhead.LeverStatus = false;
                LabApi.Features.Wrappers.Warhead.IsAuthorized = false;
                LabApi.Features.Wrappers.Warhead.IsLocked = false;
                LabApi.Features.Wrappers.Warhead.ForceCountdownToggle = false;
                LabApi.Features.Wrappers.Warhead.DeadManSwitchRemaining = 0f;
            }
            catch (Exception ex)
            {
                Logger.Warn($"[SCPSLBot] Failed to disable warhead in warmup: {ex.Message}");
            }
        }

        private void DisableLczDecontaminationIfNeeded()
        {
            if (ShouldDisableLczDecontamination())
            {
                SetLczDecontamination(DecontaminationController.DecontaminationStatus.Disabled, "disabled");
            }
        }

        private void RestoreLczDecontaminationIfOwned()
        {
            if (config != null && config.DisableLczDecontaminationInWarmup)
            {
                SetLczDecontamination(DecontaminationController.DecontaminationStatus.None, "enabled");
            }
        }

        private static void SetLczDecontamination(DecontaminationController.DecontaminationStatus status, string label)
        {
            try
            {
                var controller = DecontaminationController.Singleton;
                if (controller == null || controller.DecontaminationOverride == status)
                {
                    return;
                }

                controller.DecontaminationOverride = status;
                Logger.Info($"[SCPSLBot] LCZ decontamination {label}.");
            }
            catch (Exception ex)
            {
                Logger.Warn($"[SCPSLBot] Failed to set LCZ decontamination {label}: {ex.Message}");
            }
        }

        private bool IsAdminWarheadOverrideActive()
        {
            return adminWarheadOverrideUntilTick != 0
                   && unchecked(adminWarheadOverrideUntilTick - Environment.TickCount) > 0;
        }

        private static bool IsAdminWarheadStart(LabPlayer player)
        {
            return player == null
                   || player.RemoteAdminAccess
                   || (player.PlayerId == 1
                       && string.Equals(player.Nickname, "Dedicated Server", StringComparison.OrdinalIgnoreCase));
        }
    }
}
