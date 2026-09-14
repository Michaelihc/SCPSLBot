using System;

namespace SCPSLBot.Navigation.Policy
{
    /// <summary>
    /// Funnel corners are portal endpoints, which sit on walls and door frames. A capsule can never
    /// reach such a point, so steering at it wedges the bot into the corner. This insets the corner
    /// along the portal by the agent radius plus a margin (pure 2D math, unit-tested).
    /// </summary>
    internal static class PortalCornerPolicy
    {
        /// <summary>
        /// Returns the point on the portal (from -> to) that is <paramref name="inset"/> away from
        /// <paramref name="cornerX"/>/<paramref name="cornerZ"/> toward the other endpoint. Portals
        /// shorter than twice the inset collapse to their midpoint.
        /// </summary>
        public static (float X, float Z) InsetCorner(float cornerX, float cornerZ, float otherX, float otherZ, float inset)
        {
            var dx = otherX - cornerX;
            var dz = otherZ - cornerZ;
            var length = (float)Math.Sqrt(dx * dx + dz * dz);
            if (length <= 1e-5f)
            {
                return (cornerX, cornerZ);
            }

            var distance = Math.Min(Math.Max(inset, 0f), length * 0.5f);
            return (cornerX + dx / length * distance, cornerZ + dz / length * distance);
        }

        /// <summary>Distance a corner should be inset for a capsule of the given radius.</summary>
        public static float InsetFor(float agentRadius, float margin = 0.1f)
            => Math.Max(0f, agentRadius) + Math.Max(0f, margin);
    }
}
