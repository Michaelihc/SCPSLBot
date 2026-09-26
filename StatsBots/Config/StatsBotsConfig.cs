using System.Collections.Generic;
using System.ComponentModel;

namespace StatsBots.Config;

public sealed class StatsBotsConfig
{
    [Description("Language: empty matches a client language provider when available, 'cn' forces Chinese, and 'en' forces English. Chinese is the fallback.")]
    public string Language { get; set; } = string.Empty;

    [Description("Permission required by the statsbots RA grant/revoke/status commands.")]
    public string AdminPermission { get; set; } = "statsbots.manage";

    [Description("Points awarded for one allowed real-player-to-managed-bot kill.")]
    public long ScorePerBotKill { get; set; } = 10;

    [Description("Duplicate death callback suppression window. This only coalesces callbacks sharing the same native damage-handler identity.")]
    public int DuplicateEventWindowMilliseconds { get; set; } = 1500;

    [Description("Validated score tiers, in ascending threshold order.")]
    public List<TierConfig> Tiers { get; set; } = TierConfig.Defaults();

    [Description("Validated warmup title catalog. Codes and IDs must be unique; labels are escaped before display.")]
    public List<TitleConfig> Titles { get; set; } = TitleConfig.Defaults();

    [Description("Native Server-Specific Settings block base reserved for StatsBots.")]
    public int SssBaseId { get; set; } = 1131000;

    [Description("How often the event-driven HUD cache is checked for provider/runtime changes.")]
    public float HudPollSeconds { get; set; } = 1f;

    [Description("Grace before a scoring event may initialize a missing player_stats record. This lets StatsSystem's shared-store join hydration finish first.")]
    public int ProviderHydrationGraceSeconds { get; set; } = 5;

    [Description("How long a player is considered a beginner, including the current session.")]
    public int BeginnerThresholdSeconds { get; set; } = 3600;

    [Description("Delay after join before the once-per-session Server-Specific Settings setup notice becomes due.")]
    public int SetupNoticeDelaySeconds { get; set; } = 20;

    [Description("Setup-notice native broadcast duration.")]
    public int SetupNoticeDurationSeconds { get; set; } = 8;

    [Description("Beginner-tip interval.")]
    public int TipIntervalSeconds { get; set; } = 120;

    [Description("Beginner-tip native broadcast duration.")]
    public int TipDurationSeconds { get; set; } = 8;

    [Description("Community-notice interval.")]
    public int CommunityIntervalSeconds { get; set; } = 300;

    [Description("Community-notice native broadcast duration.")]
    public int CommunityDurationSeconds { get; set; } = 12;

    [Description("Gap retained between StatsBots-owned native broadcasts so its local queue remains bounded.")]
    public int BroadcastGapSeconds { get; set; } = 1;

    public List<LocalizedTextConfig> Tips { get; set; } = LocalizedTextConfig.DefaultTips();

    public HintDisplayConfig HintDisplay { get; set; } = new();

    [Description("Decayed combat skill rating shown beside the score tier. Only engagements with managed bots are sampled.")]
    public SkillRatingConfig SkillRating { get; set; } = new();

    internal void Validate()
    {
        ScorePerBotKill = System.Math.Max(0, ScorePerBotKill);
        DuplicateEventWindowMilliseconds = Clamp(DuplicateEventWindowMilliseconds, 100, 10000);
        SssBaseId = SssBaseId >= 1000 && SssBaseId % 1000 == 0 ? SssBaseId : 1131000;
        HudPollSeconds = System.Math.Max(0.25f, System.Math.Min(10f, HudPollSeconds));
        ProviderHydrationGraceSeconds = Clamp(ProviderHydrationGraceSeconds, 1, 60);
        BeginnerThresholdSeconds = Clamp(BeginnerThresholdSeconds, 60, 86400);
        SetupNoticeDelaySeconds = Clamp(SetupNoticeDelaySeconds, 0, 600);
        SetupNoticeDurationSeconds = Clamp(SetupNoticeDurationSeconds, 1, 60);
        TipIntervalSeconds = Clamp(TipIntervalSeconds, 30, 3600);
        TipDurationSeconds = Clamp(TipDurationSeconds, 1, 60);
        CommunityIntervalSeconds = Clamp(CommunityIntervalSeconds, 60, 7200);
        CommunityDurationSeconds = Clamp(CommunityDurationSeconds, 1, 60);
        BroadcastGapSeconds = Clamp(BroadcastGapSeconds, 0, 30);
        Tiers = Core.TierCatalog.Normalize(Tiers);
        Titles = Core.TitleCatalog.Normalize(Titles);
        if (Tips == null || Tips.Count == 0) Tips = LocalizedTextConfig.DefaultTips();
        Tips.RemoveAll(static tip => tip == null || string.IsNullOrWhiteSpace(tip.English) || string.IsNullOrWhiteSpace(tip.Chinese));
        if (Tips.Count == 0) Tips = LocalizedTextConfig.DefaultTips();
        SkillRating ??= new SkillRatingConfig();
        SkillRating.Validate();
        HintDisplay ??= new HintDisplayConfig();
        HintDisplay.GroupName = "statsbots.warmup";
        HintDisplay.TagPrefix = "statsbots.warmup.";
    }

