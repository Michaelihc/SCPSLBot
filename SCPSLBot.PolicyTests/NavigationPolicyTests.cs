using SCPSLBot.Navigation.Policy;

namespace SCPSLBot.PolicyTests;

public sealed class NavigationFormsTests
{
    [Theory]
    [InlineData("HCZ_049(Clone)", "HCZ_049")]
    [InlineData("HCZ_049 Christmas(Clone)", "HCZ_049")]
    [InlineData("LCZ_173 Halloween", "LCZ_173")]
    [InlineData("HCZ_Straight Variant(Clone)", "HCZ_Straight Variant")]
    [InlineData("Outside", "Outside")]
    public void NormalizeStripsCloneAndSeasonalSuffixes(string instanceName, string expectedForm)
    {
        Assert.Equal(expectedForm, NavigationForms.Normalize(instanceName));
    }

    [Fact]
    public void NormalizeKeepsNullAndEmpty()
    {
        Assert.Null(NavigationForms.Normalize(null!));
        Assert.Equal(string.Empty, NavigationForms.Normalize(string.Empty));
    }

    [Theory]
    [InlineData("HCZ_049 Christmas(Clone)", true)]
    [InlineData("EZ_PCs Halloween", true)]
    [InlineData("EZ_PCs(Clone)", false)]
    public void DetectsSeasonalVariants(string instanceName, bool expected)
    {
        Assert.Equal(expected, NavigationForms.IsSeasonalVariant(instanceName));
    }
}

public sealed class LateralGapFinderTests
{
    [Fact]
    public void GroupsConsecutiveFreeColumnsIntoGaps()
    {
        // columns at -1.0, -0.5, 0.0, 0.5, 1.0 (step 0.5)
        bool[] free = { true, false, true, true, false };
        var gaps = LateralGapFinder.FindGaps(free, -1f, 0.5f);

        Assert.Equal(2, gaps.Count);
        Assert.Equal(-1.25f, gaps[0].Start, 3);
        Assert.Equal(-0.75f, gaps[0].End, 3);
        Assert.Equal(-0.25f, gaps[1].Start, 3);
        Assert.Equal(0.75f, gaps[1].End, 3);
        Assert.Equal(1f, gaps[1].Width, 3);
    }

    [Fact]
    public void NoFreeColumnsMeansNoGaps()
    {
        Assert.Empty(LateralGapFinder.FindGaps(new[] { false, false }, 0f, 0.1f));
        Assert.Empty(LateralGapFinder.FindGaps(Array.Empty<bool>(), 0f, 0.1f));
    }

    [Fact]
    public void RejectsGapsNarrowerThanTheCapsule()
    {
        var gaps = new[] { new LateralGap(-0.3f, 0.3f) };
        Assert.Null(LateralGapFinder.SelectPassage(gaps, minWidth: 0.8f, comfortableWidth: 1.2f, preferredOffset: 0f));
    }

    [Fact]
    public void PrefersTheGapContainingTheCenterWhenWideEnough()
    {
        // Simple Boxes: boxes on the left, a wide gap starting near the center.
        var gaps = new[] { new LateralGap(-3f, -2.2f), new LateralGap(-0.3f, 2.9f) };
        var selected = LateralGapFinder.SelectPassage(gaps, 0.8f, 1.2f, 0f);

        Assert.NotNull(selected);
        Assert.Equal(-0.3f, selected!.Value.Start, 3);
    }

    [Fact]
    public void PrefersAComfortableGapOverABarelyPassableNearerOne()
    {
        var gaps = new[] { new LateralGap(-0.45f, 0.45f), new LateralGap(1.2f, 3.0f) };
        var selected = LateralGapFinder.SelectPassage(gaps, 0.8f, 1.2f, 0f);

        Assert.NotNull(selected);
        Assert.Equal(1.2f, selected!.Value.Start, 3);
    }

    [Fact]
    public void FallsBackToTheOnlyPassableGapEvenIfNarrow()
    {
        // Boxes Ladder: one 1.1 m slot between two piles.
        var gaps = new[] { new LateralGap(-3f, -2.4f), new LateralGap(-0.4f, 0.7f), new LateralGap(2.2f, 2.9f) };
        var selected = LateralGapFinder.SelectPassage(gaps, 0.8f, 1.2f, 0f);

        Assert.NotNull(selected);
        Assert.Equal(0.15f, selected!.Value.Center, 3);
    }
}

