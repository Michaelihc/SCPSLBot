using InventorySystem;
using InventorySystem.Items;
using InventorySystem.Items.Firearms;
using InventorySystem.Items.Firearms.Modules;
using InventorySystem.Items.Firearms.ShotEvents;
using PlayerStatsSystem;
using SCPSLBot.Api;
using UnityEngine;

namespace SCPSLBot.AI.FirstPersonControl.Combat
{
    /// <summary>
    /// Owns human firearm selection, reload cadence, aiming, and firing behavior. Only Standard
    /// warmup hands out a firearm; in a normal round a human without a usable firearm does not
    /// engage (<see cref="CanEngage"/>) and the brain's survival behaviors take over.
    /// </summary>
    internal sealed class HumanWeaponCombatStrategy
    {
        private const float ChaseDistance = 11f;
        private const float RetreatDistance = 4.5f;

        private readonly FpcBotCombat combat;
        private float nextReloadAttemptTime;

        public HumanWeaponCombatStrategy(FpcBotCombat combat)
        {
            this.combat = combat;
        }

        /// <summary>
        /// True when this human can fight: Standard warmup (a firearm is granted on demand) or a
        /// carried firearm with loaded or reserve ammo. Allocation-free; safe to call every frame.
        /// </summary>
        public bool CanEngage()
        {
            return BotServerPolicy.IsStandardWarmup || HasUsableFirearm(combat.BotPlayer.BotHub.PlayerHub);
        }

        /// <summary>A carried firearm that can still fire: loaded rounds, reserve ammo, or no magazine model at all.</summary>
        internal static bool HasUsableFirearm(ReferenceHub hub)
        {
            return TryGetUsableFirearm(hub, out _);
        }

        internal static bool TryGetUsableFirearm(ReferenceHub hub, out Firearm firearm)
        {
            firearm = null;
            var inventory = hub != null ? hub.inventory : null;
            if (inventory == null)
            {
                return false;
            }

            if (inventory.CurInstance is Firearm held && HasAmmo(held))
            {
                firearm = held;
                return true;
            }

            foreach (var item in inventory.UserInventory.Items.Values)
            {
                if (item is Firearm candidate && HasAmmo(candidate))
                {
                    firearm = candidate;
                    return true;
                }
            }

            return false;
        }

        /// <summary>Rounds in the gun plus reserve ammo of its type; firearms without a magazine model count as armed.</summary>
        internal static bool HasAmmo(Firearm firearm)
        {
            if (!firearm.TryGetModule<IPrimaryAmmoContainerModule>(out var primaryAmmo))
            {
                return true;
            }

            return GetLoadedAmmo(firearm, primaryAmmo) > 0 || firearm.Owner.inventory.GetCurAmmo(primaryAmmo.AmmoType) > 0;
        }

        public void Run(CombatTarget target)
        {
            var firearm = EnsureFirearmEquipped();
            var targetPosition = target.Hub.transform.position;
            var surfaceDoorBlockingTarget = combat.OpenSurfaceDoorTowardTarget(target.Hub);

            if (surfaceDoorBlockingTarget || target.Distance > ChaseDistance)
            {
                combat.MoveToCombatPosition(targetPosition);
            }
            else
            {
                combat.StrafeAroundTarget(targetPosition, target.Distance, RetreatDistance, ChaseDistance);
            }

            combat.BotPlayer.LookToPosition(target.AimPoint);
            if (!target.HasLineOfSight || firearm == null || Time.time < combat.NextShotTime)
            {
                return;
            }

            PrepareActionForShot(firearm);
            if (IsReloading(firearm))
            {
                return;
            }

            if (ShouldReload(firearm))
            {
                TryReloadNormally();
                return;
            }

            var settings = FpcBotCombat.CurrentSettings;
            if (!combat.IsAimedAt(target.AimPoint, settings.AimAngleDegrees))
            {
                return;
            }

            combat.NextShotTime = Time.time + settings.ShotCooldownSeconds;
            if (!combat.TryClickDummyAction("Shoot->Click"))
            {
                FireDirectly(firearm, target.Hub);
            }
        }

        private Firearm EnsureFirearmEquipped()
        {
            var inventory = combat.BotPlayer.BotHub.PlayerHub.inventory;
            if (inventory.CurInstance is Firearm currentFirearm)
            {
                return currentFirearm;
            }

            if (combat.BotPlayer.ItemLease.IsHeld)
            {
                // Something else holds the hands for a moment (a keycard at a door); fire once it is done.
                return null;
            }

            Firearm firearm = null;
            foreach (var item in inventory.UserInventory.Items.Values)
            {
                if (item is not Firearm candidate)
                {
                    continue;
                }

                firearm ??= candidate;
                if (HasAmmo(candidate))
                {
                    firearm = candidate;
                    break;
                }
            }

            // Standard warmup arms every human; a normal round never conjures a weapon.
            if (firearm == null && BotServerPolicy.IsStandardWarmup)
            {
                firearm = inventory.ServerAddItem(ItemType.GunCOM15, ItemAddReason.AdminCommand) as Firearm;
            }

            if (firearm == null)
            {
                return null;
            }

            inventory.ServerSelectItem(firearm.ItemSerial);
            PrepareActionForShot(firearm);
            return firearm;
        }

        private static void PrepareActionForShot(Firearm firearm)
        {
            if (!firearm.TryGetModule<AutomaticActionModule>(out var action))
            {
                return;
            }

            action.Cocked = true;
            if (!action.OpenBolt
                && action.AmmoStored <= 0
                && firearm.TryGetModule<IPrimaryAmmoContainerModule>(out var primaryAmmo)
                && primaryAmmo.AmmoStored > 0)
            {
                action.ServerCycleAction();
            }

            action.BoltLocked = false;
            action.ServerResync();
        }

        private static bool ShouldReload(Firearm firearm)
        {
            if (!firearm.TryGetModule<IPrimaryAmmoContainerModule>(out var primaryAmmo))
            {
                return false;
            }

            return HumanWeaponReloadPolicy.ShouldAttemptReload(
                GetLoadedAmmo(firearm, primaryAmmo),
                firearm.Owner.inventory.GetCurAmmo(primaryAmmo.AmmoType));
        }

        private static int GetLoadedAmmo(Firearm firearm, IPrimaryAmmoContainerModule primaryAmmo)
        {
            var loaded = primaryAmmo.AmmoStored;
            if (firearm.TryGetModule<AutomaticActionModule>(out var action))
            {
                loaded += action.AmmoStored;
            }

            return loaded;
        }

        private static bool IsReloading(Firearm firearm)
        {
            return firearm.TryGetModule<IReloaderModule>(out var reloader) && reloader.IsReloadingOrUnloading;
        }

        private void TryReloadNormally()
        {
            if (Time.time < nextReloadAttemptTime)
            {
                return;
            }

            nextReloadAttemptTime = Time.time + 0.45f;
            combat.TryClickDummyAction("Reload->Click");
        }

        private static void FireDirectly(Firearm firearm, ReferenceHub target)
        {
            if (firearm.TryGetModule<IHitregModule>(out var hitreg))
            {
                hitreg.Fire(target, new BulletShotEvent(firearm.ItemId));
                return;
            }

            target.playerStats.DealDamage(new FirearmDamageHandler(firearm, 18f, 0.35f)
            {
                Hitbox = HitboxType.Body,
            });
        }
    }
}
