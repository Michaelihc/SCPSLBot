using MapGeneration;
using SCPSLBot.AI.FirstPersonControl.Mind.Spacial;
using System.Collections.Generic;
using UnityEngine;

namespace SCPSLBot.AI.FirstPersonControl.Mind.Room.Beliefs
{
    /// <summary>Entry points into <see cref="Zone"/> from <see cref="FromZone"/>, taken from the map's zone borders.</summary>
    internal class ZoneEnterLocation : Location
    {
        private const float RefreshSeconds = 2f;

        public FacilityZone Zone { get; }
        public FacilityZone FromZone { get; }

        private readonly List<ZoneBoundary> boundaries = new();
        private readonly List<Vector3> entries = new();
        private float nextRefreshAt;

        public ZoneEnterLocation(FacilityZone zone, FacilityZone fromZone)
        {
            Zone = zone;
            FromZone = fromZone;
        }

        /// <summary>Cheap and throttled; called by the facility access tracker every sensing pass.</summary>
        public void Refresh()
        {
            var now = Time.time;
            if (now < nextRefreshAt)
            {
                return;
            }

            nextRefreshAt = now + RefreshSeconds;
            ZoneBoundaries.Get(FromZone, Zone, boundaries);
            entries.Clear();
            foreach (var boundary in boundaries)
            {
                entries.Add(boundary.Entry);
            }

            SetPositions(entries);
        }

        public override string ToString()
        {
            return $"{nameof(ZoneEnterLocation)}({Zone} from {FromZone}): {Positions.Count}";
        }
    }
}
