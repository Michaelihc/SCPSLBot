using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.Arguments.ServerEvents;
using LabApi.Events.Arguments.WarheadEvents;
using LabApi.Events.Handlers;
using Interactables.Interobjects;
using Interactables.Interobjects.DoorUtils;
using LightContainmentZoneDecontamination;
using MapGeneration.Holidays;
using System;
using System.Collections.Generic;
using System.Linq;
using LabElevatorDoor = LabApi.Features.Wrappers.ElevatorDoor;
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
        private readonly HashSet<HubertMoon> ownedMoonControllers = new(Infrastructure.ManagedReferenceComparer<HubertMoon>.Instance);
        // The controller whose override this service set; a later round, admin command, warhead or
        // another plugin owns any other value.
        private DecontaminationController ownedDecontaminationOverride;
        private BotPluginConfig config;
        private Func<bool> isStandardWarmup;
        private bool initialized;
        private bool surfaceElevatorLockLogged;

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
            ServerEvents.DeadmanSequenceActivating += OnDeadmanSequenceActivating;
        }

        public void Terminate()
        {
            if (!initialized)
            {
                return;
            }

            LabWarheadEvents.Detonating -= OnWarheadDetonating;
            ServerEvents.DeadmanSequenceActivating -= OnDeadmanSequenceActivating;
            LabWarheadEvents.Starting -= OnWarheadStarting;
            PlayerEvents.InteractingWarheadLever -= OnPlayerInteractingWarheadLever;
            PlayerEvents.UnlockingWarheadButton -= OnPlayerUnlockingWarheadButton;
            PlayerEvents.Cuffing -= OnPlayerCuffing;
            RestoreSurfaceElevators();
            RestoreLczDecontaminationIfOwned();
            RestoreMoonControllers();
            initialized = false;
            isStandardWarmup = null;
            config = null;
        }

        public void ApplyWarmupPolicies()
        {
            LockSurfaceElevators();
            DisableLczDecontaminationIfNeeded();
            DisableWarheadIfNeeded();
            PauseMoonControllers();
        }

        public void OnWarmupDisabled()
        {
            RestoreSurfaceElevators();
            RestoreLczDecontaminationIfOwned();
            RestoreMoonControllers();
        }

        private void PauseMoonControllers()
        {
            if (!IsStandardWarmup())
            {
                return;
            }

            ownedMoonControllers.RemoveWhere(moon => moon == null);
            foreach (HubertMoon moon in UnityEngine.Object.FindObjectsByType<HubertMoon>(UnityEngine.FindObjectsSortMode.None))
            {
                if (!moon.enabled)
                {
                    continue;
                }

                // The Halloween ending explodes players independently of the warhead. Pausing the
                // native controller also stops its countdown, so endless warmup cannot reach it.
                moon.enabled = false;
                ownedMoonControllers.Add(moon);
            }
        }

        private void RestoreMoonControllers()
        {
            foreach (HubertMoon moon in ownedMoonControllers)
            {
                if (moon != null)
                {
                    moon.enabled = true;
                }
            }

            ownedMoonControllers.Clear();
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
            if (!ShouldBlockDisarming()
                || !WarmupParticipation.IsManagedRole(ev.Player.Role)
                || !WarmupParticipation.IsManagedRole(ev.Target.Role))
            {
                return;
            }

            ev.IsAllowed = false;
            global::BotRoundControl.BotRoundControlPlugin.Instance?.Presentation?.ShowDisarmingDisabled(ev.Player);
        }

        private void OnPlayerUnlockingWarheadButton(PlayerUnlockingWarheadButtonEventArgs ev)
        {
            if (!ShouldBlockWarhead() || !WarmupParticipation.IsManagedRole(ev.Player.Role))
            {
                return;
            }

            ev.IsAllowed = false;
            DisableWarheadIfNeeded();
            global::BotRoundControl.BotRoundControlPlugin.Instance?.Presentation?.ShowWarheadDisabled(ev.Player);
        }

        private void OnPlayerInteractingWarheadLever(PlayerInteractingWarheadLeverEventArgs ev)
        {
            if (!ShouldBlockWarhead() || !WarmupParticipation.IsManagedRole(ev.Player.Role))
            {
                return;
            }

            ev.IsAllowed = false;
            DisableWarheadIfNeeded();
            global::BotRoundControl.BotRoundControlPlugin.Instance?.Presentation?.ShowWarheadDisabled(ev.Player);
        }

        private void OnWarheadStarting(WarheadStartingEventArgs ev)
        {
            if (!ShouldBlockWarhead())
            {
                return;
            }

            ev.IsAllowed = false;
            DisableWarheadIfNeeded();
            Logger.Info("[SCPSLBot] Warhead start blocked in warmup.");
        }

        private void OnWarheadDetonating(WarheadDetonatingEventArgs ev)
        {
            if (!ShouldBlockWarhead())
            {
                return;
            }

            ev.IsAllowed = false;
            DisableWarheadIfNeeded();
            Logger.Info("[SCPSLBot] Warhead detonation blocked in warmup.");
        }

        private bool ShouldBlockWarhead()
        {
            return IsStandardWarmup() && config.DisableWarheadInWarmup;
        }

        private void OnDeadmanSequenceActivating(DeadmanSequenceActivatingEventArgs ev)
        {
            if (ShouldBlockWarhead())
            {
                ev.IsAllowed = false;
                DisableWarheadIfNeeded();
            }
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
                    LabApi.Features.Wrappers.Warhead.IsLocked = false;
                    LabApi.Features.Wrappers.Warhead.Stop(null);
                    if (LabApi.Features.Wrappers.Warhead.IsDetonationInProgress)
                    {
                        // Native cancellation refuses the final ten seconds. Reset the public
                        // countdown state as well when entering warmup or rejecting an instant nuke.
                        LabApi.Features.Wrappers.Warhead.BaseController?.InstantPrepare();
                    }
                }

                LabApi.Features.Wrappers.Warhead.LeverStatus = false;
                LabApi.Features.Wrappers.Warhead.IsAuthorized = false;
                LabApi.Features.Wrappers.Warhead.IsLocked = false;
                LabApi.Features.Wrappers.Warhead.ForceCountdownToggle = false;
            }
            catch (Exception ex)
            {
                Logger.Warn($"[SCPSLBot] Failed to disable warhead in warmup: {ex.Message}");
            }
        }

        private void DisableLczDecontaminationIfNeeded()
        {
            if (ShouldDisableLczDecontamination()
                && SetLczDecontamination(DecontaminationController.DecontaminationStatus.Disabled, "disabled"))
            {
                ownedDecontaminationOverride = DecontaminationController.Singleton;
            }
        }

        private void RestoreLczDecontaminationIfOwned()
        {
            DecontaminationController owned = ownedDecontaminationOverride;
            ownedDecontaminationOverride = null;
            DecontaminationController controller = DecontaminationController.Singleton;
            if (owned != null
                && ReferenceEquals(controller, owned)
                && controller.DecontaminationOverride == DecontaminationController.DecontaminationStatus.Disabled)
            {
                SetLczDecontamination(DecontaminationController.DecontaminationStatus.None, "enabled");
            }
        }

        private static bool SetLczDecontamination(DecontaminationController.DecontaminationStatus status, string label)
        {
            try
            {
                var controller = DecontaminationController.Singleton;
                if (controller == null || controller.DecontaminationOverride == status)
                {
                    return false;
                }

                controller.DecontaminationOverride = status;
                Logger.Info($"[SCPSLBot] LCZ decontamination {label}.");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warn($"[SCPSLBot] Failed to set LCZ decontamination {label}: {ex.Message}");
                return false;
            }
        }

    }
}
