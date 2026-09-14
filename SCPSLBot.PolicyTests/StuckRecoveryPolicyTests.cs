using SCPSLBot.AI.FirstPersonControl.Movement;

namespace SCPSLBot.PolicyTests;

public sealed class StuckRecoveryPolicyTests
{
    [Fact]
    public void WalkingTowardTheWaypointNeverEscalates()
    {
        var policy = new StuckRecoveryPolicy();
        var distance = 10f;
        for (var t = 0f; t < 6f; t += 0.1f)
        {
            distance -= 0.3f;
            var actions = policy.Tick(t, hasMoveIntent: true, x: 10f - distance, z: 0f, waypointDistance: distance, doorAhead: false);
            Assert.Equal(StuckRecoveryActions.None, actions);
        }

        Assert.Equal(0f, policy.StuckSeconds);
    }

    [Fact]
    public void StandingStillByChoiceIsNotStuck()
    {
        var policy = new StuckRecoveryPolicy();
        for (var t = 0f; t < 10f; t += 0.5f)
        {
            Assert.Equal(StuckRecoveryActions.None, policy.Tick(t, hasMoveIntent: false, 0f, 0f, 5f, false));
        }
    }

    [Fact]
    public void LadderEscalatesNudgeThenJumpThenReplan()
    {
        var policy = new StuckRecoveryPolicy();
        policy.Tick(0f, true, 0f, 0f, 5f, false);

        Assert.Equal(StuckRecoveryActions.None, policy.Tick(0.5f, true, 0f, 0f, 5f, false));

        var atNudge = policy.Tick(0.8f, true, 0f, 0f, 5f, false);
        Assert.True(atNudge.HasFlag(StuckRecoveryActions.Nudge));
        Assert.False(atNudge.HasFlag(StuckRecoveryActions.Jump));

        var atJump = policy.Tick(1.6f, true, 0f, 0f, 5f, false);
        Assert.True(atJump.HasFlag(StuckRecoveryActions.Jump));
        Assert.False(atJump.HasFlag(StuckRecoveryActions.Replan));

        var atBackOff = policy.Tick(2.3f, true, 0f, 0f, 5f, false);
        Assert.True(atBackOff.HasFlag(StuckRecoveryActions.BackOff));

        var atReplan = policy.Tick(2.6f, true, 0f, 0f, 5f, false);
        Assert.True(atReplan.HasFlag(StuckRecoveryActions.Replan));
        Assert.False(atReplan.HasFlag(StuckRecoveryActions.AvoidCrossing));
        Assert.Equal(1, policy.ReplansAtSpot);
        Assert.Equal(0f, policy.StuckSeconds);
    }

    [Fact]
    public void SidewaysNudgesDoNotCountAsProgressOnceRecovering()
    {
        var policy = new StuckRecoveryPolicy();
        policy.Tick(0f, true, 0f, 0f, 5f, false);
        policy.Tick(0.8f, true, 0f, 0f, 5f, false);

        // The nudge shoves the bot 0.6 m sideways; the waypoint is no closer.
        var actions = policy.Tick(1.6f, true, 0.6f, 0f, 5.05f, false);
        Assert.True(actions.HasFlag(StuckRecoveryActions.Jump));
        Assert.True(policy.StuckSeconds >= 1.5f);
    }

    [Fact]
    public void SmallGainsDuringRecoveryDoNotResetTheLadder()
    {
        var policy = new StuckRecoveryPolicy();
        policy.Tick(0f, true, 0f, 0f, 3f, false, progressStamp: 1);
        policy.Tick(1.6f, true, 0f, 0f, 3f, false, progressStamp: 1);
        Assert.True(policy.StuckSeconds >= 1.5f);

        // A forced jump landed 0.3 m nearer: not a real stride while recovering.
        policy.Tick(1.8f, true, 0f, 0f, 2.7f, false, progressStamp: 1);
        Assert.True(policy.StuckSeconds >= 1.7f);

        // A genuine 0.7 m stride toward the waypoint is progress.
        policy.Tick(2.0f, true, 0f, 0f, 2.3f, false, progressStamp: 1);
        Assert.Equal(0f, policy.StuckSeconds);
    }

