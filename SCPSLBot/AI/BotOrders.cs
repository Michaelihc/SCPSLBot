using MapGeneration;
using PlayerRoles;
using SCPSLBot.AI.FirstPersonControl.Objectives;
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
        public string LastBlocker { get; internal set; }
    }

    public sealed class BotObjectiveStatus
    {
        public ReferenceHub Bot { get; internal set; }
        public Vector3 Point { get; internal set; }
        public float EngageRadius { get; internal set; }
        /// <summary>The navigable point the bot walks to: <see cref="Point"/>, or the nearest navmesh point when it is off the mesh.</summary>
        public Vector3 Goal { get; internal set; }
        /// <summary>False until navigation is ready for the current map.</summary>
        public bool GoalResolved { get; internal set; }
        public bool GoalIsNearestPoint { get; internal set; }
        public BotObjectivePhase Phase { get; internal set; }
        /// <summary>Holding at the end of the reachable path because the goal itself cannot be reached.</summary>
        public bool AtNearestReachable { get; internal set; }
        public bool HasPath { get; internal set; }
        public float DistanceRemaining { get; internal set; }
        public float ElapsedSeconds { get; internal set; }
        public int StallCount { get; internal set; }
        public int Engagements { get; internal set; }
        public string Room { get; internal set; }
    }

    /// <summary>
    /// Public per-bot movement facade. Orders only supply native FPC input; they never set position.
    /// </summary>
    public static class BotOrders
    {
        public static ReferenceHub SpawnBot(string nickname, RoleTypeId role)
            => BotManager.Instance.AddBotPlayer(nickname, role);

        /// <summary>Walks to a world position; runtime navigation accepts floor or native standing-root coordinates.</summary>
        public static bool MoveTo(ReferenceHub hub, Vector3 worldPosition)
            => BotManager.Instance.IssueMoveOrder(hub, worldPosition, BotOrderKind.MoveTo, $"world:{Format(worldPosition)}");

        public static bool MoveToRoom(ReferenceHub hub, RoomName roomName)
            => BotManager.Instance.IssueMoveToRoomOrder(hub, roomName);

        public static bool Stop(ReferenceHub hub)
            => BotManager.Instance.StopOrder(hub, "requested");

        /// <summary>Drops any order or objective so the bot returns to its normal AI.</summary>
        public static bool Release(ReferenceHub hub)
            => BotManager.Instance.ReleaseBot(hub, "requested");

        /// <summary>
        /// Walks to <paramref name="point"/> and holds there, fighting only hostiles in line of sight
        /// within <paramref name="engageRadius"/> meters of the bot (0 never fights). An off-mesh point
        /// resolves to the nearest navmesh point; an unreachable one holds at the end of the reachable
        /// path and is retried. Replaces any order; lasts until <see cref="Release"/>, another order or
        /// objective, removal or round restart, including across role changes.
        /// </summary>
        public static bool SetObjective(ReferenceHub hub, Vector3 point, float engageRadius)
            => BotManager.Instance.SetObjective(hub, point, engageRadius);

        public static bool TryGetObjective(ReferenceHub hub, out BotObjectiveStatus status)
            => BotManager.Instance.TryGetObjectiveStatus(hub, out status);

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
        public string LastBlocker;
        public int LastProgressStamp = int.MinValue;
        public Vector3 LastProgressPosition;
        public float BestRemaining = float.PositiveInfinity;
    }

    internal sealed class BotObjectiveState
    {
        public BotObjectiveState(Vector3 point, float engageRadius, float now)
        {
            Point = point;
            EngageRadius = engageRadius;
            IssuedAt = now;
            Goal = point;
            Policy = new BotObjectivePolicy(now);
        }

        public readonly Vector3 Point;
        public readonly float EngageRadius;
        public readonly float IssuedAt;
        public readonly BotObjectivePolicy Policy;
        public Vector3 Goal;
        public bool GoalResolved;
        public bool GoalIsNearestPoint;
        public int ResolvedTopologyVersion;
        public bool HasPath;
        public float DistanceRemaining = float.PositiveInfinity;
        public bool ArrivalLogged;
        public bool OffMeshLogged;
    }
}
