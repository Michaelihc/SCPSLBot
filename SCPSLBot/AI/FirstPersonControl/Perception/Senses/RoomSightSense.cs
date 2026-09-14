using MapGeneration;
using SCPSLBot.AI.FirstPersonControl.Perception.Senses.Sight;
using SCPSLBot.Navigation;
using System;
using System.Collections.Generic;
using Unity.Jobs;
using UnityEngine;

namespace SCPSLBot.AI.FirstPersonControl.Perception.Senses
{
    internal class RoomSightSense : SightSense, ISense
    {
        /// <summary>Entry points into the rooms reachable from the current room through one doorway, connector or elevator.</summary>
        public List<RoomEntry> ForeignRoomEntries { get; } = new();
        public List<RoomIdentifier> ForeignRooms => foreignRooms;
        public RoomIdentifier RoomWithin { get; private set; }

        public event Action<RoomEntry> OnSensedForeignRoomEntry;
        public event Action OnAfterSensedForeignRooms;

        public event Action<RoomIdentifier> OnSensedRoomWithin;

        private readonly FpcBotPlayer _fpcBotPlayer;
        private readonly List<RoomIdentifier> foreignRooms = new();
        private readonly HashSet<RoomIdentifier> foreignRoomsSet = new();
        private RoomIdentifier cachedTopologyRoom;
        private INavigationBackend cachedBackend;
        private int cachedTopologyVersion = -1;

        public RoomSightSense(FpcBotPlayer botPlayer) : base(botPlayer)
        {
            _fpcBotPlayer = botPlayer;
        }

        public override void ProcessSightSensedItems()
        {
            UpdateRoomWithin();
            UpdateForeignRoomEntries();

            foreach (var entry in ForeignRoomEntries)
            {
                OnSensedForeignRoomEntry?.Invoke(entry);
            }
            OnAfterSensedForeignRooms?.Invoke();
        }

        private void UpdateRoomWithin()
        {
            var playerPosition = _fpcBotPlayer.PlayerPosition;

            if (!RoomUtils.TryGetRoom(playerPosition, out var newRoomWithin))
            {
                if (BotLog.Verbose) Debug.LogWarning($"Could not determine room bot currently in");
                return;
            }

            OnSensedRoomWithin?.Invoke(newRoomWithin);
            RoomWithin = newRoomWithin;
        }

        private void UpdateForeignRoomEntries()
        {
            var backend = NavigationSystem.Instance.Backend;
            if (!RoomWithin || backend == null || !backend.RoomHasNavigation(RoomWithin))
            {
                ForeignRoomEntries.Clear();
                foreignRooms.Clear();
                foreignRoomsSet.Clear();
                cachedTopologyRoom = null;
                cachedBackend = null;
                cachedTopologyVersion = -1;
                return;
            }

            // Room-to-room links are static between topology changes. Rebuilding this on every
            // sight tick made every bot scan its room each frame; keep the per-frame sensing event
            // cadence but refresh the cached lists only when the room or the topology changed.
            if (RoomWithin == cachedTopologyRoom
                && ReferenceEquals(backend, cachedBackend)
                && backend.TopologyVersion == cachedTopologyVersion)
            {
                return;
            }

            backend.GetForeignRoomEntries(RoomWithin, ForeignRoomEntries);
            foreignRooms.Clear();
            foreignRoomsSet.Clear();
            foreach (var entry in ForeignRoomEntries)
            {
                if (entry.Room != null && foreignRoomsSet.Add(entry.Room))
                {
                    foreignRooms.Add(entry.Room);
                }
            }

            cachedTopologyRoom = RoomWithin;
            cachedBackend = backend;
            cachedTopologyVersion = backend.TopologyVersion;
        }

        public void ProcessEnter(Collider other)
        {
        }

        public void ProcessExit(Collider other)
        {
        }

        public IEnumerator<JobHandle> ProcessSensibility()
        {
            yield break;
        }
    }
}
