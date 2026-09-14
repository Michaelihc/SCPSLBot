using WarmupShared;

namespace SCPSLBot.PolicyTests;

public sealed class WarmupParticipationPolicyTests
{
    [Theory]
    [InlineData("Tutorial", false)]
    [InlineData("tutorial", false)]
    [InlineData("NtfPrivate", true)]
    [InlineData("FacilityGuard", true)]
    [InlineData("ChaosRifleman", true)]
    [InlineData("Scp173", true)]
    [InlineData("Spectator", true)]
    public void AdminRoleIsOutsideWarmupButSpectatorRecoveryStillParticipates(string role, bool expected)
    {
        Assert.Equal(expected, WarmupParticipationPolicy.IsManagedRole(role));
    }
}
