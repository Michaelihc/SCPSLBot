using PlayerRoles;
using SCPSLBot.Presentation;
using SCPSLBot.Warmup.Controls;
using SCPSLBot.Warmup.Controls.Panel;
using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace SCPSLBot
{
    public sealed class BotPluginConfig
    {
        [Description("Player-facing language: empty matches a client preference when a companion exposes it; cn forces Chinese; en forces English. Chinese is the fallback. / 玩家文本语言：留空时在可用时匹配客户端偏好；cn 强制中文；en 强制英文。无法判断时回退中文。")]
        public string Language { get; set; } = string.Empty;

        [Description("HintServiceMeow-backed presentation settings. / 基于 HintServiceMeow 的显示设置。")]
        public HintDisplayConfig HintDisplay { get; set; } = new();

        [Description("Server-authoritative warmup role, item, and arena policy. / 服务器权威的热身角色、物品和竞技场策略。")]
        public WarmupControlsConfig Controls { get; set; } = WarmupControlsConfig.CreateDefault();

        [Description("Personalized Server-Specific Settings presentation. / 个性化服务器专属设置界面。")]
        public WarmupPanelConfig Panel { get; set; } = WarmupPanelConfig.CreateDefault();

        [Description("Fallback used only when WarmupMode contains an invalid value; WarmupMode itself is persisted across reloads.")]
        public WarmupMode DefaultWarmupMode { get; set; } = WarmupMode.Standard;

        public WarmupMode WarmupMode { get; set; } = WarmupMode.Standard;

        public int HumanRespawnDelayMs { get; set; } = 1200;

        public int BotRespawnDelayMs { get; set; } = 2500;

        public int SpectatorRespawnDelayMs { get; set; } = 5000;

        [Description("Seconds between global scans for eligible real players in the exact Spectator role. / 全局扫描符合条件且角色恰好为 Spectator 的真实玩家的间隔（秒）。")]
        public float RespawnScanIntervalSeconds { get; set; } = 0.5f;

        public RoleTypeId DefaultRespawnRole { get; set; } = RoleTypeId.NtfPrivate;

        public int WarmupBotCount { get; set; } = 3;

        public RoleTypeId WarmupBotRole { get; set; } = RoleTypeId.ChaosRifleman;

        public RoleTypeId WarmupHumanRole { get; set; } = RoleTypeId.NtfPrivate;

        [Description("Default physical arena for players who have not selected one. / 尚未选择竞技场的玩家默认区域。")]
        public WarmupArena DefaultWarmupArena { get; set; } = WarmupArena.SurfacePve;

        public float WarmupArenaSwitchCooldownSeconds { get; set; } = 5f;

        public float SurfacePveBotFactor { get; set; } = 1.2f;

        public int SurfacePveMaxBotCount { get; set; } = 6;

        public int HeavyEntrancePvpveBotCount { get; set; } = 2;

        public int LightContainmentScpBotCount { get; set; } = 1;

        public bool DisableWarheadInWarmup { get; set; } = true;

        public bool DisableLczDecontaminationInWarmup { get; set; } = true;

        public bool DisableDisarmingInWarmup { get; set; } = true;

        public bool DisableScp207HealthDrainInWarmup { get; set; } = true;

        [Description("Enables periodic overflow checks. When loose pickups grow beyond the round baseline plus the configured threshold, native item, corpse, blood, and bullet-hole cleanup runs. / 启用定期溢出检查。当散落物品数超过本回合基线与配置阈值之和时，执行原生物品、尸体、血迹和弹孔清理。")]
        public bool EnableOverflowCleanup { get; set; } = true;

        [Description("Number of loose pickups allowed above the current round baseline before native cleanup runs. / 触发原生清理前，相对本回合基线允许增加的散落物品数量。")]
        public int CleanupItemThreshold { get; set; } = 80;

        [Description("Seconds between overflow checks. / 溢出检查间隔（秒）。")]
        public float CleanupCheckIntervalSeconds { get; set; } = 10f;

        // When true, every non-standard room connector (open hallways, bulk doors, clutter) is
        // forced to an HCZ standard door at map generation so the baked navmesh (which only links
        // rooms that have a door) has a door at every room link. This uniformizes the map and
        // conflicts with map-layout plugins, so it is OFF by default; bots instead get navmesh
        // links built across door-less connectors at load time (see NavigationSystem).
        public bool ForceStandardDoorConnectors { get; set; } = false;

        internal IReadOnlyList<string> Normalize()
        {
            var changes = new List<string>();
            HintDisplay ??= new HintDisplayConfig();
            Controls ??= WarmupControlsConfig.CreateDefault();
            Panel ??= WarmupPanelConfig.CreateDefault();
            Controls.Language = Language ?? string.Empty;
            foreach (string restoredOption in Controls.RestoreClassicPlayerOptions())
            {
                changes.Add(restoredOption);
            }
            if (Panel.ShowAdminTools)
            {
                Panel.ShowAdminTools = false;
                changes.Add("disabled player-visible debug/admin SSS options");
            }
            Panel.MinimumActionIntervalMilliseconds = NormalizeRange(
                "Panel.MinimumActionIntervalMilliseconds",
                Panel.MinimumActionIntervalMilliseconds,
                250,
                10000,
                changes);
            HumanRespawnDelayMs = NormalizeRange(nameof(HumanRespawnDelayMs), HumanRespawnDelayMs, 50, 300000, changes);
            BotRespawnDelayMs = NormalizeRange(nameof(BotRespawnDelayMs), BotRespawnDelayMs, 50, 300000, changes);
            SpectatorRespawnDelayMs = NormalizeRange(nameof(SpectatorRespawnDelayMs), SpectatorRespawnDelayMs, 50, 300000, changes);
            RespawnScanIntervalSeconds = NormalizeRange(nameof(RespawnScanIntervalSeconds), RespawnScanIntervalSeconds, 0.1f, 5f, changes);
            WarmupBotCount = NormalizeRange(nameof(WarmupBotCount), WarmupBotCount, 0, 10, changes);
            SurfacePveMaxBotCount = NormalizeRange(nameof(SurfacePveMaxBotCount), SurfacePveMaxBotCount, 2, 6, changes);
            HeavyEntrancePvpveBotCount = NormalizeRange(nameof(HeavyEntrancePvpveBotCount), HeavyEntrancePvpveBotCount, 2, 5, changes);
            LightContainmentScpBotCount = NormalizeRange(nameof(LightContainmentScpBotCount), LightContainmentScpBotCount, 1, 1, changes);
            SurfacePveBotFactor = Math.Max(1f, Math.Min(2f, SurfacePveBotFactor));
            WarmupArenaSwitchCooldownSeconds = Math.Max(0f, Math.Min(300f, WarmupArenaSwitchCooldownSeconds));
            return changes;
        }

        private static int NormalizeRange(string name, int value, int minimum, int maximum, ICollection<string> changes)
        {
            var original = value;
            value = Math.Max(minimum, Math.Min(maximum, value));
            if (value != original)
            {
                changes.Add($"{name} {original} -> {value}");
            }

            return value;
        }

        private static float NormalizeRange(string name, float value, float minimum, float maximum, ICollection<string> changes)
        {
            var original = value;
            value = Math.Max(minimum, Math.Min(maximum, value));
            if (Math.Abs(value - original) > 0.0001f)
            {
                changes.Add($"{name} {original} -> {value}");
            }

            return value;
        }
    }

    public enum WarmupMode
    {
        None,
        Standard,
    }

    public enum WarmupArena
    {
        SurfacePve,
        HeavyEntrancePvpve,
        LightContainmentScp,
    }
}
