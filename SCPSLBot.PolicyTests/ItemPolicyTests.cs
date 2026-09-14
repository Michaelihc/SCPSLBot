using System.Threading;
using SCPSLBot.Warmup.Controls;

namespace SCPSLBot.PolicyTests;

public sealed class ItemPolicyTests
{
    private const string UserA = "76561198000000001@steam";
    private const string UserB = "76561198000000002@steam";

    [Fact]
    public void SuccessfulGrantCallsAddOnceAndCommitsMonotonicCooldown()
    {
        TestFixture fixture = CreateFixture(cooldownSeconds: 4.25);
        var context = new FakeItemContext(UserA);

        ControlResult first = fixture.Policy.TryGrant(context, Request(UserA));
        ControlResult second = fixture.Policy.TryGrant(context, Request(UserA));

        Assert.True(first.Succeeded);
        Assert.Equal(1, context.AddCalls);
        Assert.Equal(ControlResultCode.ItemCooldown, second.Code);
        Assert.Equal(5, second.RoundedRemainingSeconds);

        fixture.Clock.Advance(4.25);
        Assert.True(fixture.Policy.TryGrant(context, Request(UserA)).Succeeded);
        Assert.Equal(2, context.AddCalls);
    }

    [Fact]
    public void FailedOrWrongNativeItemConsumesNoCooldownOrCounter()
    {
        TestFixture fixture = CreateFixture(cooldownSeconds: 30, perRoundLimit: 1);
        var context = new FakeItemContext(UserA) { AddSucceeds = false };

        Assert.Equal(ControlResultCode.ItemGrantFailed, fixture.Policy.TryGrant(context, Request(UserA)).Code);
        context.AddSucceeds = true;
        Assert.True(fixture.Policy.TryGrant(context, Request(UserA)).Succeeded);
        Assert.Equal(2, context.AddCalls);
    }

    [Fact]
    public void ReconnectWithSameFullUserIdKeepsCooldownAndRoundCap()
    {
        TestFixture fixture = CreateFixture(cooldownSeconds: 3, perRoundLimit: 1);
        var firstConnection = new FakeItemContext(UserA);
        Assert.True(fixture.Policy.TryGrant(firstConnection, Request(UserA)).Succeeded);

        var reconnect = new FakeItemContext(UserA) { LifeId = "different-life" };
        Assert.Equal(ControlResultCode.ItemCooldown, fixture.Policy.TryGrant(reconnect, Request(UserA)).Code);
        fixture.Clock.Advance(3);
        Assert.Equal(ControlResultCode.RoundLimitReached, fixture.Policy.TryGrant(reconnect, Request(UserA)).Code);

        var reusedSlotDifferentUser = new FakeItemContext(UserB);
        Assert.True(fixture.Policy.TryGrant(reusedSlotDifferentUser, Request(UserB)).Succeeded);
    }

    [Fact]
    public void DeathSpectatorRoleAndUiTransitionsHaveNoLedgerResetPath()
    {
        TestFixture fixture = CreateFixture(cooldownSeconds: 0, perLifeLimit: 1);
        var context = new FakeItemContext(UserA);
        Assert.True(fixture.Policy.TryGrant(context, Request(UserA)).Succeeded);

        context.IsAlive = false;
        Assert.Equal(ControlResultCode.NotAlive, fixture.Policy.TryGrant(context, Request(UserA)).Code);
        context.IsAlive = true;
        context.ExactRoleId = "Scientist";
        Assert.Equal(ControlResultCode.LifeLimitReached, fixture.Policy.TryGrant(context, Request(UserA)).Code);

        context.LifeId = "new-native-life-id";
        Assert.True(fixture.Policy.TryGrant(context, Request(UserA)).Succeeded);
    }

