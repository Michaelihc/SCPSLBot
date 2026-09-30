using SCPSLBot.AI.FirstPersonControl.Combat;

namespace SCPSLBot.PolicyTests;

public sealed class HostilityOverrideTests
{
    [Fact]
    public void WithoutAResolverOrVerdictTheNativeRulesDecide()
    {
        Assert.False(HostilityOverride.TryResolve<string>(null, "bot", "target", out _, out var fault));
        Assert.Null(fault);

        Assert.False(HostilityOverride.TryResolve<string>((_, _) => null, "bot", "target", out _, out fault));
        Assert.Null(fault);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AVerdictOverridesTheNativeRules(bool verdict)
    {
        Assert.True(HostilityOverride.TryResolve<string>((_, _) => verdict, "bot", "target", out var hostile, out _));
        Assert.Equal(verdict, hostile);
    }

    [Fact]
    public void TheResolverSeesTheBotFirstAndTheCandidateSecond()
    {
        Func<string, string, bool?> alliedWithScientists = (bot, candidate) =>
            bot == "mtf" && candidate == "scientist" ? false : null;

        Assert.True(HostilityOverride.TryResolve(alliedWithScientists, "mtf", "scientist", out var hostile, out _));
        Assert.False(hostile);
        Assert.False(HostilityOverride.TryResolve(alliedWithScientists, "scientist", "mtf", out _, out _));
    }

    [Fact]
    public void AFaultingResolverFallsBackToTheNativeRulesAndReportsTheFault()
    {
        var failure = new InvalidOperationException("resolver bug");
        Assert.False(HostilityOverride.TryResolve<string>((_, _) => throw failure, "bot", "target", out var hostile, out var fault));
        Assert.False(hostile);
        Assert.Same(failure, fault);
    }
}
