using Interactables.Interobjects;
using MapGeneration;
using SCPSLBot.Navigation.Mesh;
using System;
using System.Collections.Generic;
using UnityEngine;
using LabLogger = LabApi.Features.Console.Logger;

namespace SCPSLBot.Navigation.Authored
{
    /// <summary>
    /// Ties the authored cell mesh to the live elevators. Landings are derived from each door, so
    /// room placement and rotation do not matter. A room whose landing has no cell at its real
    /// height no longer matches the game and is rebuilt from live collision; every group is then
    /// linked between the cells its bots actually stand on when calling the chamber.
    /// </summary>
    internal static class AuthoredElevatorLinks
    {
        // Native doors face their landing; the chamber docks on the other side.
        private const float LandingDistance = 1.6f;
        private const float LandingProbeStart = 1f;
        private const float LandingProbeDepth = 2.2f;
        private const float NearestLandingCellMeters = 2.5f;

        /// <summary>Rebuilds the forms of rooms with an uncovered landing. Returns the number of forms rebuilt.</summary>
        public static int RebuildOutdatedRooms()
        {
            var landingsByRoom = new Dictionary<RoomIdentifier, List<Vector3>>();
            var outdated = new HashSet<RoomIdentifier>();
            foreach (ElevatorGroup group in Enum.GetValues(typeof(ElevatorGroup)))
            {
                foreach (var door in ElevatorDoor.GetDoorsForGroup(group))
                {
                    if (door == null)
                    {
                        continue;
                    }

                    var landing = GetLanding(door);
                    var standing = landing + Vector3.up * NavigationAgentProfile.RootHeight;
                    if (!RoomUtils.TryGetRoom(standing, out var room) || room == null)
                    {
                        continue;
                    }

                    if (!landingsByRoom.TryGetValue(room, out var landings))
                    {
                        landingsByRoom[room] = landings = new List<Vector3>();
                    }

                    landings.Add(landing);
                    if (NavigationMesh.GetRoomCellWithin(standing, room) == null)
                    {
                        outdated.Add(room);
                    }
                }
            }

            var rebuiltForms = 0;
            foreach (var room in outdated)
            {
                if (NavigationMesh.LocalMeshesByRoom.TryGetValue(room.gameObject, out var mesh)
                    && LiveFloorRoomBuilder.Rebuild(room, mesh, landingsByRoom[room]))
                {
                    rebuiltForms++;
                }
            }

            return rebuiltForms;
        }

        /// <summary>Links both landings of every two-door group, in both directions.</summary>
        public static void Connect(Action<TransformCell, TransformCell> connectCells)
        {
            foreach (ElevatorGroup group in Enum.GetValues(typeof(ElevatorGroup)))
            {
                var doors = ElevatorDoor.GetDoorsForGroup(group);
                if (doors.Count != 2 || doors[0] == null || doors[1] == null)
                {
                    LabLogger.Warn($"[SCPSLBot] NAV_LINK_SKIPPED group={group} reason=doors count={doors.Count}");
                    continue;
                }

                var landingA = GetLanding(doors[0]);
                var landingB = GetLanding(doors[1]);
                if (!TryGetLandingCell(landingA, out var cellA) || !TryGetLandingCell(landingB, out var cellB))
                {
                    LabLogger.Warn($"[SCPSLBot] NAV_LINK_SKIPPED group={group} reason=landing-off-mesh a={Format(landingA)} b={Format(landingB)}");
                    continue;
                }

                connectCells(cellA, cellB);
                connectCells(cellB, cellA);
                NavigationMesh.AddElevatorLink(cellA, cellB, new ElevatorLinkSegment(landingA, landingB, doors[0], doors[1]));
                NavigationMesh.AddElevatorLink(cellB, cellA, new ElevatorLinkSegment(landingB, landingA, doors[1], doors[0]));
                LabLogger.Info($"[SCPSLBot] NAV_LINK group={group} a={Format(landingA)} b={Format(landingB)}");
            }
        }

        private static Vector3 GetLanding(ElevatorDoor door)
        {
            var transform = door.transform;
            var landing = transform.position + Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized * LandingDistance;
            return Physics.Raycast(landing + Vector3.up * LandingProbeStart, Vector3.down, out var hit, LandingProbeDepth, LiveFloorRoomBuilder.ProbeMask, QueryTriggerInteraction.Ignore)
                ? hit.point
                : landing;
        }

        private static bool TryGetLandingCell(Vector3 landing, out TransformCell cell)
        {
            cell = default;
            var standing = landing + Vector3.up * NavigationAgentProfile.RootHeight;
            if (!RoomUtils.TryGetRoom(standing, out var room) || room == null)
            {
                return false;
            }

            var within = NavigationMesh.GetRoomCellWithin(standing, room);
            if (within.HasValue)
            {
                cell = within.Value;
                return true;
            }

            return NavigationMesh.TryGetNearestCell(standing, room, NearestLandingCellMeters, out cell);
        }

        private static string Format(Vector3 value) => $"({value.x:F1},{value.y:F1},{value.z:F1})";
    }
}
