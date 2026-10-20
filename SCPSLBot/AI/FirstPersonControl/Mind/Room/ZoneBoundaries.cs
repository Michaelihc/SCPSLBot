using Interactables.Interobjects.DoorUtils;
using MapGeneration;
using SCPSLBot.AI.FirstPersonControl.Movement;
using SCPSLBot.Navigation;
using System.Collections.Generic;
using UnityEngine;

namespace SCPSLBot.AI.FirstPersonControl.Mind.Room
{
    /// <summary>One way across a zone border: the entry point just inside the next zone and the keycard doors guarding it.</summary>
    internal sealed class ZoneBoundary
    {
        public ZoneBoundary(FacilityZone from, FacilityZone to, Vector3 entry, DoorVariant[] gates)
        {
            From = from;
            To = to;
            Entry = entry;
            Gates = gates;
        }

        public FacilityZone From { get; }
        public FacilityZone To { get; }
        public Vector3 Entry { get; }

        /// <summary>Player doors with permissions in the two rooms the border joins (checkpoints, gates).</summary>
        public DoorVariant[] Gates { get; }

        /// <summary>
        /// True when <paramref name="hub"/> gets through every gate now: it can open it (native
        /// lock and permission rules, carried keycards included), or the gate is destroyed or
        /// held open by a lock (decontamination, warhead).
        /// </summary>
        public bool IsPassable(ReferenceHub hub)
        {
            foreach (var gate in Gates)
            {
                if (gate == null)
                {
                    continue;
                }

                if (gate is IDamageableDoor { IsDestroyed: true })
                {
                    continue;
                }

                if (gate.ActiveLocks != 0 && gate.IsConsideredOpen())
                {
                    continue;
                }

                var access = DoorAccess.Evaluate(hub, gate, out _);
                if (access is not (DoorAccessKind.Open or DoorAccessKind.NeedsKeycard))
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>
    /// Map knowledge every player has: where the zones meet. Built once per map (and topology)
    /// from the navigation backend's room links and shared by every bot, so the objective no
    /// longer has to discover the checkpoints and gates by wandering first.
    /// </summary>
    internal static class ZoneBoundaries
    {
        private static readonly List<ZoneBoundary> Boundaries = new();
        private static readonly List<RoomEntry> EntryBuffer = new();
        private static readonly List<DoorVariant> GateBuffer = new();
        private static int builtGeneration = int.MinValue;
        private static int builtTopology = int.MinValue;
        private static INavigationBackend builtBackend;

        public static IReadOnlyList<ZoneBoundary> All
        {
            get
            {
                EnsureBuilt();
                return Boundaries;
            }
        }

        /// <summary>Copies the borders from <paramref name="from"/> into <paramref name="to"/> into <paramref name="results"/>.</summary>
        public static void Get(FacilityZone from, FacilityZone to, List<ZoneBoundary> results)
        {
            results.Clear();
            foreach (var boundary in All)
            {
                if (boundary.From == from && boundary.To == to)
                {
                    results.Add(boundary);
                }
            }
        }

        private static void EnsureBuilt()
        {
            var navigation = NavigationSystem.Instance;
            var backend = navigation.Backend;
            if (backend == null)
            {
                Boundaries.Clear();
                builtBackend = null;
                return;
            }

            if (builtGeneration == navigation.MapGeneration
                && builtTopology == backend.TopologyVersion
                && ReferenceEquals(builtBackend, backend))
            {
                return;
            }

            builtGeneration = navigation.MapGeneration;
            builtTopology = backend.TopologyVersion;
            builtBackend = backend;
            Boundaries.Clear();

            foreach (var room in backend.GetNavigableRooms())
            {
                if (room == null || !IsFacilityZone(room.Zone))
                {
                    continue;
                }

                backend.GetForeignRoomEntries(room, EntryBuffer);
                foreach (var entry in EntryBuffer)
                {
                    if (entry.Room == null || entry.Room.Zone == room.Zone || !IsFacilityZone(entry.Room.Zone))
                    {
                        continue;
                    }

                    GateBuffer.Clear();
                    CollectGates(room);
                    CollectGates(entry.Room);
                    Boundaries.Add(new ZoneBoundary(room.Zone, entry.Room.Zone, entry.Position, GateBuffer.ToArray()));
                }
            }

            EntryBuffer.Clear();
            GateBuffer.Clear();
        }

        private static void CollectGates(RoomIdentifier room)
        {
            // Surface is a single room holding every surface door (escape, nuke, armory); the
            // gate itself belongs to the Entrance-side gate room.
            if (room.Zone == FacilityZone.Surface || !DoorVariant.DoorsByRoom.TryGetValue(room, out var doors))
            {
                return;
            }

            foreach (var door in doors)
            {
                if (door != null
                    && DoorAccess.IsPlayerDoor(door)
                    && door.RequiredPermissions.RequiredPermissions != DoorPermissionFlags.None
                    && !GateBuffer.Contains(door))
                {
                    GateBuffer.Add(door);
                }
            }
        }

        private static bool IsFacilityZone(FacilityZone zone)
        {
            return zone is FacilityZone.LightContainment or FacilityZone.HeavyContainment or FacilityZone.Entrance or FacilityZone.Surface;
        }
    }
}
