using SCPSLBot.Warmup.Controls;

namespace SCPSLBot.PolicyTests;

public sealed class Scp018CooldownTests
{
    [Fact]
    public void BallGrantBlocksRefillsAcrossLivesAndSharesHighImpactCooldown()
    {
        var config = WarmupControlsConfig.CreateDefault();
        Assert.True(ItemCatalog.TryCreate(config.Items, out var catalog, out var errors),
            string.Join(Environment.NewLine, errors));
        var ball = catalog!.Entries["native.scp018"];
        var grenade = catalog.Entries["high-impact.grenade-he"];
        Assert.Equal(60d, ball.CooldownSeconds);
        Assert.Equal(60d, ball.SharedCooldownSeconds);
        Assert.Equal("high-impact", ball.SharedCooldownGroup);
        Assert.Equal(1, ball.PerLifeLimit);
        Assert.True(ball.PerRoundLimit > 0);

        var clock = new Clock();
        var ledger = new CooldownLedger(clock);
        ledger.BeginRound("round");
        Assert.True(ledger.TryReserve("round", "player@steam", ball, "life1", out var reservation, out _));
        using (reservation!) reservation!.Commit();

        Assert.Equal(ControlResultCode.ItemCooldown,
            ledger.GetAvailability("round", "player@steam", ball, "life2").Code);
        Assert.Equal(ControlResultCode.GroupCooldown,
            ledger.GetAvailability("round", "player@steam", grenade, "life2").Code);
        clock.Timestamp = 60_000;
        Assert.Equal(ControlResultCode.LifeLimitReached,
            ledger.GetAvailability("round", "player@steam", ball, "life1").Code);
        Assert.True(ledger.GetAvailability("round", "player@steam", ball, "life2").Succeeded);
    }

    private sealed class Clock : IMonotonicClock
    {
        public long Timestamp { get; set; }
        public long Frequency => 1000;
    }
}
