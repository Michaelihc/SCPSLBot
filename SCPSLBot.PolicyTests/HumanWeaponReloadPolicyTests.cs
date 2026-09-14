using SCPSLBot.AI.FirstPersonControl.Combat;

namespace SCPSLBot.PolicyTests;

public sealed class HumanWeaponReloadPolicyTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void LowLoadedWeaponReloadsWhenReserveExists(int loadedAmmo)
    {
        Assert.True(HumanWeaponReloadPolicy.ShouldAttemptReload(loadedAmmo, reserveAmmo: 1));
    }

    [Fact]
    public void DryWeaponRequestsReloadWithNoReserveSoAmmoPluginsCanReplenishIt()
    {
        Assert.True(HumanWeaponReloadPolicy.ShouldAttemptReload(loadedAmmo: 0, reserveAmmo: 0));
    }

    [Fact]
    public void LastRoundIsPreservedWhenNoReserveExists()
    {
        Assert.False(HumanWeaponReloadPolicy.ShouldAttemptReload(loadedAmmo: 1, reserveAmmo: 0));
    }

    [Fact]
    public void WeaponWithMultipleLoadedRoundsDoesNotReload()
    {
        Assert.False(HumanWeaponReloadPolicy.ShouldAttemptReload(loadedAmmo: 2, reserveAmmo: 10));
    }
}
