using SCPSLBot.Navigation.Policy;

namespace SCPSLBot.PolicyTests;

public sealed class ZoneReachPolicyTests
{
    private const int Lcz = 1 << 1;
    private const int Hcz = 1 << 2;
    private const int Ez = 1 << 3;
    private const int Surface = 1 << 5;

    [Fact]
    public void FollowsOpenLinksTransitively()
    {
        var links = new[] { (Lcz, Hcz), (Hcz, Ez), (Ez, Surface) };
        Assert.Equal(Lcz | Hcz | Ez | Surface, ZoneReachPolicy.Reachable(Lcz, links));
    }

    [Fact]
    public void LockedSurfaceElevatorsKeepSurfaceOut()
    {
        var links = new[] { (Lcz, Hcz), (Hcz, Ez) };
        Assert.Equal(Lcz | Hcz | Ez, ZoneReachPolicy.Reachable(Lcz, links));
    }

    [Fact]
    public void LockedLightElevatorsKeepBotInLightContainment()
    {
        var links = new[] { (Hcz, Ez) };
        Assert.Equal(Lcz, ZoneReachPolicy.Reachable(Lcz, links));
    }

    [Fact]
    public void LinksWorkInBothDirections()
    {
        var links = new[] { (Ez, Surface), (Hcz, Ez) };
        Assert.Equal(Hcz | Ez | Surface, ZoneReachPolicy.Reachable(Surface, links));
    }
}
