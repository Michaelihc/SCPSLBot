using CommandSystem.Commands.RemoteAdmin.Doors;
using Interactables.Interobjects.DoorUtils;
using MapGeneration;
using PlayerRoles.FirstPersonControl;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using LabLogger = LabApi.Features.Console.Logger;

namespace SCPSLBot.Warmup
{
    /// <summary>
    /// Rotates HCZ/EZ arena entries across distinct generated rooms. Every position comes from
    /// the native named-door registry and the exact safety resolver used by RA's doortp command.
    /// </summary>
    internal sealed class NativeDoorArenaSpawnSelector
    {
        private readonly List<DoorSpawnCandidate> heavyEntranceCandidates = new();
        private int heavyEntranceCursor;
        private bool candidatesBuilt;

        public void Reset()
        {
            heavyEntranceCandidates.Clear();
            heavyEntranceCursor = 0;
            candidatesBuilt = false;
        }

        public bool TryGetNextHeavyEntranceSpawn(out Vector3 position)
        {
            position = default;
            if (!candidatesBuilt)
            {
                heavyEntranceCandidates.AddRange(BuildHeavyEntranceCandidates());
                candidatesBuilt = heavyEntranceCandidates.Count > 0;
            }

            List<DoorSpawnCandidate> candidates = heavyEntranceCandidates;
            if (candidates.Count == 0)
            {
                return false;
            }

            int index = heavyEntranceCursor % candidates.Count;
            heavyEntranceCursor = (index + 1) % candidates.Count;
            DoorSpawnCandidate selected = candidates[index];
            position = selected.Position;
            LabLogger.Info(
                $"[SCPSLBot] Arena spawn selected: arena=HeavyEntrancePvpve, source=RA-doortp, " +
                $"door={selected.DoorName}, room={selected.Room.Name}, zone={selected.Room.Zone}, " +
                $"coords={selected.Room.MainCoords}, position={selected.Position}, candidates={candidates.Count}.");
            return true;
        }

        private static List<DoorSpawnCandidate> BuildHeavyEntranceCandidates()
        {
            var candidates = new List<DoorSpawnCandidate>();
            var seenRooms = new HashSet<RoomIdentifier>();

            foreach (KeyValuePair<string, DoorNametagExtension> entry in
                     DoorNametagExtension.NamedDoors.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                DoorNametagExtension namedDoor = entry.Value;
                if (namedDoor == null || namedDoor.TargetDoor == null || namedDoor.transform == null)
                {
                    continue;
                }

                try
                {
                    // This is the native RA doortp placement algorithm, including its collision walk.
                    Vector3 candidatePosition = DoorTPCommand.EnsurePositionSafety(namedDoor.transform);
                    if (!IsFinite(candidatePosition)
                        || !RoomUtils.TryGetRoom(candidatePosition, out RoomIdentifier room)
                        || !WarmupArenaService.IsArenaZone(WarmupArena.HeavyEntrancePvpve, room.Zone)
                        || !HasGround(candidatePosition)
                        || !seenRooms.Add(room))
                    {
                        continue;
                    }

                    candidates.Add(new DoorSpawnCandidate(entry.Key, room, candidatePosition));
                }
                catch (Exception ex)
                {
                    LabLogger.Warn($"[SCPSLBot] Ignored invalid RA door teleport target {entry.Key}: {ex.Message}");
                }
            }

            return candidates;
        }

        private static bool IsFinite(Vector3 position) =>
            IsFinite(position.x) && IsFinite(position.y) && IsFinite(position.z);

        private static bool HasGround(Vector3 position) => Physics.Raycast(
            position + Vector3.up * 0.1f,
            Vector3.down,
            3.5f,
            FpcStateProcessor.Mask,
            QueryTriggerInteraction.Ignore);

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        private sealed class DoorSpawnCandidate
        {
            public DoorSpawnCandidate(string doorName, RoomIdentifier room, Vector3 position)
            {
                DoorName = doorName;
                Room = room;
                Position = position;
            }

            public string DoorName { get; }
            public RoomIdentifier Room { get; }
            public Vector3 Position { get; }
        }
    }
}
