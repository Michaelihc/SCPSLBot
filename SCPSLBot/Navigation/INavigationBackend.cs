using Interactables.Interobjects;
using MapGeneration;
using SCPSLBot.AI.FirstPersonControl;
using System.Collections.Generic;
using UnityEngine;

namespace SCPSLBot.Navigation
{
    /// <summary>A reachable point inside a neighbouring room, just past the shared doorway/connector.</summary>
    internal readonly struct RoomEntry
    {
        public RoomEntry(RoomIdentifier room, Vector3 position)
        {
            Room = room;
            Position = position;
        }

        public RoomIdentifier Room { get; }
        public Vector3 Position { get; }
    }

    /// <summary>
    /// An elevator crossing on a bot's current path. Doors are null for backends that only know
    /// an "edgeless" segment (the authored cell mesh); the elevation belief then falls back to
    /// probing the world around <see cref="Origin"/>.
    /// </summary>
    internal readonly struct ElevatorLinkSegment
    {
        public ElevatorLinkSegment(Vector3 origin, Vector3 destination, ElevatorDoor originDoor, ElevatorDoor destinationDoor)
        {
            Origin = origin;
            Destination = destination;
            OriginDoor = originDoor;
            DestinationDoor = destinationDoor;
        }

        public Vector3 Origin { get; }
        public Vector3 Destination { get; }
        public ElevatorDoor OriginDoor { get; }
        public ElevatorDoor DestinationDoor { get; }
        public bool HasDoors => OriginDoor != null && DestinationDoor != null;
    }

    /// <summary>Per-bot path planner and corner follower. One instance per bot, owned by its FpcBotPlayer.</summary>
    internal interface IBotNavigator
    {
        /// <summary>True when a complete path to the goal exists.</summary>
        bool HasPath { get; }

        /// <summary>True when only a partial path (toward the reachable point nearest the goal) exists.</summary>
        bool HasPartialPath { get; }

        /// <summary>The steering point returned by the last <see cref="GetPositionTowards"/> call.</summary>
        Vector3 CurrentWaypoint { get; }

        /// <summary>The goal of the current plan.</summary>
        Vector3 GoalPosition { get; }

        /// <summary>
        /// Increments whenever the bot genuinely advances along its path (a corner/portal was
        /// passed) or a new goal was planned. Replans that merely swap waypoints do not touch it.
        /// </summary>
        int ProgressStamp { get; }

        /// <summary>Number of nodes (cells or corners) in the current path; diagnostics only.</summary>
        int PathNodeCount { get; }

        /// <summary>World points of the current path, starting at the bot's position when planned.</summary>
        List<Vector3> PointsPath { get; }

        IEnumerable<(Vector3 point, Vector3 nextPoint)> PathSegments { get; }

        Vector3 GetPositionTowards(Vector3 goalPosition);

        /// <summary>Forces the next call to rebuild the path (used by stuck recovery).</summary>
        void ForceReplan();

        /// <summary>
        /// Marks the crossing the bot is currently attempting as blocked for a while so the next
        /// plan prefers another route when one exists, then replans. Returns false when the bot is
        /// not attempting a crossing.
        /// </summary>
        bool ReportBlockedCrossing();

        string DescribeState();

        /// <summary>True when the bot currently stands on (or within recovery distance of) the navigation surface.</summary>
        bool IsOnMesh();

        /// <summary>The first elevator crossing ahead on the current path, if any.</summary>
        bool TryGetElevatorLink(out ElevatorLinkSegment link);

        /// <summary>True when the bot is close enough to <paramref name="point"/> to count as standing there.</summary>
        bool HasReached(Vector3 point);

        /// <summary>One path query: can this bot (with its current keycards) reach <paramref name="goalPosition"/> completely?</summary>
        bool CanReach(Vector3 goalPosition);

        /// <summary>The navmesh area mask this bot currently plans with (all bits set for backends without areas).</summary>
        int AreaMask { get; }
    }

    /// <summary>
    /// Map-wide navigation data shared by every bot: room coverage, roam samples, room-to-room
    /// entry points and off-mesh checks. Implemented by the runtime navmesh and by the authored
    /// cell mesh so behaviors are written once.
    /// </summary>
    internal interface INavigationBackend
    {
        string Name { get; }

        /// <summary>Changes whenever the navigable topology changed (reload, bake, reconcile, editor change).</summary>
        int TopologyVersion { get; }

        IBotNavigator CreateNavigator(FpcBotPlayer botPlayer);

        bool RoomHasNavigation(RoomIdentifier room);

        /// <summary>Reachable points spread over a room's floor(s), for roaming and orders.</summary>
        IReadOnlyList<Vector3> GetRoomSamples(RoomIdentifier room);

        /// <summary>Every room that has navigation this map.</summary>
        IEnumerable<RoomIdentifier> GetNavigableRooms();

        /// <summary>Entry points into the rooms reachable from <paramref name="room"/> through one doorway, connector or elevator.</summary>
        void GetForeignRoomEntries(RoomIdentifier room, List<RoomEntry> results);

        /// <summary>The room sample nearest <paramref name="probe"/> on the same floor.</summary>
        bool TryGetNearestRoomSample(RoomIdentifier room, Vector3 probe, float maxHorizontalDistance, float maxHeightDifference, out Vector3 sample);

        /// <summary>The room sample nearest <paramref name="probe"/> on the same floor that connects to the room's main floor.</summary>
        bool TryGetConnectedRoomSample(RoomIdentifier room, Vector3 probe, float maxHorizontalDistance, float maxHeightDifference, out Vector3 sample);

        /// <summary>The nearest room sample a complete path from <paramref name="origin"/> reaches (orders into a room).</summary>
        bool TryGetReachableRoomSample(RoomIdentifier room, Vector3 origin, int areaMask, out Vector3 goal);

        /// <summary>The room sample nearest <paramref name="probe"/> (same floor, within range) reachable from <paramref name="origin"/> with <paramref name="areaMask"/>.</summary>
        bool TryGetReachableRoomSample(RoomIdentifier room, Vector3 origin, Vector3 probe, float maxHorizontalDistance, float maxHeightDifference, int areaMask, out Vector3 goal);

        bool IsOnMesh(Vector3 position, float maxDistance);

        bool TryGetNearestPoint(Vector3 position, float maxDistance, out Vector3 nearest, out float distance);

        /// <summary>Semicolon-free key=value diagnostics appended to bot_status.</summary>
        string Diagnostics { get; }
    }
}
