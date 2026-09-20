using SCPSLBot.Navigation.Policy;
using System.Globalization;

namespace SCPSLBot.PolicyTests;

public sealed class CustomNavigationRegionTests
{
    [Fact]
    public void OffRoomDistrictUsesAllSixCoordinatesWithCommandSegmentOffset()
    {
        string[] tokens = { "nav", "rebuild", "200.25", "-4000", "-300", "600", "100", "800", "unused" };
        Assert.True(CustomNavigationRegion.TryParse(new(tokens, 2, 6), out var region, out var error), error);
        Assert.Equal(200.25f, region.X);
        Assert.Equal(-4000f, region.Y);
        Assert.Equal(-300f, region.Z);
        Assert.Equal(600f, region.SizeX);
        Assert.Equal(100f, region.SizeY);
        Assert.Equal(800f, region.SizeZ);
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    [InlineData("1e1000")]
    [InlineData("wrong")]
    public void EveryFieldRejectsNonFiniteOrUnparseableInput(string invalid)
    {
        for (var index = 0; index < 6; index++)
        {
            string[] tokens = { "0", "0", "0", "100", "100", "100" };
            tokens[index] = invalid;
            Assert.False(CustomNavigationRegion.TryParse(new(tokens), out _, out var error));
            Assert.NotEmpty(error);
        }
    }

    [Theory]
    [InlineData(3, "0")]
    [InlineData(4, "-1")]
    [InlineData(5, "0.999")]
    [InlineData(3, "1025")]
    [InlineData(4, "257")]
    [InlineData(5, "1025")]
    public void RejectsEmptyOrOversizedBakes(int field, string invalid)
    {
        string[] tokens = { "0", "0", "0", "100", "100", "100" };
        tokens[field] = invalid;
        Assert.False(CustomNavigationRegion.TryParse(new(tokens), out _, out _));
    }

    [Fact]
    public void WorldLimitIncludesRegionExtentsOnAllAxes()
    {
        for (var axis = 0; axis < 3; axis++)
        foreach (var sign in new[] { -1, 1 })
        {
            string[] tokens = { "0", "0", "0", "100", "100", "100" };
            tokens[axis] = (sign * 19950).ToString(CultureInfo.InvariantCulture);
            Assert.True(CustomNavigationRegion.TryParse(new(tokens), out _, out _));
            tokens[axis] = (sign * 19951).ToString(CultureInfo.InvariantCulture);
            Assert.False(CustomNavigationRegion.TryParse(new(tokens), out _, out _));
        }
    }

    [Fact]
    public void DecimalPointParsingDoesNotDependOnServerCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            Assert.True(CustomNavigationRegion.TryParse(new(new[] { "1.5", "0", "0", "1024", "256", "1024" }), out var region, out _));
            Assert.Equal(1.5f, region.X);
            Assert.False(CustomNavigationRegion.TryParse(new(new[] { "1,5", "0", "0", "100", "100", "100" }), out _, out _));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void RequiresExactArgumentCount()
    {
        Assert.False(CustomNavigationRegion.TryParse(default, out _, out _));
        Assert.False(CustomNavigationRegion.TryParse(new(new[] { "0", "0", "0", "100", "100" }), out _, out _));
        Assert.False(CustomNavigationRegion.TryParse(new(new[] { "0", "0", "0", "100", "100", "100", "extra" }), out _, out _));
    }
}
