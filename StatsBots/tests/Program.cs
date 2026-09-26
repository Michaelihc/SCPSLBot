using System;
using System.Collections.Generic;
using System.Threading;
using StatsBots.Config;
using StatsBots.Core;
using StatsBots.Integration;

internal static class Program
{
    private static int _passed;

    public static int Main()
    {
        Run("identity requires a full authenticated UserId", Identity);
        Run("exact scoring matrix and score clamp", Scoring);
        Run("duplicate callback suppression is bounded by identity and time", DuplicateSuppression);
        Run("tier/title catalog validation and removed entries", Catalogs);
        Run("playtime boundary and bounded notice cadence", Cadence);
        Run("tip rotation has no immediate repeats", TipCadence);
        Run("provider loading/failure never becomes a fake zero", ProviderFailure);
        Run("skill decay, combat-only sampling and rating", Skill);
        Console.WriteLine($"StatsBots pure tests: {_passed}/8 passed");
        return 0;
    }

    private static void Identity()
    {
        False(AuthenticatedIdentity.IsFullUserId(null));
        False(AuthenticatedIdentity.IsFullUserId(""));
        False(AuthenticatedIdentity.IsFullUserId("ID_Dummy"));
        False(AuthenticatedIdentity.IsFullUserId("76561198000000000"));
        False(AuthenticatedIdentity.IsFullUserId("a@@steam"));
        True(AuthenticatedIdentity.IsFullUserId("76561198000000000@steam"));
        True(AuthenticatedIdentity.IsFullUserId("user@northwood"));
        True(StatsKeys.IsWarmupKey(StatsKeys.BotKills));
        False(StatsKeys.IsWarmupKey(StatsKeys.TotalPlayTime));
    }

    private static void Scoring()
    {
        Equal(new ScoreMutation(1, 0, 10, 1, false), ScoringMatrix.Evaluate(
            new ScoringInput(CombatActorKind.RealAuthenticated, CombatActorKind.ManagedBot, false, false), 10));
        Equal(new ScoreMutation(0, 1, 0, 0, true), ScoringMatrix.Evaluate(
            new ScoringInput(CombatActorKind.ManagedBot, CombatActorKind.RealAuthenticated, false, false), 10));
        False(ScoringMatrix.Evaluate(new ScoringInput(CombatActorKind.ManagedBot, CombatActorKind.ManagedBot, false, false), 10).HasChanges);
        False(ScoringMatrix.Evaluate(new ScoringInput(CombatActorKind.RealAuthenticated, CombatActorKind.RealAuthenticated, false, false), 10).HasChanges);
        False(ScoringMatrix.Evaluate(new ScoringInput(CombatActorKind.Other, CombatActorKind.ManagedBot, false, false), 10).HasChanges);
        False(ScoringMatrix.Evaluate(new ScoringInput(CombatActorKind.ManagedBot, CombatActorKind.Other, false, false), 10).HasChanges);
        False(ScoringMatrix.Evaluate(new ScoringInput(CombatActorKind.RealAuthenticated, CombatActorKind.ManagedBot, true, false), 10).HasChanges);
        False(ScoringMatrix.Evaluate(new ScoringInput(CombatActorKind.RealAuthenticated, CombatActorKind.ManagedBot, false, true), 10).HasChanges);
        Equal(new ScoreMutation(1, 0, 0, 1, false), ScoringMatrix.Evaluate(
            new ScoringInput(CombatActorKind.RealAuthenticated, CombatActorKind.ManagedBot, false, false), -1));
        Equal(0L, ScoringMatrix.ClampScore(4, -10));
        Equal(long.MaxValue, ScoringMatrix.ClampScore(long.MaxValue - 2, 10));
    }

    private static void DuplicateSuppression()
    {
        var dedup = new DeathEventDeduplicator(100);
        var one = new DeathFingerprint(7, 42);
        True(dedup.TryAccept(one, 1000));
        False(dedup.TryAccept(one, 1050));
        True(dedup.TryAccept(new DeathFingerprint(7, 43), 1050));
        True(dedup.TryAccept(one, 1101));

        var burst = new DeathEventDeduplicator(10_000, 512);
        for (uint i = 0; i < 600; i++) True(burst.TryAccept(new DeathFingerprint(i, (int)i), 2_000 + i));
        False(burst.TryAccept(new DeathFingerprint(599, 599), 2_700));
    }