    [Fact]
    public void CreepingAlongAWallWithoutApproachingEscalates()
    {
        var policy = new StuckRecoveryPolicy();
        policy.Tick(0f, true, 0f, 0f, 5f, false, progressStamp: 1);

        // The bot slides 0.1 m per tick along a railing; the waypoint never gets closer.
        var sawReplan = false;
        for (var t = 0.1f; t <= 3f; t += 0.1f)
        {
            sawReplan |= policy.Tick(t, true, t, 0f, 5f, false, progressStamp: 1).HasFlag(StuckRecoveryActions.Replan);
        }

        Assert.True(sawReplan);
    }

    [Fact]
    public void RepeatedReplansAtTheSameSpotPenalizeTheCrossingThenAbandon()
    {
        var policy = new StuckRecoveryPolicy();
        var time = 0f;
        StuckRecoveryActions last = StuckRecoveryActions.None;
        var replans = 0;
        while (replans < 2)
        {
            time += 0.5f;
            last = policy.Tick(time, true, 0.1f, 0.1f, 5f, false);
            if (last.HasFlag(StuckRecoveryActions.Replan))
            {
                replans++;
            }
        }

        Assert.True(last.HasFlag(StuckRecoveryActions.AvoidCrossing));
        Assert.False(last.HasFlag(StuckRecoveryActions.AbandonGoal));

        while (replans < 4)
        {
            time += 0.5f;
            last = policy.Tick(time, true, 0.1f, 0.1f, 5f, false);
            if (last.HasFlag(StuckRecoveryActions.Replan))
            {
                replans++;
            }
        }

        Assert.True(last.HasFlag(StuckRecoveryActions.AbandonGoal));
        Assert.Equal(0, policy.ReplansAtSpot);
    }

    [Fact]
    public void ReplanElsewhereRestartsTheSpotCounter()
    {
        var policy = new StuckRecoveryPolicy();
        policy.Tick(0f, true, 0f, 0f, 5f, false);
        policy.Tick(2.6f, true, 0f, 0f, 5f, false);
        Assert.Equal(1, policy.ReplansAtSpot);

        // Real progress to a far spot, then stuck again there.
        policy.Tick(3f, true, 0f, 0f, 2f, false);
        policy.Tick(3.1f, true, 10f, 10f, 2f, false);
        policy.Tick(5.8f, true, 10f, 10f, 2f, false);
        Assert.Equal(1, policy.ReplansAtSpot);
    }

    [Fact]
    public void PathAdvanceCountsAsProgress()
    {
        var policy = new StuckRecoveryPolicy();
        policy.Tick(0f, true, 0f, 0f, 0.4f, false, progressStamp: 1);
        policy.Tick(0.9f, true, 0f, 0f, 0.4f, false, progressStamp: 1);
        Assert.True(policy.StuckSeconds > 0.7f);

        // The navigator crossed the portal and now steers at the next corner 4 m away.
        var actions = policy.Tick(1f, true, 0f, 0f, 4f, false, progressStamp: 2);
        Assert.Equal(StuckRecoveryActions.None, actions);
        Assert.Equal(0f, policy.StuckSeconds);
    }

    [Fact]
    public void WaypointFlipFlopWithoutAdvanceIsNotProgress()
    {
        var policy = new StuckRecoveryPolicy();
        policy.Tick(0f, true, 0f, 0f, 0.4f, false, progressStamp: 1);

        // The plan keeps swapping between a near and a far waypoint while the bot stands still.
        var sawReplan = false;
        for (var t = 0.1f; t <= 3f; t += 0.1f)
        {
            var distance = ((int)Math.Round(t * 10)) % 2 == 0 ? 0.4f : 4f;
            sawReplan |= policy.Tick(t, true, 0f, 0f, distance, false, progressStamp: 1).HasFlag(StuckRecoveryActions.Replan);
        }

        Assert.True(sawReplan);
    }

    [Fact]
    public void DoorAheadIsOpenedWithARetryInterval()
    {
        var policy = new StuckRecoveryPolicy();
        policy.Tick(0f, true, 0f, 0f, 3f, doorAhead: true);
        var first = policy.Tick(0.8f, true, 0f, 0f, 3f, doorAhead: true);
        var second = policy.Tick(0.9f, true, 0f, 0f, 3f, doorAhead: true);
        var third = policy.Tick(1.5f, true, 0f, 0f, 3f, doorAhead: true);

        Assert.True(first.HasFlag(StuckRecoveryActions.OpenDoor));
        Assert.False(second.HasFlag(StuckRecoveryActions.OpenDoor));
        Assert.True(third.HasFlag(StuckRecoveryActions.OpenDoor));
    }
}