    private static int Clamp(int value, int min, int max) => System.Math.Max(min, System.Math.Min(max, value));
}

public sealed class TierConfig
{
    public string Id { get; set; } = "recruit";
    public long MinimumScore { get; set; }
    public string English { get; set; } = "Recruit";
    public string Chinese { get; set; } = "新兵";

    public static List<TierConfig> Defaults() => new()
    {
        new() { Id = "recruit", MinimumScore = 0, English = "Recruit", Chinese = "新兵" },
        new() { Id = "operator", MinimumScore = 100, English = "Operator", Chinese = "干员" },
        new() { Id = "veteran", MinimumScore = 500, English = "Veteran", Chinese = "老兵" },
        new() { Id = "elite", MinimumScore = 1500, English = "Elite", Chinese = "精英" },
        new() { Id = "legend", MinimumScore = 5000, English = "Legend", Chinese = "传奇" },
        new() { Id = "mythic", MinimumScore = 15000, English = "Mythic", Chinese = "神话" },
        new() { Id = "immortal", MinimumScore = 40000, English = "Immortal", Chinese = "不朽" },
    };
}

public sealed class TitleConfig
{
    public string Id { get; set; } = "rookie";
    public long Code { get; set; } = 1;
    public long MinimumScore { get; set; }
    public string English { get; set; } = "Rookie";
    public string Chinese { get; set; } = "萌新";

    public static List<TitleConfig> Defaults() => new()
    {
        new() { Id = "rookie", Code = 1, MinimumScore = 0, English = "Rookie", Chinese = "萌新" },
        new() { Id = "bot-hunter", Code = 2, MinimumScore = 100, English = "Bot Hunter", Chinese = "机器人猎手" },
        new() { Id = "streak-master", Code = 3, MinimumScore = 500, English = "Streak Master", Chinese = "连杀大师" },
        new() { Id = "warmup-ace", Code = 4, MinimumScore = 1500, English = "Warmup Ace", Chinese = "热身王牌" },
        new() { Id = "bot-legend", Code = 5, MinimumScore = 5000, English = "Bot Legend", Chinese = "猎机传奇" },
        new() { Id = "mythic-gunner", Code = 6, MinimumScore = 15000, English = "Mythic Gunner", Chinese = "神话枪手" },
        new() { Id = "immortal", Code = 7, MinimumScore = 40000, English = "Immortal", Chinese = "不朽战神" },
    };
}

public sealed class SkillRatingConfig
{
    [Description("Enable combat skill sampling, persistence and the HUD rank.")]
    public bool Enabled { get; set; } = true;

    [Description("Half-life of stored combat totals in days. Recent fights dominate; inactive players lose sample confidence and eventually become unranked.")]
    public double HalfLifeDays { get; set; } = 7;

    [Description("Seconds after a bot interaction during which a player counts as in combat. Missed shots outside this window around a bot interaction are ignored.")]
    public double CombatWindowSeconds { get; set; } = 10;

    [Description("Combat seconds credited when a new engagement starts.")]
    public double CombatEpisodeSeconds { get; set; } = 3;

    [Description("Decayed firearm shots required before a player is ranked.")]
    public double MinimumShots { get; set; } = 150;

    [Description("Decayed kills plus deaths required before a player is ranked.")]
    public double MinimumEvents { get; set; } = 20;

    [Description("Decayed kills plus deaths at which rating confidence reaches 63%. Rating = 1000 x skill x confidence.")]
    public double ConfidenceEvents { get; set; } = 40;

    [Description("Weight of firearm accuracy (hitting shots / shots).")]
    public double AccuracyWeight { get; set; } = 0.35;

