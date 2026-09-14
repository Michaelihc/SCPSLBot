using ServerKeybinds;

internal static class Program
{
    private static int Main()
    {
        (string Name, Action Test)[] tests =
        {
            ("debounce_coalesces_and_keeps_latest_snapshot", DebounceCoalescesAndKeepsLatestSnapshot),
            ("identical_fingerprint_is_suppressed", IdenticalFingerprintIsSuppressed),
            ("minimum_interval_keeps_latest_replacement", MinimumIntervalKeepsLatestReplacement),
            ("rolling_minute_caps_at_six", RollingMinuteCapsAtSix),
            ("idle_time_does_not_create_work", IdleTimeDoesNotCreateWork),
            ("personal_interest_never_fans_out", PersonalInterestNeverFansOut),
            ("population_boundaries_route_only_intersection", PopulationBoundariesRouteOnlyIntersection),
            ("dropdown_acquisition_is_visible_to_staging", DropdownAcquisitionIsVisibleToStaging),
            ("dropdown_duplicate_is_swallowed", DropdownDuplicateIsSwallowed),
            ("dropdown_change_is_distinct_from_acquisition", DropdownChangeIsDistinctFromAcquisition),
        };

        try
        {
            foreach ((string name, Action test) in tests)
            {
                test();
                Console.WriteLine("PASS " + name);
            }

            Console.WriteLine($"PASS all {tests.Length} pure scenarios");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL " + exception);
            return 1;
        }
    }

    private static void DebounceCoalescesAndKeepsLatestSnapshot()
    {
        FakeClock clock = new();
        List<Sent> sent = new();
        SssRefreshCoordinator<int, string> sut = Coordinator(clock, sent);

        for (int i = 0; i < 30; i++)
        {
            sut.Request(7, "view-" + i, "reason-" + i);
            clock.Advance(0.01);
        }

        Equal(0, sent.Count, "requests must not send synchronously");
        Equal(1, sut.PendingCount, "one player has one candidate");
        Equal(30L, sut.Counters.Requested, "requested counter");
        Equal(29L, sut.Counters.Coalesced, "coalesced counter");

        clock.Advance(0.48);
        sut.ProcessDue();
        Equal(0, sent.Count, "trailing debounce has not elapsed");
        clock.Advance(0.02);
        sut.ProcessDue();
        Equal(1, sent.Count, "one send after debounce");
        Equal("view-29", sent[0].Snapshot, "latest snapshot wins");
        Equal(30, sent[0].Reasons.Count, "all distinct reasons coalesce");
    }

    private static void IdenticalFingerprintIsSuppressed()
    {
        FakeClock clock = new();
        List<Sent> sent = new();
        SssRefreshCoordinator<int, string> sut = Coordinator(clock, sent);
        sut.RecordSent(1, "same");

        clock.Advance(10);
        sut.Request(1, "same", "role");
        clock.Advance(0.5);
        sut.ProcessDue();

        Equal(0, sent.Count, "identical view not sent");
        Equal(1L, sut.Counters.IdenticalSnapshots, "identical counter");
        Equal(0, sut.PendingCount, "identical candidate removed");
    }

    private static void MinimumIntervalKeepsLatestReplacement()
    {
        FakeClock clock = new();
        List<Sent> sent = new();
        SssRefreshCoordinator<int, string> sut = Coordinator(clock, sent);

        sut.Request(1, "first", "join-followup");
        clock.Advance(0.5);
        sut.ProcessDue();
        Equal(1, sent.Count, "first candidate sent");

        sut.Request(1, "second", "role");
        clock.Advance(0.5);
        sut.ProcessDue();
        Equal(1, sent.Count, "minimum interval blocks second");
        Equal(1L, sut.Counters.RateLimited, "rate limit counted once");

        clock.Advance(0.2);
        sut.Request(1, "latest", "zone");
        clock.Advance(1.3);
        sut.ProcessDue();
        Equal(2, sent.Count, "send at two-second boundary");
        Equal("latest", sent[1].Snapshot, "blocked candidate was replaced");
        SequenceEqual(new[] { "role", "zone" }, sent[1].Reasons, "reasons survive replacement");
    }

    private static void RollingMinuteCapsAtSix()
    {
        FakeClock clock = new();
        List<Sent> sent = new();
        SssRefreshCoordinator<int, string> sut = Coordinator(clock, sent);

        for (int i = 0; i < 6; i++)
        {
            sut.Request(4, "v" + i, "change");
            clock.Advance(0.5);
            sut.ProcessDue();
            if (i < 5)
            {
                clock.Advance(1.5);
            }
        }

        Equal(6, sent.Count, "first six sends fit budget");
        clock.Advance(1.5);
        sut.Request(4, "v6", "change");
        clock.Advance(0.5);
        sut.ProcessDue();
        Equal(6, sent.Count, "seventh blocked inside rolling minute");

        clock.Set(60.49);
        sut.ProcessDue();
        Equal(6, sent.Count, "still inside window");
        clock.Set(60.5);
        sut.ProcessDue();
        Equal(7, sent.Count, "oldest send expires exactly at sixty seconds");
    }

