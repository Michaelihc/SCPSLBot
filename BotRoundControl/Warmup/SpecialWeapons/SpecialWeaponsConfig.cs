#nullable enable

using System.ComponentModel;

namespace SCPSLBot.Warmup.SpecialWeapons;

/// <summary>Operator switches and balance. Visual timing stays in each weapon's tuning class.</summary>
public sealed class SpecialWeaponsConfig
{
    [Description("Master switch for RA grants and the item panel. / 总开关，同时控制 RA 发放和物品面板。")]
    public bool Enabled { get; set; } = true;

    [Description("List weapons in the player item panel. When false, only RA special_weapon grants them. / 在玩家物品面板中列出武器；为 false 时只能通过 RA special_weapon 发放。")]
    public bool AllowPlayerRequests { get; set; }

    public GravitonDisruptorConfig GravitonDisruptor { get; set; } = new();

    public GuidedRocketConfig GuidedRocket { get; set; } = new();

    public Ak12Config Ak12 { get; set; } = new();

    internal void Normalize()
    {
        GravitonDisruptor ??= new GravitonDisruptorConfig();
        GuidedRocket ??= new GuidedRocketConfig();
        Ak12 ??= new Ak12Config();
    }
}

public sealed class GravitonDisruptorConfig
{
    public bool Enabled { get; set; } = true;

    [Description("Seconds between shockwaves. Native firing is never blocked. / 冲击波间隔（秒），原版开火不受限。")]
    public float ShockwaveCooldownSeconds { get; set; } = 6f;

    public float ShockwaveRadius { get; set; } = 7f;

    [Description("Share of the victim's maximum health dealt by a shockwave. / 冲击波造成的目标最大生命值比例。")]
    public float MaxHealthDamageFraction { get; set; } = 0.9f;

    public float DamageCap { get; set; } = 800f;

    public float CoreRadius { get; set; } = 2f;

    public float CoreBonusDamage { get; set; } = 800f;
}

public sealed class GuidedRocketConfig
{
    public bool Enabled { get; set; } = true;

    [Description("Rockets in one launcher. The spent launcher is removed. / 每具发射器的火箭数，打空后移除。")]
    public int RocketsPerLauncher { get; set; } = 3;

    public float ReloadSeconds { get; set; } = 2.4f;

    public float LockRange { get; set; } = 60f;

    public float LockConeHalfAngleDegrees { get; set; } = 12f;

    public float MissileSpeed { get; set; } = 22f;

    public float TurnRateDegreesPerSecond { get; set; } = 95f;

    public float MaxFlightSeconds { get; set; } = 6f;

    public float HitRadius { get; set; } = 0.45f;

    [Description("Blast damage at the centre, falling to zero at the splash radius. / 爆心伤害，至溅射半径衰减为零。")]
    public float CenterDamage { get; set; } = 500f;

    public float SplashRadius { get; set; } = 4.5f;
}

public sealed class Ak12Config
{
    public bool Enabled { get; set; } = true;

    [Description("Multiplier on every hit. The default keeps damage per second level with the AK. / 每次命中的伤害倍率，默认使每秒伤害与 AK 持平。")]
    public float DamageMultiplier { get; set; } = 0.88f;

    [Description("Rounds added to the owner's reserve on grant. / 发放时补充的备用弹药。")]
    public int ReserveAmmo { get; set; } = 120;
}