    private static void Catalogs()
    {
        List<TierConfig> tiers = TierCatalog.Normalize(new[]
        {
            new TierConfig { Id = "elite", MinimumScore = 100, English = "Elite", Chinese = "精英" },
            new TierConfig { Id = "elite", MinimumScore = 200, English = "Duplicate", Chinese = "重复" },
        });
        Equal(0L, tiers[0].MinimumScore);
        Equal("elite", TierCatalog.Resolve(tiers, 100).Id);
        Equal(100L, TierCatalog.NextThreshold(tiers, 99));
        List<TierConfig> collidingBaseline = TierCatalog.Normalize(new[]
        {
            new TierConfig { Id = "recruit", MinimumScore = 100, English = "Custom", Chinese = "自定义" },
        });
        Equal(2, collidingBaseline.Count);
        False(string.Equals(collidingBaseline[0].Id, collidingBaseline[1].Id, StringComparison.OrdinalIgnoreCase));

        List<TitleConfig> titles = TitleCatalog.Normalize(new[]
        {
            new TitleConfig { Id = "one", Code = 1, MinimumScore = 10, English = "One", Chinese = "一" },
            new TitleConfig { Id = "bad id", Code = 2, English = "Bad", Chinese = "坏" },
            new TitleConfig { Id = "duplicate-code", Code = 1, English = "Dup", Chinese = "重" },
        });
        Equal(1, titles.Count);
        False(TitleCatalog.IsUnlocked(titles[0], 9, 0));
        True(TitleCatalog.IsUnlocked(titles[0], 9, 1));
        False(TitleCatalog.IsUnlocked(titles[0], 99, -1));
        Equal(null, TitleCatalog.ByCode(titles, 999));
    }

    private static void Skill()
    {
        // Half-life decay and fixed-point storage.
        var sample = new SkillSample(100, 20, 300, 1000, 600);
        SkillSample halved = SkillMath.Decay(sample, 7 * 86400, 7 * 86400);
        True(Math.Abs(halved.Kills - 50) < 1e-9 && Math.Abs(halved.CombatSeconds - 300) < 1e-9);
        Equal(12345L, SkillMath.ToFixed(12.345));
        var stored = new Dictionary<string, long>();
        foreach (var entry in SkillKeys.Write(sample, 1000)) stored[entry.Key] = entry.Value;
        SkillSample later = SkillKeys.Current(k => stored.TryGetValue(k, out long v) ? v : 0, 1000 + 14 * 86400, 7 * 86400);
        True(Math.Abs(later.Shots - 250) < 0.01);

        // Idle stray shots never count; misses just before a bot interaction do; PvP suspends sampling.
        var sampler = new CombatSampler(10, 3);
        sampler.Shot(0, false);
        sampler.Shot(1, false);
        False(sampler.HasPending);
        sampler.Shot(100, false);
        sampler.BotInteraction(105);
        Equal(1d, sampler.Pending.Shots);
        True(Math.Abs(sampler.Pending.CombatSeconds - 8) < 1e-9); // 3s episode start + 5s gap
        sampler.Shot(106, true);
        sampler.Kill(106);
        Equal(2d, sampler.Pending.Shots);
        Equal(1d, sampler.Pending.Hits);
        Equal(1d, sampler.Pending.Kills);
        sampler.Shot(200, false); // long after combat, never confirmed
        sampler.PlayerVersusPlayer(300);
        sampler.Death(305);
        sampler.Shot(306, true);
        SkillSample taken = sampler.TakePending();
        Equal(2d, taken.Shots);
        Equal(0d, taken.Deaths);
        False(sampler.HasPending);

        // Placement gate, confidence and bounded components.
        var config = new SkillRatingConfig();
        config.Validate();
        False(SkillMath.Evaluate(new SkillSample(5, 1, 40, 100, 60), config).Ranked);
        SkillResult shotsOnly = SkillMath.Evaluate(new SkillSample(0, 0, 6, 40, 9), config);
        True(!shotsOnly.Ranked && shotsOnly.PlacementProgress > 0.1 && shotsOnly.PlacementProgress < 0.2);
        False(SkillMath.Evaluate(new SkillSample(40, 10, 100, 149, 600), config).Ranked);
        SkillResult strong = SkillMath.Evaluate(new SkillSample(400, 40, 1200, 2000, 3600), config);
        SkillResult weak = SkillMath.Evaluate(new SkillSample(40, 40, 200, 2000, 3600), config);
        True(strong.Ranked && weak.Ranked && strong.Rating > weak.Rating && strong.Rating <= 1000);
        SkillResult faded = SkillMath.Evaluate(SkillMath.Decay(new SkillSample(400, 40, 1200, 2000, 3600), 21 * 86400, config.HalfLifeSeconds), config);
        True(faded.Rating < strong.Rating);
        Equal("master", TierCatalog.Resolve(config.Ranks, 800).Id);
    }

