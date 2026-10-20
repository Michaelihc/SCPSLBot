using Interactables.Interobjects;
using Interactables.Interobjects.DoorUtils;
using InventorySystem.Items;
using PlayerRoles;

namespace SCPSLBot.AI.FirstPersonControl.Movement
{
    internal enum DoorAccessKind
    {
        /// <summary>The bot can open the door with what it holds now (or the door needs nothing).</summary>
        Open,
        /// <summary>A carried keycard satisfies the door; equip it first.</summary>
        NeedsKeycard,
        /// <summary>Not interactable by players but opens on its own (SCP-914 chamber doors).</summary>
        Wait,
        /// <summary>Locked for this player, missing permissions, or not a player door at all.</summary>
        Denied,
    }

    /// <summary>
    /// Mirrors the server-side checks of <see cref="DoorVariant.ServerInteract"/> so the bot only
    /// walks up to doors it can actually open: lock resolution (<see cref="DoorLockUtils"/>), then
    /// <see cref="DoorPermissionsPolicy.CheckPermissions(ReferenceHub, IDoorPermissionRequester, out PermissionUsed)"/>,
    /// where a role permission provider (every standard SCP: ScpOverride) is asked exclusively and
    /// a human only counts the held item. Allocation-free.
    /// </summary>
    internal static class DoorAccess
    {
        // Fully open by the door's own state metric. IsConsideredOpen alone is not enough: the
        // HCZ bulk gate prefab's considered-open threshold is never exceeded even at exact state 1.
        private const float FullyOpenExactState = 0.95f;

        /// <summary>True when the door no longer blocks the doorway (open, or destroyed).</summary>
        public static bool IsPassable(DoorVariant door)
            => door != null && (door.IsConsideredOpen() || door.GetExactState() >= FullyOpenExactState);

        public static bool IsPlayerDoor(DoorVariant door)
            => door != null && door is not ElevatorDoor && door is not DummyDoor && door is not BasicNonInteractableDoor;

        public static DoorAccessKind Evaluate(ReferenceHub hub, DoorVariant door, out ItemBase keycard)
        {
            keycard = null;
            if (hub == null || door == null || door is ElevatorDoor || door is DummyDoor)
            {
                return DoorAccessKind.Denied;
            }

            if (door is BasicNonInteractableDoor)
            {
                return DoorAccessKind.Wait;
            }

            var bypass = hub.serverRoles != null && hub.serverRoles.BypassMode;
            if (door.ActiveLocks != 0 && !bypass)
            {
                var mode = DoorLockUtils.GetMode(door);
                var resolvable = (mode.HasFlagFast(DoorLockMode.CanOpen) && !door.TargetState)
                                 || (mode.HasFlagFast(DoorLockMode.ScpOverride) && hub.IsSCP());
                if (!resolvable)
                {
                    return DoorAccessKind.Denied;
                }
            }

            var policy = door.RequiredPermissions;
            if (policy.RequiredPermissions == DoorPermissionFlags.None || bypass)
            {
                return DoorAccessKind.Open;
            }

            if (hub.roleManager.CurrentRole is IDoorPermissionProvider roleProvider)
            {
                return policy.CheckPermissions(roleProvider.GetPermissions(door)) ? DoorAccessKind.Open : DoorAccessKind.Denied;
            }

            var inventory = hub.inventory;
            if (inventory == null)
            {
                return DoorAccessKind.Denied;
            }

            if (inventory.CurInstance is IDoorPermissionProvider held && policy.CheckPermissions(held.GetPermissions(door)))
            {
                return DoorAccessKind.Open;
            }

            foreach (var item in inventory.UserInventory.Items.Values)
            {
                if (item is IDoorPermissionProvider provider
                    && item.AllowEquip
                    && policy.CheckPermissions(provider.GetPermissions(door)))
                {
                    keycard = item;
                    return DoorAccessKind.NeedsKeycard;
                }
            }

            return DoorAccessKind.Denied;
        }
    }
}
