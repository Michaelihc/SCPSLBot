using PlayerRoles;

namespace SCPSLBot
{
    /// <summary>Whether a role change shows the native role intro screen (15.0 <c>RoleSpawnFlags.ShowIntroScreen</c>).</summary>
    public enum IntroIntent
    {
        /// <summary>A plain spawn with no presentation of its own: the native role intro should show.</summary>
        Native,

        /// <summary>The spawn has its own presentation or is a swap, disguise, event role or bookkeeping change: no native intro.</summary>
        Hidden,
    }

    /// <summary>Single seam where every role change records its intro intent in the spawn flags.</summary>
    public static class RoleIntro
    {
        public static RoleSpawnFlags Flags(RoleSpawnFlags flags, IntroIntent intent)
        {
            // TODO(port-15): add RoleSpawnFlags.ShowIntroScreen for IntroIntent.Native and clear it for IntroIntent.Hidden.
            return flags;
        }
    }
}