public sealed class PortalCornerPolicyTests
{
    [Fact]
    public void InsetMovesTheCornerAlongThePortal()
    {
        var (x, z) = PortalCornerPolicy.InsetCorner(0f, 0f, 4f, 0f, 0.5f);
        Assert.Equal(0.5f, x, 4);
        Assert.Equal(0f, z, 4);
    }

    [Fact]
    public void ShortPortalsCollapseToTheMidpoint()
    {
        var (x, z) = PortalCornerPolicy.InsetCorner(0f, 0f, 0f, 0.6f, 0.5f);
        Assert.Equal(0f, x, 4);
        Assert.Equal(0.3f, z, 4);
    }

    [Fact]
    public void DegeneratePortalReturnsTheCorner()
    {
        var (x, z) = PortalCornerPolicy.InsetCorner(1f, 2f, 1f, 2f, 0.5f);
        Assert.Equal(1f, x, 4);
        Assert.Equal(2f, z, 4);
    }

    [Fact]
    public void InsetCoversRadiusPlusMargin()
    {
        Assert.Equal(0.48f, PortalCornerPolicy.InsetFor(0.36f, 0.12f), 4);
        Assert.Equal(0.1f, PortalCornerPolicy.InsetFor(-1f, 0.1f), 4);
    }
}

public sealed class PassageCorridorFinderTests
{
    private static LateralGap G(float start, float end) => new(start, end);

    [Fact]
    public void StraightOpeningYieldsThePortalGap()
    {
        var slices = new IReadOnlyList<LateralGap>[]
        {
            new[] { G(-2f, 2f) },
            new[] { G(-2f, 2f) },
            new[] { G(-2f, 2f) },
        };

        var portal = PassageCorridorFinder.SelectCorridorPortal(slices, 1, 0.8f, 0.45f, 0f);
        Assert.NotNull(portal);
        Assert.Equal(0f, portal!.Value.Center, 3);
    }

    [Fact]
    public void SCurveWithoutAnyStraightColumnIsStillACorridor()
    {
        // Angled fences: the free band drifts from the right to the left across the depth.
        var slices = new IReadOnlyList<LateralGap>[]
        {
            new[] { G(0.6f, 2.2f) },
            new[] { G(-0.4f, 1.4f) },
            new[] { G(-1.2f, 0.6f) },
            new[] { G(-2.2f, -0.3f) },
        };

        var portal = PassageCorridorFinder.SelectCorridorPortal(slices, 2, 0.8f, 0.45f, 0f);
        Assert.NotNull(portal);
        Assert.Equal(-1.2f, portal!.Value.Start, 3);
        Assert.Equal(0.6f, portal.Value.End, 3);
    }

    [Fact]
    public void GapsThatDoNotOverlapEnoughDoNotChain()
    {
        var slices = new IReadOnlyList<LateralGap>[]
        {
            new[] { G(-2f, -0.9f) },
            new[] { G(-1f, 0.5f) },
            new[] { G(0.4f, 2f) },
        };

        Assert.Null(PassageCorridorFinder.SelectCorridorPortal(slices, 1, 0.8f, 0.45f, 0f));
    }

    [Fact]
    public void PrefersTheCorridorWithTheWidestBottleneck()
    {
        var slices = new IReadOnlyList<LateralGap>[]
        {
            new[] { G(-3f, -1f), G(0.5f, 3f) },
            new[] { G(-3f, -2.1f), G(0.5f, 3f) },
            new[] { G(-3f, -1f), G(0.5f, 3f) },
        };

        var portal = PassageCorridorFinder.SelectCorridorPortal(slices, 1, 0.8f, 0.45f, 0f);
        Assert.NotNull(portal);
        Assert.Equal(0.5f, portal!.Value.Start, 3);
    }

    [Fact]
    public void SealedSliceMeansNoCorridor()
    {
        var slices = new IReadOnlyList<LateralGap>[]
        {
            new[] { G(-2f, 2f) },
            Array.Empty<LateralGap>(),
            new[] { G(-2f, 2f) },
        };

        Assert.Null(PassageCorridorFinder.SelectCorridorPortal(slices, 1, 0.8f, 0.45f, 0f));
    }
}
