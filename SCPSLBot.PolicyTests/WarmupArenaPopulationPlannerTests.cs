using SCPSLBot.Warmup.Controls;

namespace SCPSLBot.PolicyTests;

public sealed class WarmupArenaPopulationPlannerTests
{
    [Theory]
    [InlineData("ChaosConscript")]
    [InlineData("ChaosRifleman")]
    [InlineData("ChaosMarauder")]
    [InlineData("ChaosRepressor")]
    public void SurfaceChaosBotsUseTheirExactNativeCiSpawn(string exactRoleId)
    {
        Assert.Equal(
            exactRoleId,
            WarmupBotSpawnAnchorPolicy.ResolveAnchorRoleId("surface", exactRoleId));
        Assert.True(WarmupBotSpawnAnchorPolicy.UsesExactNativeRoleSpawn("surface", exactRoleId));
    }

    [Fact]
    public void PlayerAndFacilityAnchorsRemainUnchanged()
    {
        Assert.Equal("NtfPrivate", WarmupBotSpawnAnchorPolicy.ResolveAnchorRoleId("surface", "NtfPrivate"));
        Assert.Equal("Scp939", WarmupBotSpawnAnchorPolicy.ResolveAnchorRoleId("pvpve", "ChaosRifleman"));
        Assert.Equal("ClassD", WarmupBotSpawnAnchorPolicy.ResolveAnchorRoleId("lcz", "Scp173"));
        Assert.True(WarmupBotSpawnAnchorPolicy.UsesExactNativeRoleSpawn("surface", "NtfPrivate"));
        Assert.False(WarmupBotSpawnAnchorPolicy.UsesExactNativeRoleSpawn("pvpve", "ChaosRifleman"));
        Assert.False(WarmupBotSpawnAnchorPolicy.UsesExactNativeRoleSpawn("lcz", "Scp173"));
    }

    [Fact]
    public void ExactSpectatorCanRespawnThroughAlreadyActiveArenaWithoutSwitchCooldown()
    {
        Assert.True(WarmupArenaSelectionPolicy.RequiresNativeTransition(
            isAlreadyActive: true,
            isExactSpectator: true));
        Assert.False(WarmupArenaSelectionPolicy.IsSwitchCooldownApplicable(isAlreadyActive: true));
    }

    [Fact]
    public void AlreadyActiveArenaIsAcceptedNoOpForAlivePlayer()
    {
        Assert.False(WarmupArenaSelectionPolicy.RequiresNativeTransition(
            isAlreadyActive: true,
            isExactSpectator: false));
    }

    [Fact]
    public void SurfaceRoleChoicesRouteToTheCorrectArena()
    {
        Assert.Equal("pvpve", WarmupRoleArenaRouting.ResolveArenaId(isScp: false));
        Assert.Equal("lcz", WarmupRoleArenaRouting.ResolveArenaId(isScp: true));
        Assert.NotEqual("surface", WarmupRoleArenaRouting.ResolveArenaId(isScp: false));
        Assert.NotEqual("surface", WarmupRoleArenaRouting.ResolveArenaId(isScp: true));
        Assert.Equal(
            "surface",
            WarmupRoleArenaRouting.ResolveSurfaceOriginArenaId(isSurfaceAllowedRole: true, isScp: false));
        Assert.Equal(
            "pvpve",
            WarmupRoleArenaRouting.ResolveSurfaceOriginArenaId(isSurfaceAllowedRole: false, isScp: false));
        Assert.Equal(
            "lcz",
            WarmupRoleArenaRouting.ResolveSurfaceOriginArenaId(isSurfaceAllowedRole: false, isScp: true));
    }

    [Theory]
    [InlineData("FacilityGuard")]
    [InlineData("NtfPrivate")]
    [InlineData("NtfSergeant")]
    [InlineData("NtfCaptain")]
    [InlineData("NtfSpecialist")]
    public void FoundationHumanRolesAreAllowedOnSurface(string roleId)
    {
        Assert.True(WarmupRoleArenaRouting.IsSurfaceAllowedRole(roleId));
    }

    [Theory]
    [InlineData("ChaosConscript")]
    [InlineData("ChaosRifleman")]
    [InlineData("ClassD")]
    [InlineData("Scientist")]
    [InlineData("Scp173")]
    [InlineData("Tutorial")]
    public void OtherRolesAreEvacuatedFromSurface(string roleId)
    {
        Assert.False(WarmupRoleArenaRouting.IsSurfaceAllowedRole(roleId));
    }

    [Theory]
    [InlineData(true, "surface", "pvpve", false, "surface")]
    [InlineData(true, "pvpve", "surface", true, "pvpve")]
    [InlineData(false, "pvpve", "surface", true, "surface")]
    [InlineData(false, "pvpve", null, true, "surface")]
    [InlineData(false, "lcz", null, false, "lcz")]
    public void SpectatorsUseLogicalArenaWhilePlayableRolesUsePhysicalOrigin(
        bool isSpectator,
        string logicalArenaId,
        string? physicalArenaId,
        bool nativeZoneIsSurface,
        string expectedArenaId)
    {
        Assert.Equal(
            expectedArenaId,
            WarmupRoleArenaRouting.ResolveRoleChangeOriginArenaId(
                isSpectator,
                logicalArenaId,
                physicalArenaId,
                nativeZoneIsSurface));
    }

    [Fact]
    public void LczOccupancyAlwaysCreatesAnScpBot()
    {
        IReadOnlyList<WarmupArenaPopulationEntry> plan = WarmupArenaPopulationPlanner.Build(new()
        {
            LightContainmentPlayers = 1,
            FallbackBotCount = 0,
        });

        WarmupArenaPopulationEntry scp = Assert.Single(plan);
        Assert.Equal("lcz", scp.ArenaId);
        Assert.StartsWith("Scp", scp.RoleId, StringComparison.Ordinal);
    }

    [Fact]
    public void HeavyEntranceOccupancyCreatesAtLeastTwoOpposingHumanBots()
    {
        IReadOnlyList<WarmupArenaPopulationEntry> plan = WarmupArenaPopulationPlanner.Build(new()
        {
            HeavyEntrancePlayers = 1,
            HeavyEntranceBotCount = 1,
            FallbackBotCount = 0,
        });

        Assert.Equal(2, plan.Count);
        Assert.All(plan, entry => Assert.Equal("pvpve", entry.ArenaId));
        Assert.Contains(plan, entry => entry.RoleId == "ChaosRifleman");
        Assert.Contains(plan, entry => entry.RoleId == "NtfPrivate");
    }

    [Fact]
    public void EmptyServerPreservesConfiguredBaselineAndTotalCap()
    {
        IReadOnlyList<WarmupArenaPopulationEntry> baseline = WarmupArenaPopulationPlanner.Build(new()
        {
            FallbackBotCount = 3,
            DefaultArenaId = "pvpve",
        });
        IReadOnlyList<WarmupArenaPopulationEntry> capped = WarmupArenaPopulationPlanner.Build(new()
        {
            SurfacePlayers = 20,
            HeavyEntrancePlayers = 20,
            LightContainmentPlayers = 20,
            FallbackBotCount = 10,
            TotalBotCap = 10,
        });

        Assert.Equal(3, baseline.Count);
        Assert.All(baseline, entry => Assert.Equal("pvpve", entry.ArenaId));
        Assert.Equal(10, capped.Count);
    }
}
