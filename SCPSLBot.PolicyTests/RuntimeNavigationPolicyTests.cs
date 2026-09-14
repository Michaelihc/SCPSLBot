using SCPSLBot.Navigation.Policy;

namespace SCPSLBot.PolicyTests;

public sealed class DoorAreaRegistryTests
{
    private const ushort Checkpoints = 0x1;
    private const ushort ContainmentLevelOne = 0x10;
    private const ushort ContainmentLevelTwo = 0x20;
    private const ushort ArmoryLevelOne = 0x80;
    private const ushort ScpOverride = 0x400;

    [Fact]
    public void DoorsWithoutPermissionsStayWalkable()
    {
        DoorAreaRegistry registry = new();
        Assert.Equal(DoorAreaRegistry.WalkableArea, registry.GetOrAddArea(0, requireAll: false));
        Assert.Equal(0, registry.ClassCount);
    }

    [Fact]
    public void EachDistinctPermissionClassGetsOneArea()
    {
        DoorAreaRegistry registry = new();
        int containmentTwo = registry.GetOrAddArea(ContainmentLevelTwo, requireAll: false);
        int containmentTwoAgain = registry.GetOrAddArea(ContainmentLevelTwo, requireAll: false);
        int checkpoint = registry.GetOrAddArea(Checkpoints, requireAll: false);
        int checkpointAll = registry.GetOrAddArea(Checkpoints, requireAll: true);

        Assert.Equal(DoorAreaRegistry.FirstDoorArea, containmentTwo);
        Assert.Equal(containmentTwo, containmentTwoAgain);
        Assert.Equal(DoorAreaRegistry.FirstDoorArea + 1, checkpoint);
        Assert.Equal(DoorAreaRegistry.FirstDoorArea + 2, checkpointAll);
        Assert.Equal(3, registry.ClassCount);
    }

    [Fact]
    public void BotWithoutTheCardNeverGetsTheDoorArea()
    {
        DoorAreaRegistry registry = new();
        int containmentTwo = registry.GetOrAddArea(ContainmentLevelTwo, requireAll: false);

        int classDMask = registry.BuildAreaMask(0);
        int scientistMask = registry.BuildAreaMask(ContainmentLevelTwo | ContainmentLevelOne);

        Assert.Equal(0, classDMask & (1 << containmentTwo));
        Assert.NotEqual(0, scientistMask & (1 << containmentTwo));
        Assert.NotEqual(0, classDMask & (1 << DoorAreaRegistry.WalkableArea));
        Assert.NotEqual(0, classDMask & (1 << DoorAreaRegistry.ElevatorArea));
        Assert.Equal(0, classDMask & (1 << DoorAreaRegistry.NotWalkableArea));
    }

    [Fact]
    public void AnyOfPolicyAcceptsEitherFlagAndAllOfNeedsBoth()
    {
        DoorAreaRegistry registry = new();
        int anyArea = registry.GetOrAddArea(Checkpoints | ContainmentLevelOne, requireAll: false);
        int allArea = registry.GetOrAddArea(Checkpoints | ContainmentLevelOne, requireAll: true);

        int onlyCheckpoints = registry.BuildAreaMask(Checkpoints);
        int both = registry.BuildAreaMask(Checkpoints | ContainmentLevelOne);

        Assert.NotEqual(0, onlyCheckpoints & (1 << anyArea));
        Assert.Equal(0, onlyCheckpoints & (1 << allArea));
        Assert.NotEqual(0, both & (1 << allArea));
    }

    [Fact]
    public void ScpOverrideDoorsOpenForScpsButNotForKeycards()
    {
        DoorAreaRegistry registry = new();
        int area = registry.GetOrAddArea(ContainmentLevelTwo | ScpOverride, requireAll: false);

        Assert.NotEqual(0, registry.BuildAreaMask(ScpOverride) & (1 << area));
        Assert.NotEqual(0, registry.BuildAreaMask(ContainmentLevelTwo) & (1 << area));
        Assert.Equal(0, registry.BuildAreaMask(ArmoryLevelOne) & (1 << area));
    }

    [Fact]
    public void BypassModeOpensEveryDoorClass()
    {
        DoorAreaRegistry registry = new();
        int area = registry.GetOrAddArea(ArmoryLevelOne, requireAll: false);
        Assert.NotEqual(0, registry.BuildAreaMask(0, bypassAll: true) & (1 << area));
    }

