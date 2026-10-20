using MapGeneration;
using SCPSLBot.AI.FirstPersonControl.Mind.Spacial;
using SCPSLBot.AI.FirstPersonControl.Perception.Senses;
using SCPSLBot.Infrastructure;
using SCPSLBot.Navigation;
using System.Collections.Generic;
using UnityEngine;
using Random = UnityEngine.Random;

namespace SCPSLBot.AI.FirstPersonControl.Mind.Room.Beliefs
{
    /// <summary>
    /// Entry points of the neighbouring rooms in the bot's zone, least recently visited first
    /// (never visited rooms in random order ahead of visited ones). Rebuilt when the bot enters
    /// another room.
    /// </summary>
    internal class RoomEnterLocation : Location
    {
        private readonly RoomSightSense roomSightSense;
        private readonly Dictionary<RoomIdentifier, float> roomsLastVisitTime = new(ManagedReferenceComparer<RoomIdentifier>.Instance);
        private readonly List<(Vector3 Position, float Order)> candidates = new();
        private readonly List<Vector3> orderedPositions = new();
        private RoomIdentifier prevRoomWithin;
        private int generation = int.MinValue;

        public RoomEnterLocation(RoomSightSense roomSightSense)
        {
            this.roomSightSense = roomSightSense;
            this.roomSightSense.OnAfterSensedForeignRooms += OnAfterSensedForeignRooms;
        }

        private void OnAfterSensedForeignRooms()
        {
            var roomWithin = this.roomSightSense.RoomWithin;
            if (roomWithin == null)
            {
                return;
            }

            var current = Navigation.NavigationSystem.Instance.MapGeneration;
            if (current != generation)
            {
                generation = current;
                roomsLastVisitTime.Clear();
                prevRoomWithin = null;
            }

            if (prevRoomWithin != null && roomWithin != prevRoomWithin)
            {
                roomsLastVisitTime[prevRoomWithin] = Time.time;
            }

            prevRoomWithin = roomWithin;

            candidates.Clear();
            foreach (var entry in this.roomSightSense.ForeignRoomEntries)
            {
                if (entry.Room == null || entry.Room.Zone != roomWithin.Zone)
                {
                    continue;
                }

                if (entry.Room.Name != RoomName.Unnamed && entry.Room.Name == roomWithin.Name)
                {
                    continue;
                }

                var order = roomsLastVisitTime.TryGetValue(entry.Room, out var time) ? time : -Random.Range(0f, 4f);
                var index = candidates.Count;
                while (index > 0 && candidates[index - 1].Order > order)
                {
                    index--;
                }

                candidates.Insert(index, (entry.Position, order));
            }

            orderedPositions.Clear();
            foreach (var candidate in candidates)
            {
                orderedPositions.Add(candidate.Position);
            }

            SetPositions(orderedPositions);
        }

        public override string ToString()
        {
            return $"{nameof(RoomEnterLocation)}s: {Positions.Count}";
        }
    }
}
