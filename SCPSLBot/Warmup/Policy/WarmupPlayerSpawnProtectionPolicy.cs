namespace SCPSLBot.Warmup.Policy;

internal enum WarmupPlayerSpawnProtectionAction
{
    None,
    ClearNativeProtection,
    RetainDeathRespawnProtection,
}

internal static class WarmupPlayerSpawnProtectionPolicy
{
    public static WarmupPlayerSpawnProtectionAction Evaluate(
        bool isStandardWarmup,
        bool isRealPlayer,
        bool isPlayableRole,
        bool hasPendingDeathRespawn)
    {
        if (!isRealPlayer || !isPlayableRole)
        {
            return WarmupPlayerSpawnProtectionAction.None;
        }

        if (hasPendingDeathRespawn)
        {
            return WarmupPlayerSpawnProtectionAction.RetainDeathRespawnProtection;
        }

        return isStandardWarmup
            ? WarmupPlayerSpawnProtectionAction.ClearNativeProtection
            : WarmupPlayerSpawnProtectionAction.None;
    }
}
