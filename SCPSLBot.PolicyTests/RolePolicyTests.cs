using SCPSLBot.Warmup.Controls;

namespace SCPSLBot.PolicyTests;

public sealed class RolePolicyTests
{
    [Theory]
    [InlineData("None")]
    [InlineData("Spectator")]
    [InlineData("Destroyed")]
    [InlineData("Overwatch")]
    [InlineData("Filmmaker")]
    [InlineData("CustomRole")]
    [InlineData("Tutorial")]
    public void NonGameplayAndTutorialRolesAreExcluded(string roleId)
    {
        Assert.False(WarmupRoleSelectionPolicy.IsPlayerSelectableRole(roleId));
    }

    [Theory]
    [InlineData("ChaosRifleman")]
    [InlineData("NtfPrivate")]
    [InlineData("Scp173")]
    public void NativeGameplayRolesArePermissive(string roleId)
    {
        Assert.True(WarmupRoleSelectionPolicy.IsPlayerSelectableRole(roleId));
    }

    [Fact]
    public void RegularSpectatorCanUseConfiguredRespawnRoles()
    {
        var service = new RoleEligibilityService(CreateRoleConfig());
        RoleEligibilitySnapshot snapshot = Snapshot(
            isAlive: false,
            isSpectator: true,
            isOnSurface: false,
            presetAllowsScp: true,
            hasAdminPermission: false);

        Assert.Equal(
            new[] { "ClassD", "Scp173" },
            service.GetConfiguredRoles(snapshot, RoleControlSurface.Regular)
                .Select(candidate => candidate.RoleId)
                .ToArray());
        Assert.True(service.Evaluate(snapshot, RoleControlSurface.Regular, "ClassD").Succeeded);
    }

    [Fact]
    public void PresetAndSurfaceDoNotGateNativeGameplayRoles()
    {
        var service = new RoleEligibilityService(CreateRoleConfig());

        ControlResult surface = service.Evaluate(
            Snapshot(true, false, true, true, false),
            RoleControlSurface.Regular,
            "Scp173");
        ControlResult preset = service.Evaluate(
            Snapshot(true, false, false, false, false),
            RoleControlSurface.Regular,
            "Scp173");

        Assert.True(surface.Succeeded);
        Assert.True(preset.Succeeded);
    }

    [Fact]
    public void ConfigurationAndCapacityDoNotGateNativeGameplayRole()
    {
        var service = new RoleEligibilityService(new RoleControlsConfig());
        var snapshot = new RoleEligibilitySnapshot(
            "76561198000000001@steam",
            isAuthenticated: true,
            isRealPlayer: true,
            isWarmupActive: true,
            isAlive: false,
            isSpectator: true,
            isOnSurface: true,
            hasAdminForcePermission: false,
            currentRoleId: "Spectator",
            new ArenaPresetDefinition("closed", allowScpPlayers: false, new[] { "ClassD" }),
            new[] { new RoleCandidateDefinition("ChaosRifleman", false, true, false) });

        Assert.True(service.Evaluate(snapshot, RoleControlSurface.Regular, "ChaosRifleman").Succeeded);
    }

    [Fact]
    public void AdminForceUsesSeparatePermissionAndTutorialRemainsExcluded()
    {
        var service = new RoleEligibilityService(CreateRoleConfig());
        RoleEligibilitySnapshot allowed = Snapshot(false, true, true, false, true);
        RoleEligibilitySnapshot denied = Snapshot(false, true, true, false, false);

        Assert.Equal(
            ControlResultCode.RoleUnavailable,
            service.Evaluate(allowed, RoleControlSurface.AdminForce, "Tutorial").Code);
        Assert.Equal(
            ControlResultCode.PermissionDenied,
            service.Evaluate(denied, RoleControlSurface.AdminForce, "Scp173").Code);
        Assert.Equal(
            ControlResultCode.RoleUnavailable,
            service.Evaluate(allowed, RoleControlSurface.Regular, "Tutorial").Code);
    }

