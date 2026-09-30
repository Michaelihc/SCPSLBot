#nullable enable

using System;

namespace SCPSLBot.AI.FirstPersonControl.Combat
{
    /// <summary>
    /// Precedence of an external hostility verdict over the native team rules: a verdict wins, while
    /// no resolver, a null verdict or a resolver fault falls back. Pure so it is unit-testable.
    /// </summary>
    internal static class HostilityOverride
    {
        public static bool TryResolve<THub>(Func<THub, THub, bool?>? resolver, THub bot, THub candidate,
            out bool hostile, out Exception? fault)
        {
            hostile = false;
            fault = null;
            if (resolver == null)
            {
                return false;
            }

            bool? verdict;
            try
            {
                verdict = resolver(bot, candidate);
            }
            catch (Exception exception)
            {
                fault = exception;
                return false;
            }

            if (!verdict.HasValue)
            {
                return false;
            }

            hostile = verdict.Value;
            return true;
        }
    }
}
