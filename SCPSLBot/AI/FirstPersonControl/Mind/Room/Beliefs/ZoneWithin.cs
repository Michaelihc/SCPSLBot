using MapGeneration;
using SCPSLBot.AI.FirstPersonControl.Perception.Senses;

using SCPSLBot.Navigation;

namespace SCPSLBot.AI.FirstPersonControl.Mind.Room.Beliefs
{
    internal class ZoneWithin : Belief<FacilityZone?>
    {
        private readonly IBotNavigator navigator;

        public ZoneWithin(RoomSightSense roomSightSense, IBotNavigator navigator)
        {
            this.navigator = navigator;
            roomSightSense.OnSensedRoomWithin += OnSensedRoomWithin;
        }

        private void OnSensedRoomWithin(RoomIdentifier room)
        {
            if (navigator.IsOnMesh())
            {
                Update(room.Zone);
            }
        }

        public FacilityZone? Zone { get; private set; }

        private void Update(FacilityZone? newZoneValue)
        {
            if (newZoneValue != Zone)
            {
                Zone = newZoneValue;
                InvokeOnUpdate();
            }
        }

        public override string ToString()
        {
            return $"{nameof(ZoneWithin)}: {Zone}";
        }
    }
}