    [Fact]
    public void SharedGroupCooldownBlocksDifferentCatalogEntry()
    {
        FakeClock clock = new();
        ItemCatalog catalog = Catalog(
            Entry("grenade.he", "GrenadeHE", group: "high-impact", groupCooldown: 10),
            Entry("weapon.hid", "MicroHID", group: "high-impact", groupCooldown: 10));
        var ledger = new CooldownLedger(clock);
        ledger.BeginRound("round-1");
        var policy = new ItemGrantPolicy(catalog, ledger, new PerUserRequestGuard());
        var context = new FakeItemContext(UserA);

        Assert.True(policy.TryGrant(context, new ItemGrantRequest(UserA, "grenade.he")).Succeeded);
        ControlResult blocked = policy.TryGrant(context, new ItemGrantRequest(UserA, "weapon.hid"));

        Assert.Equal(ControlResultCode.GroupCooldown, blocked.Code);
        Assert.Equal("high-impact", blocked.Detail);
        Assert.Equal(1, context.AddCalls);
    }

    [Fact]
    public async Task ConcurrentRequestsProduceAtMostOneAddAndOneCommit()
    {
        TestFixture fixture = CreateFixture(cooldownSeconds: 30);
        using var enteredAdd = new ManualResetEventSlim(false);
        using var releaseAdd = new ManualResetEventSlim(false);
        var context = new FakeItemContext(UserA)
        {
            OnAdd = () =>
            {
                enteredAdd.Set();
                Assert.True(releaseAdd.Wait(TimeSpan.FromSeconds(5)));
            },
        };

        Task<ControlResult> firstTask = Task.Run(() => fixture.Policy.TryGrant(context, Request(UserA)));
        Assert.True(enteredAdd.Wait(TimeSpan.FromSeconds(5)));
        ControlResult concurrent = fixture.Policy.TryGrant(context, Request(UserA));
        releaseAdd.Set();
        ControlResult first = await firstTask;

        Assert.True(first.Succeeded);
        Assert.Equal(ControlResultCode.ConcurrentRequest, concurrent.Code);
        Assert.Equal(1, context.AddCalls);
        Assert.Equal(
            ControlResultCode.ItemCooldown,
            fixture.Policy.EvaluateAvailability(context, Request(UserA)).Code);
    }

    [Fact]
    public void NewRoundClearsOnlyRoundOwnedLedgerState()
    {
        TestFixture fixture = CreateFixture(cooldownSeconds: 60, perRoundLimit: 1);
        var context = new FakeItemContext(UserA);
        Assert.True(fixture.Policy.TryGrant(context, Request(UserA)).Succeeded);

        fixture.Ledger.BeginRound("round-2");
        context.RoundId = "round-2";

        Assert.True(fixture.Policy.TryGrant(context, Request(UserA)).Succeeded);
    }

    [Theory]
    [InlineData("unauthenticated", ControlResultCode.Unauthenticated)]
    [InlineData("dummy", ControlResultCode.NotRealPlayer)]
    [InlineData("unavailable", ControlResultCode.PlayerUnavailable)]
    [InlineData("not-warmup", ControlResultCode.WarmupInactive)]
    [InlineData("dead", ControlResultCode.NotAlive)]
    [InlineData("wrong-role", ControlResultCode.RoleCannotRequestItem)]
    [InlineData("wrong-zone", ControlResultCode.ZoneCannotRequestItem)]
    [InlineData("inventory-full", ControlResultCode.InventoryFull)]
    [InlineData("wrong-preset", ControlResultCode.ItemNotAllowedByPreset)]
    public void ExecutionRevalidatesEveryAuthoritativeState(string mutation, ControlResultCode expected)
    {
        TestFixture fixture = CreateFixture();
        var context = new FakeItemContext(UserA);
        switch (mutation)
        {
            case "unauthenticated": context.IsAuthenticated = false; break;
            case "dummy": context.IsRealPlayer = false; break;
            case "unavailable": context.IsPlayerAvailable = false; break;
            case "not-warmup": context.IsWarmupActive = false; break;
            case "dead": context.IsAlive = false; break;
            case "wrong-role": context.ExactRoleId = "Scp173"; break;
            case "wrong-zone": context.ExactZoneId = "Surface"; break;
            case "inventory-full": context.HasInventoryCapacity = false; break;
            case "wrong-preset": context.AllowedByPreset = false; break;
        }

        Assert.Equal(expected, fixture.Policy.TryGrant(context, Request(UserA)).Code);
        Assert.Equal(0, context.AddCalls);
    }

