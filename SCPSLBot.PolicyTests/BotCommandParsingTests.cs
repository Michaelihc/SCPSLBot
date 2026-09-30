using SCPSLBot.AI.Commands;

namespace SCPSLBot.PolicyTests;

public sealed class BotCommandParsingTests
{
    [Fact]
    public void ObjectiveForAllBotsCarriesPointAndRadius()
    {
        Assert.True(BotCommandParsing.TryParseOrder(Args("ALL objective 22 290.7 -40.5 25"), out var request, out var error));
        Assert.Equal(string.Empty, error);
        Assert.True(request.AllBots);
        Assert.Equal(BotOrderVerb.Objective, request.Verb);
        Assert.Equal((22f, 290.7f, -40.5f, 25f), (request.X, request.Y, request.Z, request.EngageRadius));
    }

    [Fact]
    public void MoveToHoldAndReleaseTargetOnePlayer()
    {
        Assert.True(BotCommandParsing.TryParseOrder(Args("12 moveto -12 290.7 -43"), out var move, out _));
        Assert.False(move.AllBots);
        Assert.Equal(12, move.PlayerId);
        Assert.Equal(BotOrderVerb.MoveTo, move.Verb);
        Assert.Equal((-12f, 290.7f, -43f), (move.X, move.Y, move.Z));

        Assert.True(BotCommandParsing.TryParseOrder(Args("3 hold"), out var hold, out _));
        Assert.Equal(BotOrderVerb.Hold, hold.Verb);
        Assert.True(BotCommandParsing.TryParseOrder(Args("3 Release"), out var release, out _));
        Assert.Equal(BotOrderVerb.Release, release.Verb);
    }

    [Theory]
    [InlineData("")]
    [InlineData("all")]
    [InlineData("0 hold")]
    [InlineData("-4 hold")]
    [InlineData("bot hold")]
    [InlineData("all jump")]
    [InlineData("all hold now")]
    [InlineData("all moveto 1 2")]
    [InlineData("all moveto 1 2 3 4")]
    [InlineData("all moveto 1 NaN 3")]
    [InlineData("all moveto 1,5 2 3")]
    [InlineData("all objective 1 2 3")]
    [InlineData("all objective 1 2 3 -1")]
    [InlineData("all objective 1 2 3 1001")]
    [InlineData("all objective 1 2 3 Infinity")]
    public void MalformedOrdersAreRejectedWithGuidance(string line)
    {
        Assert.False(BotCommandParsing.TryParseOrder(Args(line), out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void BareAddKeepsTheSingleCappedSpawn()
    {
        Assert.True(BotCommandParsing.TryParseAdd(Args(""), out var count, out var role, out var error));
        Assert.Equal(0, count);
        Assert.Null(role);
        Assert.Equal(string.Empty, error);
    }

    [Theory]
    [InlineData("1", 1, null)]
    [InlineData("20", 20, null)]
    [InlineData("40 NtfSergeant", 40, "NtfSergeant")]
    public void ExplicitCountsUpToTheGuardAreAccepted(string line, int expectedCount, string? expectedRole)
    {
        Assert.True(BotCommandParsing.TryParseAdd(Args(line), out var count, out var role, out _));
        Assert.Equal(expectedCount, count);
        Assert.Equal(expectedRole, role);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("41")]
    [InlineData("-3")]
    [InlineData("many")]
    [InlineData("5 ClassD extra")]
    public void CountsOutsideTheGuardAreRejected(string line)
    {
        Assert.False(BotCommandParsing.TryParseAdd(Args(line), out var count, out _, out var error));
        Assert.Equal(0, count);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    private static string[] Args(string line)
        => line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
}
