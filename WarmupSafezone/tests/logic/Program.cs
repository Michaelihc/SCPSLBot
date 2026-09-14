using System;
using ScpslPluginStarter.Core;

internal static class Program
{
    private static int _assertions;

    public static int Main()
    {
        TestExitProtectionUsesMonotonicNonShorteningExpiries();
        TestBlockerAccumulatesOnlyActiveTimeAndResetsContinuously();
        TestBlockerFirstPostGraceDamageIsExact();
        TestDamageMatrix();
        TestActionMatrix();
        TestConfiguredSurfaceSafezoneGeometry();
        TestRecurringFaultIsolation();
        Console.WriteLine($"WarmupSafezone deterministic logic tests passed: {_assertions}/{_assertions}");
        return 0;
    }

    private static void TestExitProtectionUsesMonotonicNonShorteningExpiries()
    {
        ExitProtectionTracker tracker = new();
        tracker.Grant(7, 1000, 1000);
        Require(tracker.IsProtected(7, 1999), "exit protection must cover the open interval before expiry");
        tracker.Grant(7, 1500, 1000);
        tracker.Grant(7, 1200, 100);
        Require(tracker.IsProtected(7, 2499), "a later/shorter grant must not shorten the current expiry");
        Require(!tracker.IsProtected(7, 2500), "exit protection must expire exactly at its monotonic deadline");
    }

    private static void TestBlockerAccumulatesOnlyActiveTimeAndResetsContinuously()
    {
        BlockerPenaltyTracker tracker = new();
        const int grace = 3000;
        const int reset = 60000;
        Require(tracker.Update(1, true, 0, grace, reset).PunishableDeltaMilliseconds == 0, "entry has no elapsed active time");
        Require(tracker.Update(1, true, 3000, grace, reset).PunishableDeltaMilliseconds == 0, "grace duration itself is not punishable");
        BlockerUpdate first = tracker.Update(1, true, 4000, grace, reset);
        Require(first.PunishableBeforeMilliseconds == 0 && first.PunishableDeltaMilliseconds == 1000,
            "first post-grace slice begins at progression zero");

        tracker.Update(1, false, 4500, grace, reset);
        BlockerUpdate resumed = tracker.Update(1, true, 5000, grace, reset);
        Require(resumed.PunishableDeltaMilliseconds == 0, "outside time must not advance blocker punishment");
        Require(tracker.Update(1, true, 6000, grace, reset).PunishableBeforeMilliseconds == 1000,
            "re-entry before reset resumes the prior active progression");

        tracker.Update(1, false, 6000, grace, reset);
        Require(!tracker.Update(1, false, 65999, grace, reset).Reset, "reset requires a full continuous outside period");
        Require(tracker.Update(1, false, 66000, grace, reset).Reset, "reset occurs at the exact continuous outside deadline");
        Require(!tracker.Update(1, false, 70000, grace, reset).Tracked, "reset state is removed");
    }

    private static void TestBlockerFirstPostGraceDamageIsExact()
    {
        float first = BlockerDrainCalculator.Calculate(100f, 0, 1000, 1f, 2f, 35f);
        float second = BlockerDrainCalculator.Calculate(100f, 1000, 1000, 1f, 2f, 35f);
        float delayed = BlockerDrainCalculator.Calculate(100f, 0, 2000, 1f, 2f, 35f);
        Require(Math.Abs(first - 1f) < 0.0001f, "first post-grace damage must equal configured initial HP/s");
        Require(Math.Abs(second - 2f) < 0.0001f, "the multiplier starts only on the second punishable second");
        Require(Math.Abs(delayed - 3f) < 0.0001f, "delayed ticks integrate each progression slice exactly");
    }

    private static void TestDamageMatrix()
    {
        Require(!DamagePolicy.ShouldBlock(DamageActorKind.Other, false, false, false, false, false), "outside to outside is allowed");
        Require(DamagePolicy.ShouldBlock(DamageActorKind.Other, false, false, true, false, false), "outside to inside is blocked");
        Require(DamagePolicy.ShouldBlock(DamageActorKind.Other, true, false, false, false, false), "inside to outside is blocked");
        Require(DamagePolicy.ShouldBlock(DamageActorKind.Other, true, false, true, false, false), "inside to inside is blocked");
        Require(DamagePolicy.ShouldBlock(DamageActorKind.None, false, false, true, false, false), "environment to inside is blocked");
        Require(DamagePolicy.ShouldBlock(DamageActorKind.Self, true, false, true, false, false), "self damage inside is blocked through victim policy");
        Require(DamagePolicy.ShouldBlock(DamageActorKind.Other, false, true, false, false, false), "exit-protected outgoing damage is blocked");
        Require(DamagePolicy.ShouldBlock(DamageActorKind.None, false, false, false, true, false), "exit-protected incoming damage is blocked");
        Require(!DamagePolicy.ShouldBlock(DamageActorKind.None, false, false, true, true, true), "plugin-owned drains bypass protection synchronously");
    }

