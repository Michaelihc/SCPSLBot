using MapGeneration;
using PlayerRoles;
using SCPSLBot.Performance;
using System.Collections.Generic;
using UnityEngine;

namespace SCPSLBot.AI.FirstPersonControl.Combat
{
    /// <summary>
    /// Process-wide, low-rate view of currently targetable players. Every bot reads the same
    /// materialized frame instead of independently walking ReferenceHub.AllHubs every game tick.
    /// </summary>
    internal static class CombatWorldSnapshot
    {
        internal const float RefreshesPerSecond = 8f;

        private static readonly FixedRateRefreshGate RefreshGate = new(RefreshesPerSecond);
        private static readonly List<CombatWorldEntry> Builder = new(64);
        private static CombatWorldEntry[] entries = System.Array.Empty<CombatWorldEntry>();

        public static CombatWorldEntry[] Get(float now)
        {
            if (!RefreshGate.TryAcquire(now))
            {
                return entries;
            }

            Builder.Clear();
            foreach (var hub in ReferenceHub.AllHubs)
            {
                if (!TryCapture(hub, out var entry))
                {
                    continue;
                }

                Builder.Add(entry);
            }

            entries = Builder.ToArray();
            return entries;
        }

        internal static void Reset()
        {
            entries = System.Array.Empty<CombatWorldEntry>();
            Builder.Clear();
            RefreshGate.Reset();
        }

        private static bool TryCapture(ReferenceHub hub, out CombatWorldEntry entry)
        {
            entry = default;
            if (hub == null || hub.roleManager?.CurrentRole == null || !hub.IsAlive())
            {
                return false;
            }

            var role = hub.roleManager.CurrentRole;
            if (role.RoleTypeId is RoleTypeId.None or RoleTypeId.Spectator || role.Team == Team.Dead)
            {
                return false;
            }

            var position = hub.transform.position;
            var isOnSurface = RoomUtils.TryGetRoom(position, out var room) && room.Zone == FacilityZone.Surface;
            entry = new CombatWorldEntry(hub, position, role.RoleTypeId, role.Team, isOnSurface);
            return true;
        }
    }

    internal readonly struct CombatWorldEntry
    {
        public CombatWorldEntry(ReferenceHub hub, Vector3 position, RoleTypeId role, Team team, bool isOnSurface)
        {
            Hub = hub;
            Position = position;
            Role = role;
            Team = team;
            IsOnSurface = isOnSurface;
        }

        public ReferenceHub Hub { get; }
        public Vector3 Position { get; }
        public RoleTypeId Role { get; }
        public Team Team { get; }
        public bool IsOnSurface { get; }
    }
}
