using MapGeneration;
using PlayerRoles;
using System;
using UnityEngine;

namespace SCPSLBot.AI
{
    public enum BotOrderKind
    {
        None,
        MoveTo,
        MoveToRoom,
        Completed,
        Stopped,
        FailedOffMesh,
        FailedNoPath,
    }

    public sealed class BotOrderStatus
    {
        public ReferenceHub Bot { get; internal set; }
        public BotOrderKind CurrentOrder { get; internal set; }
        public string Description { get; internal set; }
        public Vector3 Goal { get; internal set; }
        public bool IsActive { get; internal set; }
        public bool HasPath { get; internal set; }
        public float DistanceRemaining { get; internal set; }
        public DateTime LastProgressUtc { get; internal set; }
        public float SecondsSinceProgress { get; internal set; }
        public float ElapsedSeconds { get; internal set; }
        public int StallCount { get; internal set; }
        public int DoorsTraversed { get; internal set; }
        public float MaxTickDistance { get; internal set; }
        public bool TeleportDetected { get; internal set; }
        public int GroundProbeMisses { get; internal set; }
        public float MaxGroundDistance { get; internal set; }
        public string FailureReason { get; internal set; }
        public string Room { get; internal set; }
    }

    /// <summary>
    /// Public per-bot movement facade. Orders only supply native FPC input; they never set position.
    /// </summary>
    public static class BotOrders
    {
        public static ReferenceHub SpawnBot(string nickname, RoleTypeId role)
            => BotManager.Instance.AddBotPlayer(nickname, role);

        public static bool MoveTo(ReferenceHub hub, Vector3 worldPosition)
            => BotManager.Instance.IssueMoveOrder(hub, worldPosition, BotOrderKind.MoveTo, $"world:{Format(worldPosition)}");

        public static bool MoveToRoom(ReferenceHub hub, RoomName roomName)
            => BotManager.Instance.IssueMoveToRoomOrder(hub, roomName);

        public static bool Stop(ReferenceHub hub)
            => BotManager.Instance.StopOrder(hub, "requested");

        public static bool TryGetStatus(ReferenceHub hub, out BotOrderStatus status)
            => BotManager.Instance.TryGetOrderStatus(hub, out status);

        public static bool DespawnBot(ReferenceHub hub)
            => BotManager.Instance.DespawnBot(hub);

        private static string Format(Vector3 value)
            => $"({value.x:F2},{value.y:F2},{value.z:F2})";
    }

    internal sealed class BotOrderState
    {
        public BotOrderKind Kind;
        public string Description;
        public Vector3 Goal;
        public bool Active;
        public bool HasPath;
        public float DistanceRemaining;
        public float IssuedAt;
        public float LastProgressAt;
        public DateTime LastProgressUtc;
        public Vector3 LastPosition;
        public Vector3 LastWaypoint;
        public float LastWaypointDistance;
        public float NextBreadcrumbAt;
        public int StallCount;
        public int DoorsTraversed;
        public float MaxTickDistance;
        public bool TeleportDetected;
        public int GroundProbeMisses;
        public float MaxGroundDistance;
        public string FailureReason;
        public RoomIdentifier LastRoom;
        public bool SampledPosition;
    }
}
