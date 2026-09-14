using SCPSLBot.Warmup.Controls;

namespace SCPSLBot.PolicyTests;

public sealed class ActionRateLimiterTests
{
    [Fact]
    public void AlternatingSequentialActionsRemainBoundedPerFullUserId()
    {
        var clock = new FakeClock();
        var limiter = new PerUserActionRateLimiter(clock);

        Assert.True(limiter.TryAcquire("player@steam", 1000, out _));
        clock.Timestamp = 1;
        Assert.False(limiter.TryAcquire("player@steam", 1000, out double remaining));
        Assert.InRange(remaining, 0.998d, 1d);

        clock.Timestamp = 1000;
        Assert.True(limiter.TryAcquire("player@steam", 1000, out _));
        Assert.True(limiter.TryAcquire("other@steam", 1000, out _));
    }

    [Fact]
    public void ForgetAllowsAReauthenticatedSessionToStartCleanly()
    {
        var clock = new FakeClock();
        var limiter = new PerUserActionRateLimiter(clock);

        Assert.True(limiter.TryAcquire("player@steam", 1000, out _));
        limiter.Forget("player@steam");
        Assert.True(limiter.TryAcquire("player@steam", 1000, out _));
    }

    private sealed class FakeClock : IMonotonicClock
    {
        public long Timestamp { get; set; }
        public long Frequency => 1000;
    }
}
