using System.ComponentModel;

namespace SCPSLBot.Presentation
{
    public sealed class HintDisplayConfig
    {
        [Description("Unique HintServiceMeow group owned by SCPSLBot. / SCPSLBot 独占的 HintServiceMeow 分组。")]
        public string GroupName { get; set; } = "scpslbot.hints";

        [Description("Prefix for stable HintServiceMeow IDs. / 稳定 HintServiceMeow ID 的前缀。")]
        public string TagPrefix { get; set; } = "scpslbot.";

        [Description("Horizontal HSM coordinate; zero is centered. / HSM 水平坐标；0 为居中。")]
        public float X { get; set; } = 0f;

        [Description("Vertical position for warmup rule notices. / 热身规则提示的垂直位置。")]
        public float NoticeY { get; set; } = 1040f;

        [Description("Vertical position for navigation-editor notices. / 导航网格编辑提示的垂直位置。")]
        public float EditorY { get; set; } = 900f;

        [Description("Vertical position for bot spectator diagnostics. / 观察机器人诊断信息的垂直位置。")]
        public float SpectatorY { get; set; } = 180f;

        [Description("Font size for short notices. / 短提示字号。")]
        public int NoticeTextSize { get; set; } = 24;

        [Description("Font size for bot spectator diagnostics. / 机器人观察诊断字号。")]
        public int SpectatorTextSize { get; set; } = 18;

        [Description("Force an immediate HSM display refresh after changes. / 提示变化后强制 HSM 立即刷新。")]
        public bool ForceFastUpdates { get; set; } = false;
    }
}
