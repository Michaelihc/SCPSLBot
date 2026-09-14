using System.ComponentModel;

namespace ScpslPluginStarter;

public sealed class WarmupSafezoneConfig
{
    [Description("Empty matches the client when available; this LabAPI version exposes no client-language property, so empty falls back to Chinese. Use cn or en to force a language.")]
    public string Language { get; set; } = "";

    public bool Enabled { get; set; } = true;

    public bool Scp914SafezoneEnabled { get; set; } = true;

    public string Scp914SafezonePanelTextEnglish { get; set; } = "SAFE ZONE\nDAMAGE BLOCKED";

    public string Scp914SafezonePanelTextChinese { get; set; } = "安全区\n禁止造成或受到伤害";

    [Description("Shows the configured non-collidable Surface boundary wall and the SCP-914 gate panel.")]
    public bool SafezoneVisualsEnabled { get; set; } = true;

    [Description("Configured Surface safezone threshold on surface_escape_safezone_axis. Native Map.EscapeZones remain an additional fallback.")]
    public float SurfaceEscapeSafezoneMaxZ { get; set; } = -17f;

    [Description("Axis used by the configured Surface safezone threshold: x, y, or z.")]
    public string SurfaceEscapeSafezoneAxis { get; set; } = "z";

    [Description("When true, coordinates at or below the threshold are safe; otherwise coordinates at or above it are safe.")]
    public bool SurfaceEscapeSafezoneLessThan { get; set; }

    [Description("Minimum Surface X coordinate required for configured safezone and blocker membership.")]
    public float SurfaceEscapeSafezoneMinX { get; set; } = 91f;

    public bool SurfaceEscapeSafezoneHealthDrainEnabled { get; set; }

    public float SurfaceEscapeSafezoneHealthDrainPercentPerSecond { get; set; } = 0.5f;

    public bool SurfaceEscapeSafezoneHealthDrainWarningEnabled { get; set; } = true;

    public string SurfaceEscapeSafezoneHealthDrainWarningTextEnglish { get; set; } = "<color=#ff6060><b>SAFEZONE DRAIN</b> · {percent}% max HP/s.</color>";

    public string SurfaceEscapeSafezoneHealthDrainWarningTextChinese { get; set; } = "<color=#ff6060><b>安全区损耗</b> · 每秒 {percent}% 最大生命值</color>";

    [Description("Legacy Chinese warning retained for config compatibility. Used when the Chinese-specific value is empty.")]
    public string SurfaceEscapeSafezoneHealthDrainWarningText { get; set; } = "<color=#ff6060>地表安全区内每秒损失 {percent}% 最大生命值</color>";

    public bool SurfaceEscapeBlockerEnabled { get; set; } = true;

    [Description("Depth, in metres, of the blocker band immediately outside the configured Surface safezone threshold.")]
    public float SurfaceEscapeBlockerDepth { get; set; } = 9f;

    [Description("Legacy lower-Z blocker boundary retained for config compatibility; the current blocker width is controlled by surface_escape_blocker_depth.")]
    public float SurfaceEscapeBlockerMinZ { get; set; } = -26f;

    public int SurfaceEscapeBlockerGraceSeconds { get; set; } = 3;

    public int SurfaceEscapeBlockerResetSeconds { get; set; } = 60;

    public float SurfaceEscapeBlockerInitialDrainHpPerSecond { get; set; } = 1f;

    public float SurfaceEscapeBlockerDrainMultiplierPerSecond { get; set; } = 2f;

    public float SurfaceEscapeBlockerMaxDrainPercentPerSecond { get; set; } = 35f;

    public string SurfaceEscapeBlockerWarningTextEnglish { get; set; } = "<size=32><color=#ff3030><b>KEEP SAFEZONE CLEAR</b></color></size>";

    public string SurfaceEscapeBlockerWarningTextChinese { get; set; } = "<size=32><color=#ff3030><b>请勿堵塞安全区</b></color></size>";

    [Description("Legacy Chinese warning retained for config compatibility. Used when the Chinese-specific value is empty.")]
    public string SurfaceEscapeBlockerWarningText { get; set; } = "<size=36><color=#ff3030><b>请不要堵安全区</b></color></size>";

    public bool SafezoneExitSpawnProtectionEnabled { get; set; } = true;

    public int SafezoneExitSpawnProtectionDurationMs { get; set; } = 10000;

    public HintDisplayConfig HintDisplay { get; set; } = new();
}

public sealed class HintDisplayConfig
{
    [Description("Unique HintServiceMeow group. / HintServiceMeow 独占分组。")]
    public string GroupName { get; set; } = "warmupsafezone.hints";

    [Description("Prefix for stable owned HSM tags. / 稳定 HSM 标签前缀。")]
    public string TagPrefix { get; set; } = "warmupsafezone.";

    [Description("Center-aligned HSM X. -800 plus the ghost tail places visible text in the left safe lane. / 居中对齐的 HSM X；-800 配合透明尾部将文字放入左侧安全区域。")]
    public float DefaultX { get; set; } = -800f;

    [Description("Y lane for generic notices. / 通用提示的 Y 位置。")]
    public float NoticeY { get; set; } = 150f;

    [Description("Y lane for blocked-action prompts. / 操作被阻止提示的 Y 位置。")]
    public float ActionPromptY { get; set; } = 150f;

    [Description("Y lane for blocker warnings. / 堵塞警告的 Y 位置。")]
    public float BlockerPromptY { get; set; } = 235f;

    [Description("Y lane for surface drain warnings. / 地表生命流失警告的 Y 位置。")]
    public float SurfaceDrainPromptY { get; set; } = 325f;

    [Description("Transparent 1em cells appended to every row for center-aligned left-lane placement. Set 0 when choosing a different X layout. / 每行末尾追加的透明 1em 单元数；更改 X 布局时设为 0。")]
    public int GhostTailColumns { get; set; } = 49;

    public int NoticeTextSize { get; set; } = 24;
    public int PromptTextSize { get; set; } = 22;
    public float LineHeight { get; set; } = 12f;
    public bool ForceFastUpdates { get; set; }
}
