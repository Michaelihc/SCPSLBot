using SCPSLBot.Warmup.Policy;

namespace SCPSLBot.PolicyTests;

public sealed class WarmupPlayerSpawnProtectionPolicyTests
{
    [Theory]
    [InlineData(true, true, true, false, (int)WarmupPlayerSpawnProtectionAction.ClearNativeProtection)]
    [InlineData(true, true, true, true, (int)WarmupPlayerSpawnProtectionAction.RetainDeathRespawnProtection)]
    [InlineData(true, true, false, true, (int)WarmupPlayerSpawnProtectionAction.None)]
    [InlineData(true, false, true, false, (int)WarmupPlayerSpawnProtectionAction.None)]
    [InlineData(false, true, true, false, (int)WarmupPlayerSpawnProtectionAction.None)]
    [InlineData(false, true, true, true, (int)WarmupPlayerSpawnProtectionAction.RetainDeathRespawnProtection)]
    public void Evaluate_distinguishes_death_respawns_from_loadout_changes(
        bool isStandardWarmup,
        bool isRealPlayer,
        bool isPlayableRole,
        bool hasPendingDeathRespawn,
        int expected)
    {
        Assert.Equal(
            (WarmupPlayerSpawnProtectionAction)expected,
            WarmupPlayerSpawnProtectionPolicy.Evaluate(
                isStandardWarmup,
                isRealPlayer,
                isPlayableRole,
                hasPendingDeathRespawn));
    }
}
