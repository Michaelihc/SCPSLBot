using SCPSLBot.Warmup.Policy;

namespace SCPSLBot.PolicyTests;

public sealed class WarmupSpectatorRespawnPolicyTests
{
    [Fact]
    public void PlayableRoleEnteringSpectatorUsesDeathDelay()
    {
        SpectatorRespawnSource source = WarmupSpectatorRespawnPolicy.ClassifyTransition(
            currentIsSpectator: true,
            previousWasSpectator: false,
            previousWasPlayable: true);

        Assert.Equal(SpectatorRespawnSource.Death, source);
        Assert.Equal(1.2f, WarmupSpectatorRespawnPolicy.DelaySeconds(source, 1200, 5000));
    }

    [Fact]
    public void NewlyObservedSpectatorUsesRecoveryDelay()
    {
        SpectatorRespawnSource source = WarmupSpectatorRespawnPolicy.ClassifyTransition(
            currentIsSpectator: true,
            previousWasSpectator: false,
            previousWasPlayable: false);

        Assert.Equal(SpectatorRespawnSource.JoinOrRecovery, source);
        Assert.Equal(5f, WarmupSpectatorRespawnPolicy.DelaySeconds(source, 1200, 5000));
    }

    [Fact]
    public void RemainingSpectatorDoesNotCreateAReplacementSchedule()
    {
        Assert.Equal(
            SpectatorRespawnSource.None,
            WarmupSpectatorRespawnPolicy.ClassifyTransition(
                currentIsSpectator: true,
                previousWasSpectator: true,
                previousWasPlayable: false));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void OnlyRealExactSpectatorsAreEligible(bool isRealPlayer, bool isExactSpectator)
    {
        Assert.False(WarmupSpectatorRespawnPolicy.IsEligiblePlayerState(isRealPlayer, isExactSpectator));
    }

    [Fact]
    public void RealExactSpectatorIsEligible()
    {
        Assert.True(WarmupSpectatorRespawnPolicy.IsEligiblePlayerState(
            isRealPlayer: true,
            isExactSpectator: true));
    }
}
