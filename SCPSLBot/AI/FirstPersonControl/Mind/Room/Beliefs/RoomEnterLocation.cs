using MapGeneration;
using SCPSLBot.AI.FirstPersonControl.Mind.Spacial;
using SCPSLBot.AI.FirstPersonControl.Perception.Senses;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Random = UnityEngine.Random;

namespace SCPSLBot.AI.FirstPersonControl.Mind.Room.Beliefs
{
    internal class RoomEnterLocation : Location
    {
        private readonly RoomSightSense roomSightSense;

        public RoomEnterLocation(RoomSightSense roomSightSense)
        {
            this.roomSightSense = roomSightSense;
            this.roomSightSense.OnAfterSensedForeignRooms += OnAfterSensedForeignRooms;

            //seed = (int)DateTime.Now.Ticks;
            //Log.Debug($"seed for room selection: {seed}");
        }

        private readonly Dictionary<RoomIdentifier, float> roomsLastVisitTime = new();
        private RoomIdentifier prevRoomWithin;

        //private readonly int seed;

        private readonly static HashSet<RoomName> zoneTransitionRoomNames = new()
        {
            RoomName.LczCheckpointA,
            RoomName.LczCheckpointB,
            RoomName.HczCheckpointA,
            RoomName.HczCheckpointB,
            RoomName.EzGateA,
            RoomName.EzGateB,
            RoomName.Outside,
        };

        private void OnAfterSensedForeignRooms()
        {
            var roomWithin = this.roomSightSense.RoomWithin;

            // Room change check
            if (roomWithin != prevRoomWithin && prevRoomWithin != null)
            {
                roomsLastVisitTime[prevRoomWithin] = Time.time;
            }

            // Reached different room check
            if (roomWithin != prevRoomWithin)
            {
                //var prevRandomState = Random.state;
                //Random.InitState(seed);

                var enteringRooms = this.roomSightSense.ForeignRoomEntries
                    .Where(entry => entry.Room != null && entry.Room.Zone == roomWithin.Zone)
                    .Where(entry => entry.Room.Name == RoomName.Unnamed || entry.Room.Name != roomWithin.Name)
                    .OrderBy(entry => roomsLastVisitTime.TryGetValue(entry.Room, out var time) ? time : -Random.Range(0f, 4f));

                SetPositions(enteringRooms.Select(entry => entry.Position));

                //Random.state = prevRandomState;
            }

            prevRoomWithin = roomWithin;
        }

        public override string ToString()
        {
            return $"{nameof(RoomEnterLocation)}s: {Positions.Count}";
        }

        internal float GetLastVisitTime(Vector3 vector3)
        {
            throw new NotImplementedException();
        }
    }
}
