using UnityEngine;

namespace SCPSLBot.AI.FirstPersonControl.Perception.Senses.Sight
{
    // SightSense now refreshes every tracked collider's center and prunes destroyed objects on
    // each sensing pass, so movable and static candidates share one stable-ID implementation.
    internal abstract class MoveableWithinSightSense<TComponent> : SightSense<TComponent> where TComponent : Component
    {
        protected MoveableWithinSightSense(FpcBotPlayer botPlayer) : base(botPlayer)
        {
        }
    }
}
