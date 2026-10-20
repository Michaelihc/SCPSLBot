using MapGeneration;
using SCPSLBot.AI.FirstPersonControl.Mind.Spacial;
using SCPSLBot.AI.FirstPersonControl.Perception.Senses;
using UnityEngine;

namespace SCPSLBot.AI.FirstPersonControl.Mind.Escape
{
    /// <summary>
    /// A standing point inside the native escape zone on Surface. Surface is the same every round
    /// and every player knows where the escape is, so the point is resolved as soon as the
    /// surface geometry exists instead of after the bot first sees Surface.
    /// </summary>
    internal class FacilityEscapeLocation : Location
    {
        private const float RetrySeconds = 2f;

        private static readonly int GroundMask = LayerMask.GetMask("Default");
        private static readonly Vector3 roughEscapePosition = global::Escape.DefaultEscapeZone.center + new Vector3(0f, 0f, -10f);

        private float nextAttemptAt;

        public FacilityEscapeLocation(RoomSightSense roomSightSense)
        {
            roomSightSense.OnSensedRoomWithin += OnSensedRoomWithin;
        }

        private void OnSensedRoomWithin(RoomIdentifier roomWithin)
        {
            if (Positions.Count > 0 || Time.time < nextAttemptAt)
            {
                return;
            }

            nextAttemptAt = Time.time + RetrySeconds;
            if (!Physics.Raycast(roughEscapePosition, Vector3.down, out var hit, 10f, GroundMask))
            {
                if (BotLog.Verbose) Debug.LogWarning($"Raycast down from rough escape position produced no hits.");
                return;
            }

            AddPosition(hit.point + Vector3.up * 1f);
        }

        public override string ToString()
        {
            return $"{nameof(FacilityEscapeLocation)}(s): {Positions.Count}";
        }
    }
}