    [Fact]
    public void RoundGenerationMismatchRejectsStaleCallback()
    {
        TestFixture fixture = CreateFixture();
        var context = new FakeItemContext(UserA) { RoundId = "stale-round" };

        Assert.Equal(
            ControlResultCode.RoundStateUnavailable,
            fixture.Policy.TryGrant(context, Request(UserA)).Code);
        Assert.Equal(0, context.AddCalls);
    }

    [Fact]
    public void CatalogRejectsDuplicateAndImplicitRoleOrZoneAuthority()
    {
        ItemCatalogEntryConfig duplicate = Entry("medkit", "Medkit");
        bool duplicateValid = ItemCatalog.TryCreate(
            new[] { duplicate, Entry("medkit", "SCP500") },
            out _,
            out IReadOnlyList<string> duplicateErrors);
        ItemCatalogEntryConfig implicitAuthority = Entry("implicit", "Medkit");
        implicitAuthority.AllowedRoleIds.Clear();
        bool implicitValid = ItemCatalog.TryCreate(
            new[] { implicitAuthority },
            out _,
            out IReadOnlyList<string> implicitErrors);

        Assert.False(duplicateValid);
        Assert.Contains(duplicateErrors, error => error.Contains("duplicates", StringComparison.Ordinal));
        Assert.False(implicitValid);
        Assert.Contains(implicitErrors, error => error.Contains("AllowedRoleIds", StringComparison.Ordinal));
    }

    [Fact]
    public void CatalogPreservesBilingualLabelsAndFallsBackToStableId()
    {
        ItemCatalogEntryConfig localized = Entry("medical.medkit", "Medkit");
        localized.EnglishLabel = "Medkit";
        localized.ChineseLabel = "医疗包";
        ItemCatalogEntryConfig fallback = Entry("utility.coin", "Coin");
        ItemCatalog catalog = Catalog(localized, fallback);

        Assert.Equal("Medkit", catalog.Entries["medical.medkit"].EnglishLabel);
        Assert.Equal("医疗包", catalog.Entries["medical.medkit"].ChineseLabel);
        Assert.Equal("utility.coin", catalog.Entries["utility.coin"].EnglishLabel);
        Assert.Equal("utility.coin", catalog.Entries["utility.coin"].ChineseLabel);
    }