    private static void IdleTimeDoesNotCreateWork()
    {
        FakeClock clock = new();
        List<Sent> sent = new();
        SssRefreshCoordinator<int, string> sut = Coordinator(clock, sent);
        sut.RecordSent(1, "join");
        clock.Advance(600);
        Equal(null, sut.ProcessDue(), "ten idle minutes have no timer");
        Equal(0, sent.Count, "idle time sends nothing");
    }

    private static void PersonalInterestNeverFansOut()
    {
        SssInterestIndex<int> sut = new();
        sut.Track(1, SssInterest.All);
        sut.Track(2, SssInterest.All);
        sut.Track(3, SssInterest.Permission);

        SssInterest[] personal =
        {
            SssInterest.Role,
            SssInterest.Item,
            SssInterest.Cooldown,
            SssInterest.Title,
            SssInterest.Language,
            SssInterest.Zone,
            SssInterest.Permission,
            SssInterest.Display,
        };

        foreach (SssInterest interest in personal)
        {
            SequenceEqual(new[] { 1 }, sut.ResolvePersonal(1, interest), interest + " routes only player 1");
        }

        Equal(0, sut.ResolvePersonal(3, SssInterest.Role).Count, "unsubscribed interest ignored");
        SequenceEqual(new[] { 3 }, sut.ResolvePersonal(3, SssInterest.Permission), "subscribed interest routed");
    }

    private static void PopulationBoundariesRouteOnlyIntersection()
    {
        SssInterestIndex<int> sut = new();
        foreach (int player in new[] { 1, 2, 3 })
        {
            sut.Track(player, SssInterest.All);
        }

        SequenceEqual(new[] { 1 }, sut.ResolvePopulationBoundary(new[] { 1 }, new[] { 1, 2 }), "1 to 2 former sole");
        SequenceEqual(new[] { 1 }, sut.ResolvePopulationBoundary(new[] { 1, 2 }, new[] { 1 }), "2 to 1 remaining");
        Equal(0, sut.ResolvePopulationBoundary(new[] { 1, 2 }, new[] { 1, 2, 3 }).Count, "2 to 3 no fanout");
        Equal(0, sut.ResolvePopulationBoundary(new[] { 1, 2, 3 }, new[] { 1, 2 }).Count, "3 to 2 no fanout");
        Equal(0, sut.ResolvePopulationBoundary(Array.Empty<int>(), new[] { 1 }).Count, "join handled separately");
    }

    private static void DropdownAcquisitionIsVisibleToStaging()
    {
        PersonalizedDropdownResponseLatch sut = new();
        Equal(PersonalizedDropdownResponseKind.Acquisition, sut.Observe(7),
            "first visible client value is an acquisition that a staging callback can observe");
    }

    private static void DropdownDuplicateIsSwallowed()
    {
        PersonalizedDropdownResponseLatch sut = new();
        sut.Observe(3);
        Equal(PersonalizedDropdownResponseKind.Duplicate, sut.Observe(3),
            "repeated value in one send generation is not a new action");
    }

    private static void DropdownChangeIsDistinctFromAcquisition()
    {
        PersonalizedDropdownResponseLatch sut = new();
        sut.Observe(2);
        Equal(PersonalizedDropdownResponseKind.Change, sut.Observe(9),
            "later deliberate change remains distinguishable from acquisition");
    }

    private static SssRefreshCoordinator<int, string> Coordinator(FakeClock clock, List<Sent> sent)
    {
        return new SssRefreshCoordinator<int, string>(
            () => clock.Now,
            snapshot => snapshot,
            (_, snapshot, reasons) =>
            {
                sent.Add(new Sent(snapshot, reasons.ToArray()));
                return true;
            });
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message}: expected '{expected}', got '{actual}'");
        }
    }

    private static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual, string message)
    {
        if (!expected.SequenceEqual(actual))
        {
            throw new InvalidOperationException($"{message}: expected [{string.Join(",", expected)}], got [{string.Join(",", actual)}]");
        }
    }

    private sealed class FakeClock
    {
        public double Now { get; private set; }
        public void Advance(double seconds) => Now += seconds;
        public void Set(double seconds) => Now = seconds;
    }

    private sealed record Sent(string Snapshot, IReadOnlyList<string> Reasons);
}
