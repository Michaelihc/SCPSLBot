using UnityEngine;

namespace SCPSLBot
{
    // Gate for high-frequency bot diagnostics. Off by default so per-tick AI / belief / navigation
    // logging neither spams the dedicated-server console nor allocates format strings every frame.
    // Toggle at runtime (e.g. from a console command) when debugging bot behavior. Always guard the
    // call site with `if (BotLog.Verbose)` so the interpolated message is not built when disabled.
    internal static class BotLog
    {
        public static bool Verbose = false;

        public static void Trace(string message)
        {
            if (Verbose)
            {
                Debug.Log(message);
            }
        }

        public static void TraceWarning(string message)
        {
            if (Verbose)
            {
                Debug.LogWarning(message);
            }
        }
    }
}