    [Fact]
    public void CapacityAndCurrentRoleDoNotGatePermissiveRoleSelection()
    {
        var service = new RoleEligibilityService(CreateRoleConfig());
        RoleEligibilitySnapshot snapshot = Snapshot(
            isAlive: true,
            isSpectator: false,
            isOnSurface: false,
            presetAllowsScp: true,
            hasAdminPermission: false);

        string[] slots = service.GetConfiguredRoles(snapshot, RoleControlSurface.Regular)
            .Select(candidate => candidate.RoleId)
            .ToArray();
        string[] executable = service.GetEligibleRoles(snapshot, RoleControlSurface.Regular)
            .Select(candidate => candidate.RoleId)
            .ToArray();

        Assert.Equal(new[] { "ClassD", "Scp173" }, slots);
        Assert.Contains("ClassD", executable);
        Assert.Contains("Scp173", executable);
    }

    [Fact]
    public void StaleIdentityIsRejectedBeforeSnapshotOrRoleMutation()
    {
        var trace = new List<string>();
        var player = new FakeRolePlayer("new-user@steam", "ClassD");
        RoleChangeService service = CreateChangeService(trace, player);

        ControlResult result = service.TryChangeRole(
            player,
            new RoleChangeRequest("old-user@steam", "Scp173", RoleControlSurface.Regular));

        Assert.Equal(ControlResultCode.InvalidRequest, result.Code);
        Assert.Empty(trace);
        Assert.Equal("ClassD", player.CurrentRoleId);
    }

    [Fact]
    public void RoleChangeCapturesThenResolvesAnchorThenSetsAndVerifiesExactRole()
    {
        var trace = new List<string>();
        var player = new FakeRolePlayer("76561198000000001@steam", "ClassD");
        RoleChangeService service = CreateChangeService(trace, player);

        ControlResult result = service.TryChangeRole(
            player,
            new RoleChangeRequest(player.FullUserId, "Scp173", RoleControlSurface.Regular));

        Assert.True(result.Succeeded);
        Assert.Equal("Scp173", player.CurrentRoleId);
        Assert.Equal(new[] { "capture", "resolve:Scp173", "set:Scp173" }, trace);
    }

    [Fact]
    public void MissingAnchorFailsBeforeNativeRoleMutation()
    {
        var trace = new List<string>();
        var player = new FakeRolePlayer("76561198000000001@steam", "ClassD");
        RoleChangeService service = CreateChangeService(trace, player, resolveAnchor: false);

        ControlResult result = service.TryChangeRole(
            player,
            new RoleChangeRequest(player.FullUserId, "Scp173", RoleControlSurface.Regular));

        Assert.Equal(ControlResultCode.SpawnAnchorUnavailable, result.Code);
        Assert.Equal(new[] { "capture", "resolve:Scp173" }, trace);
        Assert.Equal("ClassD", player.CurrentRoleId);
    }

    [Fact]
    public void SubstitutedRoleReportsThatOriginalRoleWasRolledBackWithoutFallback()
    {
        var trace = new List<string>();
        var player = new FakeRolePlayer("76561198000000001@steam", "ClassD");
        RoleChangeService service = CreateChangeService(
            trace,
            player,
            execution: ExactRoleChangeExecutionResult.MismatchRolledBack);

        ControlResult result = service.TryChangeRole(
            player,
            new RoleChangeRequest(player.FullUserId, "Scp173", RoleControlSurface.Regular));

        Assert.Equal(ControlResultCode.ExactRoleMismatchRolledBack, result.Code);
        Assert.Equal("ClassD", player.CurrentRoleId);
        Assert.DoesNotContain("ChaosRifleman", trace);
    }

    private static RoleControlsConfig CreateRoleConfig() => new()
    {
        AllowedRegularRoleIds = new List<string> { "ClassD", "Scp173" },
        AllowedAdminForceRoleIds = new List<string> { "ClassD", "Scp173", "Tutorial" },
    };