    private static void Cadence()
    {
        TimeSpan threshold = TimeSpan.FromHours(1);
        True(BeginnerEligibility.IsEligible(TimeSpan.FromMinutes(59) + TimeSpan.FromSeconds(59), TimeSpan.Zero, threshold));
        False(BeginnerEligibility.IsEligible(TimeSpan.FromMinutes(59) + TimeSpan.FromSeconds(59), TimeSpan.FromSeconds(1), threshold));
        False(BeginnerEligibility.IsEligible(TimeSpan.FromMinutes(60) + TimeSpan.FromSeconds(1), TimeSpan.Zero, threshold));
        False(BeginnerEligibility.IsEligible(null, TimeSpan.Zero, threshold));

        var tracker = new VerifiedPlaytimeTracker(0);
        Equal(TimeSpan.FromSeconds(3595), tracker.Observe(TimeSpan.FromSeconds(3590), 5));
        Equal(TimeSpan.FromSeconds(3600), tracker.Observe(TimeSpan.FromSeconds(3598), 10));
        Equal(TimeSpan.FromSeconds(3601), tracker.Observe(TimeSpan.FromSeconds(3598), 11));

        var cadence = new NoticeCadence(0, 20, 120, 300);
        Equal(NoticeKind.Community, cadence.TakeNext(0, true, true, true));
        cadence.MarkOccupied(0, 12, 1);
        Equal(NoticeKind.None, cadence.TakeNext(12, true, true, true));
        Equal(NoticeKind.Setup, cadence.TakeNext(20, true, true, true));
        cadence.MarkOccupied(20, 8, 1);
        Equal(NoticeKind.None, cadence.TakeNext(21, true, true, true));
        Equal(NoticeKind.Tip, cadence.TakeNext(120, true, true, true));
        cadence.MarkOccupied(120, 8, 1);
        Equal(NoticeKind.None, cadence.TakeNext(121, false, true, true));
    }

    private static void TipCadence()
    {
        var shuffle = new TipShuffle("7656119@steam", 3);
        int first = shuffle.Next();
        int second = shuffle.Next();
        int third = shuffle.Next();
        False(first == second);
        False(second == third);
        Equal(3, new HashSet<int> { first, second, third }.Count);
        int fourth = shuffle.Next();
        False(third == fourth);
        True(fourth >= 0 && fourth < 3);
    }

