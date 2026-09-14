using MapGeneration;
using SCPSLBot.AI.FirstPersonControl.Mind.Spacial;
using SCPSLBot.AI.FirstPersonControl.Perception.Senses;
using System.Linq;
using UnityEngine;

namespace SCPSLBot.AI.FirstPersonControl.Mind.Room.Beliefs
{
    internal class ZoneEnterLocation : Location
    {
        public FacilityZone Zone { get; }
        public FacilityZone FromZone { get; }
        public ZoneEnterLocation(FacilityZone zone, FacilityZone fromZone, RoomSightSense roomSightSense) : this(roomSightSense)
        {
            Zone = zone;
            FromZone = fromZone;
        }

        private readonly RoomSightSense roomSightSense;
        private ZoneEnterLocation(RoomSightSense roomSightSense)
        {
            this.roomSightSense = roomSightSense;
            this.roomSightSense.OnAfterSensedForeignRooms += OnAfterSensedForeignRooms;
        }

        private void OnAfterSensedForeignRooms()
        {
            var roomWithin = this.roomSightSense.RoomWithin;
            if (roomWithin == null || roomWithin.Zone != FromZone)
            {
                return;
            }

            foreach (var entry in this.roomSightSense.ForeignRoomEntries)
            {
                if (entry.Room != null && entry.Room.Zone == Zone && entry.Room.Zone != roomWithin.Zone)
                {
                    AddPosition(entry.Position);
                    return;
                }
            }
        }

        public Vector3? Position => this.Positions.Any() ? this.Positions.First() : null;

        public override string ToString()
        {
            return $"{nameof(ZoneEnterLocation)}({Zone}): {Position}";
        }
    }
}