    [Fact]
    public void ClassicDefaultCatalogIsCompleteLocalizedAndKeepsHighImpactCooldowns()
    {
        WarmupControlsConfig config = WarmupControlsConfig.CreateDefault();
        ItemCatalog catalog = Catalog(config.Items.ToArray());

        Assert.Equal(19, config.Roles.AllowedRegularRoleIds.Count);
        Assert.Contains("Scp079", config.Roles.AllowedRegularRoleIds);
        Assert.Contains("Scp3114", config.Roles.AllowedRegularRoleIds);
        Assert.DoesNotContain("Tutorial", config.Roles.AllowedRegularRoleIds);
        Assert.Equal(69, config.Items.Count);
        Assert.DoesNotContain(config.Items, item => item.ItemId.Contains("Debug", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(new[] { "surface", "pvpve", "lcz" }, config.Presets.Select(preset => preset.Id));
        Assert.Equal("NtfPrivate", config.Presets.Single(preset => preset.Id == "surface").SpawnAnchorRoleId);
        Assert.Equal("Scp939", config.Presets.Single(preset => preset.Id == "pvpve").SpawnAnchorRoleId);
        Assert.Equal("ClassD", config.Presets.Single(preset => preset.Id == "lcz").SpawnAnchorRoleId);
        Assert.All(config.Presets, preset =>
        {
            Assert.True(preset.AllowScpPlayers);
            Assert.Equal(19, preset.AllowedRoleIds.Count);
            Assert.Equal(config.Items.Count, preset.AllowedItemIds.Count);
        });
        Assert.Equal("high-impact", catalog.Entries["high-impact.grenade-he"].SharedCooldownGroup);
        Assert.Equal("high-impact", catalog.Entries["high-impact.micro-hid"].SharedCooldownGroup);
    }

    private static ItemGrantRequest Request(string userId) => new(userId, "medical.medkit");

    private static TestFixture CreateFixture(
        double cooldownSeconds = 0,
        int perLifeLimit = 0,
        int perRoundLimit = 0)
    {
        FakeClock clock = new();
        ItemCatalog catalog = Catalog(Entry(
            "medical.medkit",
            "Medkit",
            cooldownSeconds,
            perLifeLimit,
            perRoundLimit));
        var ledger = new CooldownLedger(clock);
        ledger.BeginRound("round-1");
        return new TestFixture(
            clock,
            ledger,
            new ItemGrantPolicy(catalog, ledger, new PerUserRequestGuard()));
    }

    private static ItemCatalog Catalog(params ItemCatalogEntryConfig[] entries)
    {
        Assert.True(ItemCatalog.TryCreate(entries, out ItemCatalog? catalog, out IReadOnlyList<string> errors),
            string.Join(Environment.NewLine, errors));
        return Assert.IsType<ItemCatalog>(catalog);
    }

    private static ItemCatalogEntryConfig Entry(
        string id,
        string item,
        double cooldown = 0,
        int perLife = 0,
        int perRound = 0,
        string group = "",
        double groupCooldown = 0) =>
        new()
        {
            Id = id,
            ItemId = item,
            CooldownSeconds = cooldown,
            PerLifeLimit = perLife,
            PerRoundLimit = perRound,
            SharedCooldownGroup = group,
            SharedCooldownSeconds = groupCooldown,
            AllowedRoleIds = new List<string> { "ClassD", "Scientist" },
            AllowedZoneIds = new List<string> { "LightContainment" },
        };

    private sealed record TestFixture(FakeClock Clock, CooldownLedger Ledger, ItemGrantPolicy Policy);

    private sealed class FakeClock : IMonotonicClock
    {
        public long Timestamp { get; private set; }

        public long Frequency => 1_000;

        public void Advance(double seconds) => Timestamp += (long)Math.Round(seconds * Frequency);
    }

    private sealed class FakeItemContext : IItemGrantContext
    {
        private int addCalls;

        public FakeItemContext(string userId) => FullUserId = userId;

        public string FullUserId { get; set; }

        public bool IsAuthenticated { get; set; } = true;

        public bool IsRealPlayer { get; set; } = true;

        public bool IsPlayerAvailable { get; set; } = true;

        public bool IsWarmupActive { get; set; } = true;

        public bool IsAlive { get; set; } = true;

        public string ExactRoleId { get; set; } = "ClassD";

        public string ExactZoneId { get; set; } = "LightContainment";

        public string LifeId { get; set; } = "life-1";

        public string RoundId { get; set; } = "round-1";

        public bool HasInventoryCapacity { get; set; } = true;

        public bool AllowedByPreset { get; set; } = true;

        public bool AddSucceeds { get; set; } = true;

        public Action? OnAdd { get; set; }

        public int AddCalls => Volatile.Read(ref addCalls);

        public bool IsAllowedByActivePreset(ItemCatalogEntry entry) => AllowedByPreset;

        public bool TryAddExactItemOnce(ItemCatalogEntry entry)
        {
            Interlocked.Increment(ref addCalls);
            OnAdd?.Invoke();
            return AddSucceeds;
        }
    }
}