    private static void ProviderFailure()
    {
        var adapter = new StatsSystemAdapter();
        global::StatsSystem.StatsSystemPlugin.Stats = null;
        Equal(ProviderState.Loading, adapter.State);
        Equal(ProviderState.Loading, adapter.TryRead("7656119@steam", new[] { StatsKeys.Score }, out StatsRecord? loading));
        Equal(null, loading);

        global::StatsSystem.StatsSystemPlugin.Stats = new global::StatsSystem.ThrowingProvider();
        Equal(ProviderState.Unavailable, adapter.TryRead("7656119@steam", new[] { StatsKeys.Score }, out StatsRecord? failed));
        Equal(null, failed);

        var readyProvider = new global::StatsSystem.FakeProvider();
        readyProvider.Record.Counters[StatsKeys.Score] = 27;
        readyProvider.Record.Durations[StatsKeys.TotalPlayTime] = TimeSpan.FromMinutes(42);
        global::StatsSystem.StatsSystemPlugin.Stats = readyProvider;
        adapter = new StatsSystemAdapter();
        Equal(ProviderState.Ready, adapter.TryRead("7656119@steam", new[] { StatsKeys.Score }, out StatsRecord? ready));
        Equal(27L, ready!.Counter(StatsKeys.Score));
        Equal(TimeSpan.FromMinutes(42), ready.TotalPlayTime);
        readyProvider.Record.Durations.Remove(StatsKeys.TotalPlayTime);
        Equal(ProviderState.Ready, adapter.TryRead("7656119@steam", new[] { StatsKeys.Score }, out StatsRecord? missingPlaytime));
        Equal(null, missingPlaytime!.TotalPlayTime);
        Equal(ProviderState.Ready, adapter.Increment("7656119@steam", StatsKeys.BotKills, 10));
        Equal(ProviderState.Ready, adapter.Increment("7656119@steam", StatsKeys.BotDeaths, 2));
        adapter = new StatsSystemAdapter();
        Equal(ProviderState.Ready, adapter.TryRead("7656119@steam", new[] { StatsKeys.BotKills, StatsKeys.BotDeaths }, out StatsRecord? afterReload));
        Equal(10L, afterReload!.Counter(StatsKeys.BotKills));
        Equal(2L, afterReload.Counter(StatsKeys.BotDeaths));
        int callsBeforeDummy = readyProvider.MutationCalls;
        Equal(ProviderState.Unavailable, adapter.Increment("ID_Dummy", StatsKeys.Score, 1));
        Equal(callsBeforeDummy, readyProvider.MutationCalls);

        var hydratingProvider = new global::StatsSystem.HydratingProvider();
        global::StatsSystem.StatsSystemPlugin.Stats = hydratingProvider;
        adapter = new StatsSystemAdapter();
        Equal(ProviderState.Loading, adapter.EnsureOfflineHydrated("offline@steam"));
        True(SpinWait.SpinUntil(
            () => adapter.EnsureOfflineHydrated("offline@steam") == ProviderState.Ready,
            TimeSpan.FromSeconds(2)));
        True(hydratingProvider.Hydrated);
    }

    private static void Run(string name, Action test)
    {
        test();
        _passed++;
        Console.WriteLine("PASS " + name);
    }
    private static void True(bool value) { if (!value) throw new InvalidOperationException("Expected true."); }
    private static void False(bool value) { if (value) throw new InvalidOperationException("Expected false."); }
    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}; got {actual}.");
    }
}

namespace StatsSystem.API
{
    public sealed class PlayerStats
    {
        public Dictionary<string, long> Counters { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, TimeSpan> Durations { get; } = new(StringComparer.Ordinal);
        public long GetCounter(string key) => Counters.TryGetValue(key, out long value) ? value : 0;
        public TimeSpan GetDuration(string key) => Durations.TryGetValue(key, out TimeSpan value) ? value : TimeSpan.Zero;
    }
}

namespace StatsSystem
{
    using StatsSystem.API;

    internal static class StatsSystemPlugin
    {
        public static object? Stats { get; set; }
    }

    internal class FakeProvider
    {
        public PlayerStats Record { get; } = new();
        public int MutationCalls { get; private set; }
        public virtual bool TryGetStats(string userId, out PlayerStats stats, string? file = null) { stats = Record; return true; }
        public virtual bool TryGetOrCreateStats(string userId, out PlayerStats stats, string? file = null) { stats = Record; return true; }
        public virtual void IncrementCounter(string userId, string key, long amount, string? file = null) { MutationCalls++; Record.Counters[key] = Record.GetCounter(key) + amount; }
        public virtual void SetCounter(string userId, string key, long value, string? file = null) { MutationCalls++; Record.Counters[key] = value; }
        public virtual bool DeleteStatKey(string userId, string key, string? file = null) { MutationCalls++; return Record.Counters.Remove(key); }
        public virtual void Save() { }
        internal void EnsureHydrated(string userId, string? file = null) { }
    }

    internal sealed class ThrowingProvider : FakeProvider
    {
        public override bool TryGetStats(string userId, out PlayerStats stats, string? file = null)
        {
            stats = null!;
            throw new InvalidOperationException("simulated provider failure");
        }
    }

    internal sealed class HydratingProvider : FakeProvider
    {
        private volatile bool _hydrated;
        public bool Hydrated => _hydrated;
        public override bool TryGetStats(string userId, out PlayerStats stats, string? file = null)
        {
            stats = Record;
            return _hydrated;
        }
        internal new void EnsureHydrated(string userId, string? file = null) => _hydrated = true;
    }
}