    [Fact]
    public void OverflowingTheAreaBudgetFallsBackToWalkable()
    {
        DoorAreaRegistry registry = new();
        int budget = DoorAreaRegistry.MaxAreas - DoorAreaRegistry.FirstDoorArea;
        for (int index = 0; index < budget; index++)
        {
            Assert.NotEqual(DoorAreaRegistry.WalkableArea, registry.GetOrAddArea((ushort)(index + 1), requireAll: false));
        }

        Assert.Equal(DoorAreaRegistry.WalkableArea, registry.GetOrAddArea(0x3FF, requireAll: true));
        Assert.Equal(1, registry.OverflowClasses);
        Assert.Equal(budget, registry.ClassCount);
    }

    [Fact]
    public void ClearForgetsEveryClass()
    {
        DoorAreaRegistry registry = new();
        registry.GetOrAddArea(Checkpoints, requireAll: false);
        registry.Clear();
        Assert.Equal(0, registry.ClassCount);
        Assert.False(registry.TryGetClass(DoorAreaRegistry.FirstDoorArea, out _));
    }
}

public sealed class PathReplanPolicyTests
{
    [Fact]
    public void FirstCallAlwaysPlans()
    {
        PathReplanPolicy policy = new();
        Assert.True(policy.ShouldReplan(now: 10f, goalDisplacement: 0f, pathInvalid: false, generation: 1));
    }

    [Fact]
    public void StableGoalOnSameSurfaceNeverReplansPerTick()
    {
        PathReplanPolicy policy = new();
        policy.NotePlanned(now: 10f, generation: 1, failed: false);
        for (float now = 10.02f; now < 30f; now += 0.02f)
        {
            Assert.False(policy.ShouldReplan(now, goalDisplacement: 0f, pathInvalid: false, generation: 1));
        }
    }

    [Fact]
    public void GoalJumpReplansImmediately()
    {
        PathReplanPolicy policy = new();
        policy.NotePlanned(now: 10f, generation: 1, failed: false);
        Assert.False(policy.ShouldReplan(10.1f, PathReplanPolicy.GoalMoveReplanMeters - 0.01f, false, 1));
        Assert.True(policy.ShouldReplan(10.1f, PathReplanPolicy.GoalMoveReplanMeters + 0.01f, false, 1));
    }

    [Fact]
    public void CreepingGoalReplansAtBoundedCadence()
    {
        PathReplanPolicy policy = new();
        policy.NotePlanned(now: 10f, generation: 1, failed: false);
        Assert.False(policy.ShouldReplan(10.5f, goalDisplacement: 0.2f, pathInvalid: false, generation: 1));
        Assert.True(policy.ShouldReplan(10f + PathReplanPolicy.MovingGoalReplanSeconds, goalDisplacement: 0.2f, pathInvalid: false, generation: 1));
    }

    [Fact]
    public void SurfaceChangeAndInvalidPathReplan()
    {
        PathReplanPolicy policy = new();
        policy.NotePlanned(now: 10f, generation: 1, failed: false);
        Assert.True(policy.ShouldReplan(10.1f, 0f, pathInvalid: true, generation: 1));
        Assert.True(policy.ShouldReplan(10.1f, 0f, pathInvalid: false, generation: 2));
    }

    [Fact]
    public void ForceReplansOnceThenSettles()
    {
        PathReplanPolicy policy = new();
        policy.NotePlanned(now: 10f, generation: 1, failed: false);
        policy.Force();
        Assert.True(policy.ShouldReplan(10.1f, 0f, false, 1));
        policy.NotePlanned(now: 10.1f, generation: 1, failed: false);
        Assert.False(policy.ShouldReplan(10.2f, 0f, false, 1));
    }

    [Fact]
    public void FailedPlanRetriesOnlyAfterCooldownUnlessSomethingChanged()
    {
        PathReplanPolicy policy = new();
        policy.NotePlanned(now: 10f, generation: 1, failed: true);
        Assert.False(policy.HasPlan);
        Assert.False(policy.ShouldReplan(10.5f, 0f, false, 1));
        Assert.True(policy.ShouldReplan(10f + PathReplanPolicy.FailedPlanRetrySeconds, 0f, false, 1));
        Assert.True(policy.ShouldReplan(10.1f, PathReplanPolicy.GoalMoveReplanMeters + 0.1f, false, 1));
        Assert.True(policy.ShouldReplan(10.1f, 0f, false, generation: 2));
        policy.Force();
        Assert.True(policy.ShouldReplan(10.1f, 0f, false, 1));
    }
}
