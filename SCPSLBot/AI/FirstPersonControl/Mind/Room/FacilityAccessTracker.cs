using MapGeneration;
using SCPSLBot.AI.FirstPersonControl.Mind.Escape;
using SCPSLBot.AI.FirstPersonControl.Mind.Room.Beliefs;
using SCPSLBot.AI.FirstPersonControl.Perception.Senses;
using System.Collections.Generic;
using UnityEngine;

namespace SCPSLBot.AI.FirstPersonControl.Mind.Room
{
    /// <summary>
    /// Keeps the zone-border beliefs current: border entry points, per-border access and the
    /// whole escape route. Refreshed a couple of times per second from the inventory sensing
    /// pass (keycards are what usually changes); each refresh checks a handful of doors.
    /// </summary>
    internal sealed class FacilityAccessTracker
    {
        private const float RefreshSeconds = 0.5f;

        private static readonly FacilityZone[] ZoneChain =
        {
            FacilityZone.LightContainment, FacilityZone.HeavyContainment, FacilityZone.Entrance, FacilityZone.Surface,
        };

        private readonly FpcBotPlayer botPlayer;
        private readonly ZoneWithin zoneWithin;
        private readonly EscapeRouteAccess escapeRoute;
        private readonly List<ZoneAccess> accesses = new();
        private readonly List<ZoneEnterLocation> entries = new();
        private readonly List<ZoneBoundary> boundaries = new();
        private float nextRefreshAt;

        public FacilityAccessTracker(FpcBotPlayer botPlayer, FpcMind mind, ItemsInInventorySense inventorySense)
        {
            this.botPlayer = botPlayer;
            zoneWithin = mind.GetBelief<ZoneWithin>();
            escapeRoute = mind.GetBelief<EscapeRouteAccess>();
            foreach (var access in mind.GetBeliefs<ZoneAccess>())
            {
                accesses.Add(access);
            }

            foreach (var entry in mind.GetBeliefs<ZoneEnterLocation>())
            {
                entries.Add(entry);
            }

            inventorySense.OnAfterSensedItems += Refresh;
        }

        private void Refresh()
        {
            var now = Time.time;
            if (now < nextRefreshAt)
            {
                return;
            }

            nextRefreshAt = now + RefreshSeconds;
            var hub = botPlayer.BotHub.PlayerHub;
            foreach (var entry in entries)
            {
                entry.Refresh();
            }

            foreach (var access in accesses)
            {
                access.Set(CanPass(hub, access.From, access.To));
            }

            escapeRoute.Set(CanReachSurface(hub, zoneWithin.Zone));
        }

        private bool CanPass(ReferenceHub hub, FacilityZone from, FacilityZone to)
        {
            ZoneBoundaries.Get(from, to, boundaries);
            foreach (var boundary in boundaries)
            {
                if (boundary.IsPassable(hub))
                {
                    return true;
                }
            }

            return false;
        }

        private bool CanReachSurface(ReferenceHub hub, FacilityZone? zone)
        {
            if (!zone.HasValue)
            {
                return false;
            }

            var start = System.Array.IndexOf(ZoneChain, zone.Value);
            if (start < 0)
            {
                return false;
            }

            for (var index = start; index < ZoneChain.Length - 1; index++)
            {
                var from = ZoneChain[index];
                var to = ZoneChain[index + 1];
                var known = false;
                foreach (var access in accesses)
                {
                    if (access.From == from && access.To == to)
                    {
                        known = true;
                        if (!access.CanPass)
                        {
                            return false;
                        }

                        break;
                    }
                }

                if (!known && !CanPass(hub, from, to))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