    private static RoleEligibilitySnapshot Snapshot(
        bool isAlive,
        bool isSpectator,
        bool isOnSurface,
        bool presetAllowsScp,
        bool hasAdminPermission) =>
        new(
            "76561198000000001@steam",
            isAuthenticated: true,
            isRealPlayer: true,
            isWarmupActive: true,
            isAlive,
            isSpectator,
            isOnSurface,
            hasAdminPermission,
            isSpectator ? "Spectator" : "ClassD",
            new ArenaPresetDefinition(
                "test",
                presetAllowsScp,
                new[] { "ClassD", "Scp173", "Tutorial" }),
            new[]
            {
                new RoleCandidateDefinition("ClassD", false, true, true),
                new RoleCandidateDefinition("Scp173", true, true, true),
                new RoleCandidateDefinition("Tutorial", false, true, true, isAdminOnly: true),
            });

    private static RoleChangeService CreateChangeService(
        List<string> trace,
        FakeRolePlayer player,
        bool resolveAnchor = true,
        ExactRoleChangeExecutionResult execution = ExactRoleChangeExecutionResult.Succeeded)
    {
        var eligibility = new RoleEligibilityService(CreateRoleConfig());
        var snapshotSource = new FakeSnapshotSource(trace);
        var anchor = new FakeAnchorProvider(trace, resolveAnchor);
        var executor = new FakeRoleExecutor(trace, execution);
        return new RoleChangeService(
            eligibility,
            snapshotSource,
            anchor,
            executor,
            new PerUserRequestGuard());
    }

    private sealed class FakeRolePlayer : IRoleChangePlayer
    {
        public FakeRolePlayer(string fullUserId, string currentRoleId)
        {
            FullUserId = fullUserId;
            CurrentRoleId = currentRoleId;
        }

        public string FullUserId { get; }

        public string CurrentRoleId { get; set; }
    }

    private sealed class FakeSnapshotSource : IRoleEligibilitySnapshotSource
    {
        private readonly List<string> trace;

        public FakeSnapshotSource(List<string> trace) => this.trace = trace;

        public RoleEligibilitySnapshot Capture(IRoleChangePlayer player)
        {
            trace.Add("capture");
            return new RoleEligibilitySnapshot(
                player.FullUserId,
                true,
                true,
                true,
                true,
                false,
                false,
                true,
                player.CurrentRoleId,
                new ArenaPresetDefinition("test", true, new[] { "ClassD", "Scp173" }),
                new[]
                {
                    new RoleCandidateDefinition("ClassD", false, true, true),
                    new RoleCandidateDefinition("Scp173", true, true, true),
                    new RoleCandidateDefinition("Tutorial", false, true, true, true),
                });
        }
    }

    private sealed class FakeAnchorProvider : ISpawnAnchorProvider
    {
        private readonly List<string> trace;
        private readonly bool resolve;

        public FakeAnchorProvider(List<string> trace, bool resolve)
        {
            this.trace = trace;
            this.resolve = resolve;
        }

        public bool TryResolve(
            string exactRoleId,
            bool isScp,
            ArenaPresetDefinition activePreset,
            out SpawnAnchor anchor)
        {
            trace.Add($"resolve:{exactRoleId}");
            anchor = new SpawnAnchor(1f, 2f, 3f, 90f);
            return resolve;
        }
    }

    private sealed class FakeRoleExecutor : IExactRoleChangeExecutor
    {
        private readonly List<string> trace;
        private readonly ExactRoleChangeExecutionResult execution;

        public FakeRoleExecutor(List<string> trace, ExactRoleChangeExecutionResult execution)
        {
            this.trace = trace;
            this.execution = execution;
        }

        public ExactRoleChangeExecutionResult TrySetExactRole(
            IRoleChangePlayer player,
            string exactRoleId,
            RoleControlSurface surface,
            SpawnAnchor anchor)
        {
            trace.Add($"set:{exactRoleId}");
            var fake = Assert.IsType<FakeRolePlayer>(player);
            if (execution == ExactRoleChangeExecutionResult.Succeeded)
            {
                fake.CurrentRoleId = exactRoleId;
            }

            return execution;
        }
    }
}
