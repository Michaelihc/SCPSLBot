using Interactables.Interobjects.DoorUtils;
using SCPSLBot.AI.FirstPersonControl.Mind.Item.Actions;

namespace SCPSLBot.AI.FirstPersonControl.Mind.Item.Keycard.Actions
{
    internal class GoToPickupKeycard : GoToPickupItem<KeycardWithPermissions>
    {
        public GoToPickupKeycard(DoorPermissionFlags permissions, FpcBotPlayer botPlayer) : base(new(permissions), botPlayer)
        {
        }
    }
}