    [Description("Weight of kill share (kills / (kills + deaths)).")]
    public double KillShareWeight { get; set; } = 0.35;

    [Description("Weight of firearm kills per combat minute, scored as kpm / (kpm + kill_rate_reference).")]
    public double KillRateWeight { get; set; } = 0.30;

    [Description("Kills per combat minute that scores half of the kill-rate component.")]
    public double KillRateReference { get; set; } = 6;

    [Description("How often online players' pending combat samples are persisted.")]
    public int FlushSeconds { get; set; } = 30;

    [Description("Skill ranks by rating, in ascending threshold order. minimum_score is the rating threshold.")]
    public List<TierConfig> Ranks { get; set; } = DefaultRanks();

    public static List<TierConfig> DefaultRanks() => new()
    {
        new() { Id = "bronze", MinimumScore = 0, English = "Bronze", Chinese = "青铜" },
        new() { Id = "silver", MinimumScore = 300, English = "Silver", Chinese = "白银" },
        new() { Id = "gold", MinimumScore = 450, English = "Gold", Chinese = "黄金" },
        new() { Id = "platinum", MinimumScore = 550, English = "Platinum", Chinese = "铂金" },
        new() { Id = "diamond", MinimumScore = 650, English = "Diamond", Chinese = "钻石" },
        new() { Id = "master", MinimumScore = 750, English = "Master", Chinese = "大师" },
    };

    internal void Validate()
    {
        HalfLifeDays = Clamp(HalfLifeDays, 0.5, 365, 7);
        CombatWindowSeconds = Clamp(CombatWindowSeconds, 2, 60, 10);
        CombatEpisodeSeconds = Clamp(CombatEpisodeSeconds, 0.5, 30, 3);
        MinimumShots = Clamp(MinimumShots, 1, 100000, 150);
        MinimumEvents = Clamp(MinimumEvents, 1, 100000, 20);
        ConfidenceEvents = Clamp(ConfidenceEvents, 1, 100000, 40);
        AccuracyWeight = Clamp(AccuracyWeight, 0, 1, 0.35);
        KillShareWeight = Clamp(KillShareWeight, 0, 1, 0.35);
        KillRateWeight = Clamp(KillRateWeight, 0, 1, 0.30);
        if (AccuracyWeight + KillShareWeight + KillRateWeight <= 0)
        {
            AccuracyWeight = 0.35;
            KillShareWeight = 0.35;
            KillRateWeight = 0.30;
        }
        KillRateReference = Clamp(KillRateReference, 0.1, 120, 6);
        FlushSeconds = System.Math.Max(5, System.Math.Min(600, FlushSeconds));
        Ranks = Core.TierCatalog.Normalize(Ranks == null || Ranks.Count == 0 ? DefaultRanks() : Ranks);
    }

    internal double HalfLifeSeconds => HalfLifeDays * 86400d;

    private static double Clamp(double value, double min, double max, double fallback)
        => double.IsNaN(value) || double.IsInfinity(value) ? fallback : System.Math.Max(min, System.Math.Min(max, value));
}

public sealed class LocalizedTextConfig
{
    public string English { get; set; } = string.Empty;
    public string Chinese { get; set; } = string.Empty;

    public static List<LocalizedTextConfig> DefaultTips() => new()
    {
        new() { English = "Did you know you can play as an SCP? Switch to Entrance Zone.", Chinese = "你知道吗？你可以扮演 SCP。前往入口区切换。" },
        new() { English = "Open Server-Specific Settings to configure your warmup display.", Chinese = "打开服务器专属设置即可调整热身显示。" },
        new() { English = "Bot kills raise your score and streak; dying to a managed bot resets the streak.", Chinese = "击杀托管机器人会提高积分和连杀；被其击杀会重置连杀。" },
    };
}

public sealed class HintDisplayConfig
{
    public string GroupName { get; set; } = "statsbots.warmup";
    public string TagPrefix { get; set; } = "statsbots.warmup.";
    public float DefaultX { get; set; } = -800f;
    public float NoticeY { get; set; } = 735f;
    public int NoticeTextSize { get; set; } = 26;
    public float HeroY { get; set; } = 780f;
    public int HeroTextSize { get; set; } = 23;
    public float FooterY { get; set; } = 850f;
    public int FooterTextSize { get; set; } = 22;
    public float LineHeight { get; set; } = 0f;
    public bool ForceFastUpdates { get; set; } = false;
    public string KeybindTokenFormat { get; set; } = "[key:{0}]";
}