    private static void TestRecurringFaultIsolation()
    {
        ResilientSchedule schedule = new();
        int failingRuns = 0;
        int healthyRuns = 0;
        int faults = 0;
        schedule.Add("failing", 1000, () =>
        {
            failingRuns++;
            throw new InvalidOperationException("injected");
        }, 0);
        schedule.Add("healthy", 1000, () => healthyRuns++, 0);

        schedule.RunDue(0, (_, _) => faults++);
        Require(failingRuns == 1 && healthyRuns == 1 && faults == 1, "one service fault is isolated from siblings");
        schedule.RunDue(999, (_, _) => faults++);
        Require(failingRuns == 1 && healthyRuns == 1, "work does not run before its next deadline");
        schedule.RunDue(1000, (_, _) => faults++);
        Require(failingRuns == 2 && healthyRuns == 2 && faults == 2, "a failed recurring service runs again on later passes");
    }

    private static void TestActionMatrix()
    {
        SafezoneActionKind[] deniedKinds =
        [
            SafezoneActionKind.Firearm,
            SafezoneActionKind.DryFire,
            SafezoneActionKind.Throwable,
            SafezoneActionKind.ChargedDangerousItem,
            SafezoneActionKind.ScpTargetedOffense,
            SafezoneActionKind.ScpAreaOffense,
        ];

        foreach (SafezoneActionKind action in deniedKinds)
        {
            Require(ActionPolicy.ShouldCancel(action, actorProtected: true, targetProtected: false), $"{action} is denied for a protected actor");
            Require(ActionPolicy.ShouldCancel(action, actorProtected: false, targetProtected: true), $"{action} is denied against a protected explicit target");
            Require(!ActionPolicy.ShouldCancel(action, actorProtected: false, targetProtected: false), $"{action} is allowed when neither endpoint is protected");
        }

        Require(!ActionPolicy.ShouldCancel(SafezoneActionKind.UtilityOrMovement, true, true), "utility/movement remains allowed");
        Require(!ActionPolicy.ShouldCancel(SafezoneActionKind.IndirectHazardDamage, true, true), "indirect hazards defer to final damage policy");
    }

    private static void TestConfiguredSurfaceSafezoneGeometry()
    {
        Require(SurfaceSafezoneGeometry.Contains(136f, 295f, -17f, "z", -17f, false, 91f),
            "configured Surface safezone includes its threshold");
        Require(SurfaceSafezoneGeometry.Contains(136f, 295f, -10f, "z", -17f, false, 91f),
            "configured Surface safezone includes the restored safe side");
        Require(!SurfaceSafezoneGeometry.Contains(90f, 295f, -10f, "z", -17f, false, 91f),
            "minimum X excludes the road outside the restored zone");
        Require(!SurfaceSafezoneGeometry.Contains(136f, 295f, -17.01f, "z", -17f, false, 91f),
            "configured Surface threshold has an exact outside boundary");
        Require(SurfaceSafezoneGeometry.ContainsBlocker(136f, 295f, -26f, "z", -17f, false, 91f, 9f),
            "blocker band includes its outer boundary");
        Require(SurfaceSafezoneGeometry.ContainsBlocker(136f, 295f, -20f, "z", -17f, false, 91f, 9f),
            "blocker band covers the restored approach");
        Require(!SurfaceSafezoneGeometry.ContainsBlocker(136f, 295f, -17f, "z", -17f, false, 91f, 9f),
            "blocker band excludes the safezone itself");
        Require(SurfaceSafezoneGeometry.Contains(136f, 10f, 0f, "y", 10f, true, 91f),
            "axis and less-than configuration remain supported");
    }

    private static void Require(bool condition, string message)
    {
        _assertions++;
        if (!condition)
        {
            throw new InvalidOperationException($"Assertion {_assertions} failed: {message}");
        }
    }
}
