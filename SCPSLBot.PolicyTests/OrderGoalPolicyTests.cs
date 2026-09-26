using SCPSLBot.Navigation.Policy;

namespace SCPSLBot.PolicyTests;

public sealed class OrderGoalPolicyTests
{
    private const float SampleRadius = 0.5f;

    [Theory]
    [InlineData(0f)]
    [InlineData(0.94f)]
    [InlineData(0.98f)]
    public void FloorAndStandingRootTargetsReachTheSameSurface(float targetY)
    {
        Assert.True(OnFloor(runtime: true, targetX: 0, targetY));
    }

    [Theory]
    [InlineData(0.51f, 0f)]
    [InlineData(0.51f, 0.98f)]
    [InlineData(0f, 1.45f)]
    [InlineData(0f, 5f)]
    [InlineData(0f, -0.51f)]
    [InlineData(500f, 0.98f)]
    public void RootConventionDoesNotAcceptAirborneOrOffSurfaceDestinations(float targetX, float targetY)
    {
        Assert.False(OnFloor(runtime: true, targetX, targetY));
    }

    [Fact]
    public void AuthoredBackendNeverTriesTheRuntimeProjection()
    {
        var offsets = new List<float>();
        Assert.False(OrderGoalPolicy.IsOnNavigation(false, 1.8f, 0f, 0.04f,
            offset => { offsets.Add(offset); return offset > 0f; }));
        Assert.Equal(new[] { 0f }, offsets);
        Assert.True(OrderGoalPolicy.IsOnNavigation(false, 1.8f, 0f, 0.04f, _ => true));
    }

    [Fact]
    public void ProjectionUsesTheRequestedActorsCapsuleCenterAndSkin()
    {
        var offsets = new List<float>();
        Assert.True(OrderGoalPolicy.IsOnNavigation(true, 2.4f, 0.2f, 0.08f,
            offset => { offsets.Add(offset); return Math.Abs(offset - 1.08f) < 0.0001f; }));
        Assert.Equal(2, offsets.Count);
        Assert.Equal(0f, offsets[0]);
        Assert.Equal(1.08f, offsets[1], 4);
    }

    [Theory]
    [InlineData(0f, 0f, 0.04f)]
    [InlineData(float.NaN, 0f, 0.04f)]
    [InlineData(1.8f, float.PositiveInfinity, 0.04f)]
    [InlineData(1.8f, 0f, -0.04f)]
    public void InvalidCapsulesCannotAddAnAcceptancePath(float height, float centerY, float skin)
    {
        int samples = 0;
        Assert.False(OrderGoalPolicy.IsOnNavigation(true, height, centerY, skin, _ => ++samples > 1));
        Assert.Equal(1, samples);
    }

    private static bool OnFloor(bool runtime, float targetX, float targetY)
        => OrderGoalPolicy.IsOnNavigation(runtime, 1.8f, 0f, 0.04f,
            offset => Math.Sqrt(targetX * targetX + (targetY - offset) * (targetY - offset)) <= SampleRadius);
}
