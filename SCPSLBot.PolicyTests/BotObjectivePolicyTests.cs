using SCPSLBot.AI;
using SCPSLBot.AI.FirstPersonControl.Objectives;

namespace SCPSLBot.PolicyTests;

public sealed class BotObjectivePolicyTests
{
    private const float Far = 30f;

    [Fact]
    public void ArrivalHoldsAndOnlyLeavingTheLeashWalksBack()
    {
        var policy = new BotObjectivePolicy(0f);
        Assert.Equal(BotObjectiveActions.None, policy.BeginTick(0f, Far));
        Assert.Equal(BotObjectiveActions.None, Walk(policy, 0.1f, Far, stamp: 1));

        Assert.Equal(BotObjectiveActions.Hold, Walk(policy, 5f, 0.5f, stamp: 9));
        Assert.Equal(BotObjectivePhase.Holding, policy.Phase);
        Assert.False(policy.AtNearestReachable);

        Assert.Equal(BotObjectiveActions.Hold, policy.BeginTick(60f, BotObjectivePolicy.HoldLeashMeters));
        Assert.Equal(BotObjectiveActions.None, policy.BeginTick(61f, BotObjectivePolicy.HoldLeashMeters + 0.1f));
        Assert.Equal(BotObjectivePhase.Moving, policy.Phase);
    }

    [Fact]
    public void UnreachableGoalHoldsAtThePathEndAndRetriesPeriodically()
    {
        var policy = new BotObjectivePolicy(0f);
        Assert.Equal(BotObjectiveActions.Hold,
            policy.ObserveMove(1f, Far, hasCompletePath: false, hasPartialPath: true, pathEndMeters: 0.4f, progressStamp: 3));
        Assert.Equal(BotObjectivePhase.Holding, policy.Phase);
        Assert.True(policy.AtNearestReachable);

        // Nearest-reachable holds ignore the leash: the goal is far by definition.
        Assert.Equal(BotObjectiveActions.Hold, policy.BeginTick(1f + BotObjectivePolicy.UnreachableRecheckSeconds - 0.1f, Far));
        Assert.Equal(BotObjectiveActions.ResolveGoal | BotObjectiveActions.Replan,
            policy.BeginTick(1f + BotObjectivePolicy.UnreachableRecheckSeconds, Far));
        Assert.Equal(BotObjectivePhase.Moving, policy.Phase);
        Assert.False(policy.AtNearestReachable);
    }

    [Fact]
    public void MissingPlanWaitsOutTheGraceThenHoldsAndResolvesTheGoal()
    {
        var policy = new BotObjectivePolicy(0f);
        Assert.Equal(BotObjectiveActions.None, NoPlan(policy, 1f));
        Assert.Equal(BotObjectiveActions.None, NoPlan(policy, 1f + BotObjectivePolicy.NoPathGraceSeconds - 0.1f));
        Assert.Equal(BotObjectiveActions.Hold | BotObjectiveActions.ResolveGoal,
            NoPlan(policy, 1f + BotObjectivePolicy.NoPathGraceSeconds));
        Assert.True(policy.AtNearestReachable);
        Assert.Equal(0, policy.StallCount);
    }

    [Fact]
    public void StalledWalkingEscalatesReplanThenCrossingThenGoalAndProgressResetsTheLadder()
    {
        var policy = new BotObjectivePolicy(0f);
        Assert.Equal(BotObjectiveActions.None, Walk(policy, 0f, 20f, stamp: 1));

        var stall = BotObjectivePolicy.StallSeconds;
        Assert.Equal(BotObjectiveActions.None, Walk(policy, stall - 0.1f, 19.8f, stamp: 1));
        Assert.Equal(BotObjectiveActions.Replan, Walk(policy, stall, 19.8f, stamp: 1));
        Assert.Equal(BotObjectiveActions.AvoidCrossing, Walk(policy, stall * 2f, 19.8f, stamp: 1));
        Assert.Equal(BotObjectiveActions.ResolveGoal | BotObjectiveActions.Replan, Walk(policy, stall * 3f, 19.8f, stamp: 1));
        Assert.Equal(3, policy.StallCount);

        // A passed corner is progress: the next stall starts from a plain replan again.
        Assert.Equal(BotObjectiveActions.None, Walk(policy, stall * 3f + 1f, 19.8f, stamp: 2));
        Assert.Equal(BotObjectiveActions.Replan, Walk(policy, stall * 4f + 1f, 19.8f, stamp: 2));
    }

    [Fact]
    public void CombatTimeIsNotChargedToTheStallWindowAndEngagementsCountOnce()
    {
        var policy = new BotObjectivePolicy(0f);
        Assert.Equal(BotObjectiveActions.None, Walk(policy, 0f, 20f, stamp: 1));

        policy.NoteEngaged();
        policy.NoteEngaged();
        Assert.Equal(BotObjectivePhase.Engaging, policy.Phase);
        Assert.Equal(1, policy.Engagements);

        Assert.Equal(BotObjectiveActions.None, policy.BeginTick(30f, 20f));
        Assert.Equal(BotObjectivePhase.Moving, policy.Phase);
        Assert.Equal(BotObjectiveActions.None, Walk(policy, 30f, 20f, stamp: 1));
        Assert.Equal(BotObjectiveActions.None, Walk(policy, 30f + BotObjectivePolicy.StallSeconds - 0.1f, 20f, stamp: 1));
        Assert.Equal(0, policy.StallCount);

        policy.NoteEngaged();
        Assert.Equal(2, policy.Engagements);
    }

    [Theory]
    [InlineData(0.5f, 0f, 0.5f)]
    [InlineData(0.5f, 0.96f, 0.5f)]
    [InlineData(0.5f, -1.5f, 0.5f)]
    [InlineData(0.5f, 4f, 3f)]
    [InlineData(0.5f, -4f, 3f)]
    public void GoalDistanceAcceptsFloorAndStandingRootButNotAnotherStorey(float horizontal, float vertical, float expected)
    {
        Assert.Equal(expected, BotObjectivePolicy.GoalDistance(horizontal, vertical), 4);
    }

    [Theory]
    [InlineData(0f, true)]
    [InlineData(25f, true)]
    [InlineData(BotObjectivePolicy.MaxEngageRadius, true)]
    [InlineData(-0.1f, false)]
    [InlineData(BotObjectivePolicy.MaxEngageRadius + 1f, false)]
    [InlineData(float.NaN, false)]
    [InlineData(float.PositiveInfinity, false)]
    public void RequestsNeedAFinitePointAndABoundedRadius(float radius, bool valid)
    {
        Assert.Equal(valid, BotObjectivePolicy.IsValidRequest(1f, 2f, 3f, radius));
        Assert.False(BotObjectivePolicy.IsValidRequest(float.NaN, 2f, 3f, 10f));
        Assert.False(BotObjectivePolicy.IsValidRequest(1f, float.NegativeInfinity, 3f, 10f));
    }

    private static BotObjectiveActions Walk(BotObjectivePolicy policy, float now, float remaining, int stamp)
        => policy.ObserveMove(now, remaining, hasCompletePath: true, hasPartialPath: false, pathEndMeters: remaining, progressStamp: stamp);

    private static BotObjectiveActions NoPlan(BotObjectivePolicy policy, float now)
        => policy.ObserveMove(now, Far, hasCompletePath: false, hasPartialPath: false, pathEndMeters: float.PositiveInfinity, progressStamp: 0);
}
