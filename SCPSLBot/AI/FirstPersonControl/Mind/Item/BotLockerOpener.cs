using Interactables;
using Interactables.Interobjects.DoorUtils;
using InventorySystem.Items;
using MapGeneration.Distributors;
using PlayerRoles;
using SCPSLBot.AI.FirstPersonControl.Movement;
using UnityEngine;

namespace SCPSLBot.AI.FirstPersonControl.Mind.Item
{
    /// <summary>
    /// Mirrors the server-side check of <see cref="Locker.ServerInteract"/>:
    /// <see cref="LockerChamber.PermissionsPolicy"/> (require-all) evaluated through
    /// <see cref="DoorPermissionsPolicy.CheckPermissions(ReferenceHub, IDoorPermissionRequester, out PermissionUsed)"/>,
    /// where a role permission provider is asked exclusively and a human only counts the held
    /// item. A carried keycard that satisfies the chamber must be equipped first. Allocation-free.
    /// </summary>
    internal static class LockerAccess
    {
        public static DoorAccessKind Evaluate(ReferenceHub hub, LockerChamber chamber, out ItemBase keycard)
        {
            keycard = null;
            if (hub == null || chamber == null)
            {
                return DoorAccessKind.Denied;
            }

            var policy = chamber.PermissionsPolicy;
            if (policy.RequiredPermissions == DoorPermissionFlags.None || (hub.serverRoles != null && hub.serverRoles.BypassMode))
            {
                return DoorAccessKind.Open;
            }

            if (hub.roleManager.CurrentRole is IDoorPermissionProvider roleProvider)
            {
                return policy.CheckPermissions(roleProvider.GetPermissions(chamber)) ? DoorAccessKind.Open : DoorAccessKind.Denied;
            }

            var inventory = hub.inventory;
            if (inventory == null)
            {
                return DoorAccessKind.Denied;
            }

            if (inventory.CurInstance is IDoorPermissionProvider held && policy.CheckPermissions(held.GetPermissions(chamber)))
            {
                return DoorAccessKind.Open;
            }

            foreach (var item in inventory.UserInventory.Items.Values)
            {
                if (item is IDoorPermissionProvider provider
                    && item.AllowEquip
                    && policy.CheckPermissions(provider.GetPermissions(chamber)))
                {
                    keycard = item;
                    return DoorAccessKind.NeedsKeycard;
                }
            }

            return DoorAccessKind.Denied;
        }

        /// <summary>The interactable collider of chamber <paramref name="chamberIndex"/>; its collider id is the chamber index.</summary>
        public static bool TryGetDoor(Locker locker, int chamberIndex, out InteractableCollider door)
        {
            door = null;
            return locker != null
                   && chamberIndex >= 0
                   && InteractableCollider.AllInstances.TryGetValue(locker, out var colliders)
                   && colliders.TryGetValue((byte)chamberIndex, out door)
                   && door != null;
        }
    }

    internal enum LockerOpenProgress
    {
        Working,
        Opened,
        /// <summary>Missing permissions, or the chamber never opened.</summary>
        Denied,
    }

    /// <summary>
    /// Opens one locker chamber from where the bot stands: equips a matching keycard under the
    /// item lease when the chamber needs one, looks at the chamber door and interacts with it
    /// through the native interactable, then restores the previously held item.
    /// </summary>
    internal sealed class BotLockerOpener
    {
        private const float KeycardLeaseSeconds = 6f;
        private const float RetryInteractSeconds = 0.75f;
        private const int MaxInteractAttempts = 4;

        private readonly FpcBotPlayer bot;
        private readonly BotHeldItemSwap swap;
        private LockerChamber chamber;
        private float nextInteractAt;
        private int attempts;

        public BotLockerOpener(FpcBotPlayer bot)
        {
            this.bot = bot;
            swap = new BotHeldItemSwap(bot);
        }

        public LockerOpenProgress Tick(Locker locker, int chamberIndex)
        {
            if (locker == null || chamberIndex < 0 || chamberIndex >= locker.Chambers.Length)
            {
                Finish();
                return LockerOpenProgress.Denied;
            }

            var target = locker.Chambers[chamberIndex];
            if (!ReferenceEquals(target, chamber))
            {
                Finish();
                chamber = target;
            }

            if (target.IsOpen)
            {
                Finish();
                return LockerOpenProgress.Opened;
            }

            var hub = bot.BotHub.PlayerHub;
            var access = LockerAccess.Evaluate(hub, target, out var keycard);
            if (access == DoorAccessKind.Denied || attempts >= MaxInteractAttempts || !LockerAccess.TryGetDoor(locker, chamberIndex, out var door))
            {
                Finish();
                return LockerOpenProgress.Denied;
            }

            if (access == DoorAccessKind.NeedsKeycard && !swap.Equip(keycard, KeycardLeaseSeconds))
            {
                // Another holder has the hands for a moment.
                return LockerOpenProgress.Working;
            }

            bot.Locomotion.Stop();
            var doorCenter = door.TryGetComponent<Collider>(out var collider) ? collider.bounds.center : target.transform.position;
            if (Time.time < nextInteractAt || !target.CanInteract)
            {
                bot.LookToPosition(doorCenter);
                return LockerOpenProgress.Working;
            }

            if (bot.Interact(door))
            {
                attempts++;
                nextInteractAt = Time.time + RetryInteractSeconds;
            }
            else
            {
                bot.LookToPosition(doorCenter);
            }

            return LockerOpenProgress.Working;
        }

        /// <summary>Gives the hands back; call when the caller stops opening (done, gave up, interrupted).</summary>
        public void Finish()
        {
            swap.Restore();
            chamber = null;
            attempts = 0;
            nextInteractAt = 0f;
        }
    }
}
