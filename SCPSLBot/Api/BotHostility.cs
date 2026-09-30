using LabLogger = LabApi.Features.Console.Logger;
using SCPSLBot.AI.FirstPersonControl.Combat;
using System;
using UnityEngine;

namespace SCPSLBot.Api
{
    /// <summary>
    /// Lets a companion plugin decide which players SCPSLBot bots treat as enemies, for example
    /// allied MTF and CI or a custom side. The resolver replaces only the native team comparison:
    /// the bot itself, dead and spectating players are never targets, and Standard warmup arena
    /// separation still applies.
    /// </summary>
    public static class BotHostility
    {
        private const float FaultLogIntervalSeconds = 10f;
        private static float nextFaultLogAt = float.NegativeInfinity;

        /// <summary>
        /// Returns true (hostile), false (not hostile) or null (native team rules) for a bot and a
        /// candidate target. Runs on the main thread several times per bot per frame, so keep it cheap
        /// and allocation-free. One owner at a time: set it when the owning plugin enables and null it
        /// when it disables. A throwing resolver falls back to the native rules.
        /// </summary>
        public static Func<ReferenceHub, ReferenceHub, bool?> Resolver { get; set; }

        internal static bool TryResolve(ReferenceHub bot, ReferenceHub candidate, out bool hostile)
        {
            if (HostilityOverride.TryResolve(Resolver, bot, candidate, out hostile, out var fault))
            {
                return true;
            }

            if (fault != null && Time.time >= nextFaultLogAt)
            {
                nextFaultLogAt = Time.time + FaultLogIntervalSeconds;
                LabLogger.Warn($"[SCPSLBot] HOSTILITY_RESOLVER_FAULT {fault.GetType().Name}: {fault.Message}; using native team rules");
            }

            return false;
        }
    }
}
