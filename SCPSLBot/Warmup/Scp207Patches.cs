using CustomPlayerEffects;
using HarmonyLib;

namespace SCPSLBot.Warmup
{
    [HarmonyPatch(typeof(Scp207), "OnTick")]
    internal static class Scp207Patches
    {
        [HarmonyPrefix]
        private static bool SkipHealthDrainInStandardWarmup()
        {
            var config = LabApiPlugin.Instance?.Config;
            return !WarmupManager.Instance.IsStandardWarmup
                   || config == null
                   || !config.DisableScp207HealthDrainInWarmup;
        }
    }
}
