using PlayerRoles;
using PlayerRoles.FirstPersonControl;
using UnityEngine;

namespace SCPSLBot.Navigation
{
    /// <summary>
    /// Capsule dimensions used for navigation probing and steering. Read from the native ClassD
    /// role template (the human CharacterController preset) with conservative fallbacks, so the
    /// probes match what the server actually collides.
    /// </summary>
    internal static class NavigationAgentProfile
    {
        private const float FallbackRadius = 0.36f;
        private const float FallbackHeight = 1.8f;
        private const float FallbackStepOffset = 0.22f;

        private static bool resolved;
        private static float radius = FallbackRadius;
        private static float height = FallbackHeight;
        private static float stepOffset = FallbackStepOffset;

        public static float Radius
        {
            get
            {
                Resolve();
                return radius;
            }
        }

        public static float Height
        {
            get
            {
                Resolve();
                return height;
            }
        }

        public static float StepOffset
        {
            get
            {
                Resolve();
                return stepOffset;
            }
        }

        /// <summary>Distance from the floor to the FPC root (capsule center) when standing.</summary>
        public static float RootHeight => Height * 0.5f + 0.08f;

        /// <summary>Lowest obstacle height that blocks walking (anything lower is stepped over).</summary>
        public static float StepClearance => StepOffset + 0.08f;

        /// <summary>Corner inset applied to funnel corners so a capsule can actually reach them.</summary>
        public static float CornerInset => Policy.PortalCornerPolicy.InsetFor(Radius, 0.12f);

        public static void Invalidate()
        {
            resolved = false;
        }

        private static void Resolve()
        {
            if (resolved)
            {
                return;
            }

            try
            {
                if (RoleTypeId.ClassD.TryGetRoleTemplate(out FpcStandardRoleBase template)
                    && template != null
                    && template.FpcModule != null
                    && template.FpcModule.CharacterControllerSettings != null)
                {
                    var preset = template.FpcModule.CharacterControllerSettings;
                    if (preset.Radius > 0.1f && preset.Height > 0.5f)
                    {
                        radius = preset.Radius;
                        height = preset.Height;
                        stepOffset = Mathf.Max(0.05f, preset.StepOffset);
                    }
                }
            }
            catch
            {
                // Fall back to the constants above; probing stays conservative.
            }

            resolved = true;
        }
    }
}
