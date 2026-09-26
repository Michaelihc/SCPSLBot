using System;

namespace SCPSLBot.Navigation.Policy
{
    /// <summary>Accepts floor targets and native standing-root targets without relaxing mesh sampling.</summary>
    internal static class OrderGoalPolicy
    {
        public static bool IsOnNavigation(bool runtimeBackend, float capsuleHeight, float capsuleCenterY,
            float skinWidth, Func<float, bool> sampleBelowGoal)
        {
            // Existing floor callers and the authored mesh keep their exact original sample.
            if (sampleBelowGoal(0f)) return true;
            if (!runtimeBackend || !Finite(capsuleHeight) || !Finite(capsuleCenterY) || !Finite(skinWidth)
                || capsuleHeight <= 0f || skinWidth < 0f) return false;

            // Native FPC Position is the controller root. The lower capsule end lies at
            // Center.y - Height/2 relative to that root; SkinWidth separates it from the floor.
            float floorOffset = capsuleHeight * 0.5f - capsuleCenterY + skinWidth;
            return floorOffset > 0f && Finite(floorOffset) && sampleBelowGoal(floorOffset);
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
