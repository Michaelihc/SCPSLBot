using LabApi.Features.Wrappers;
using System.Collections.Generic;
using System.Linq;

namespace SCPSLBot.Presentation
{
    /// <summary>
    /// Typed presentation facade for gameplay features. It owns tag/lane choices so managers and
    /// AI code do not know whether HSM, a compatibility provider, or no renderer is active.
    /// </summary>
    internal sealed class BotPresentationService
    {
        private readonly HintDisplayConfig config;
        private readonly BotLocalization localization;
        private readonly IHintDisplayProvider display;
        private readonly Dictionary<(ReferenceHub Hub, string Tag), string> sentText = new();
        private readonly HashSet<string> botDiagnosticsUsers = new(System.StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> navDiagnosticsUsers = new(System.StringComparer.OrdinalIgnoreCase);

        public BotPresentationService(HintDisplayConfig config, BotLocalization localization, IHintDisplayProvider display)
        {
            this.config = config;
            this.localization = localization;
            this.display = display;
        }

        public void ShowDisarmingDisabled(Player player) => ShowNotice(player, "warmup.disarming", BotMessage.DisarmingDisabled);

        public void ShowWarheadDisabled(Player player) => ShowNotice(player, "warmup.warhead", BotMessage.WarheadDisabled);

        public void ShowSurfaceEvacuation(Player player, bool toLightContainment)
        {
            if (player == null)
            {
                return;
            }

            BotMessage message = toLightContainment
                ? BotMessage.SurfaceEvacuatedToLight
                : BotMessage.SurfaceEvacuatedToHeavyEntrance;
            player.SendBroadcast(
                localization.Get(player, message),
                4,
                global::Broadcast.BroadcastFlags.Normal,
                shouldClearPrevious: true);
        }

        public void ShowEditorStoppedOnCellCreation(Player player) => ShowEditor(player, "editor.auto_select", BotMessage.EditorStoppedOnCellCreation);

        public void ShowEditorStoppedOnFirstVertex(Player player) => ShowEditor(player, "editor.auto_select", BotMessage.EditorStoppedOnFirstVertex);

        public string ProviderName => display.Name;

        public void Enable() => display.Enable();

        public void Disable()
        {
            sentText.Clear();
            botDiagnosticsUsers.Clear();
            navDiagnosticsUsers.Clear();
            display.Disable();
        }

        public bool IsBotDiagnosticsEnabled(Player player) => HasPreference(botDiagnosticsUsers, player);

        public bool IsNavDiagnosticsEnabled(Player player) => HasPreference(navDiagnosticsUsers, player);

        public void SetBotDiagnosticsEnabled(Player player, bool enabled)
        {
            SetPreference(botDiagnosticsUsers, player, enabled);
            if (!enabled)
            {
                RemoveBotDiagnostics(player);
            }
        }

        public void SetNavDiagnosticsEnabled(Player player, bool enabled)
        {
            SetPreference(navDiagnosticsUsers, player, enabled);
            if (!enabled)
            {
                RemoveNavDiagnostics(player);
            }
        }

        public void ShowBotDiagnostics(Player player, in BotDiagnosticView view)
        {
            if (player == null || !IsBotDiagnosticsEnabled(player))
            {
                return;
            }

            string message = BuildBotDiagnostics(player, view);
            ShowChanged(player, "admin.bot_diagnostics", message, 2.5f, config.SpectatorY, config.SpectatorTextSize);
        }

        public void ShowNavDiagnostics(Player player, string message)
        {
            if (IsNavDiagnosticsEnabled(player))
            {
                ShowChanged(player, "admin.nav_authoring", message, 2.5f, config.EditorY, config.SpectatorTextSize);
            }
        }

        public void RemoveBotDiagnostics(Player player) => Remove(player, "admin.bot_diagnostics");

        public void RemoveNavDiagnostics(Player player) => Remove(player, "admin.nav_authoring");

        public void Clear(Player player)
        {
            if (player == null)
            {
                return;
            }

            foreach ((ReferenceHub hub, string tag) in sentText.Keys.Where(key => key.Hub == player.ReferenceHub).ToArray())
            {
                sentText.Remove((hub, tag));
            }

            display.Clear(player);
        }

        public void ForgetPlayer(Player player)
        {
            if (player == null)
            {
                return;
            }

            Clear(player);
            if (!string.IsNullOrWhiteSpace(player.UserId))
            {
                botDiagnosticsUsers.Remove(player.UserId);
                navDiagnosticsUsers.Remove(player.UserId);
                localization.ForgetPlayer(player.UserId);
            }
        }

        public void ForgetSpectator(ReferenceHub hub)
        {
            if (hub == null)
            {
                return;
            }

            Player player = Player.Get(hub);
            if (player != null)
            {
                RemoveBotDiagnostics(player);
            }
        }

        public void ResetSpectators()
        {
            foreach ((ReferenceHub hub, string tag) in sentText.Keys
                         .Where(key => key.Tag == "admin.bot_diagnostics")
                         .ToArray())
            {
                Player player = Player.Get(hub);
                if (player != null)
                {
                    display.Remove(player, tag);
                }

                sentText.Remove((hub, tag));
            }
        }

        private void ShowNotice(Player player, string tag, BotMessage message)
        {
            if (player == null)
            {
                return;
            }

            ShowChanged(player, tag, localization.Get(player, message), 2f, config.NoticeY, config.NoticeTextSize, alwaysShow: true);
        }

        private void ShowEditor(Player player, string tag, BotMessage message)
        {
            if (player == null)
            {
                return;
            }

            ShowChanged(player, tag, localization.Get(player, message), 3f, config.EditorY, config.NoticeTextSize, alwaysShow: true);
        }

        private void ShowChanged(Player player, string tag, string message, float duration, float y, int size, bool alwaysShow = false)
        {
            (ReferenceHub Hub, string Tag) key = (player.ReferenceHub, tag);
            if (!alwaysShow && sentText.TryGetValue(key, out string previous) && previous == message)
            {
                return;
            }

            sentText[key] = message;
            display.Show(player, new HintRequest(tag, message, duration, config.X, y, size));
        }

        private void Remove(Player player, string tag)
        {
            if (player == null)
            {
                return;
            }

            sentText.Remove((player.ReferenceHub, tag));
            display.Remove(player, tag);
        }

        private string BuildBotDiagnostics(Player player, in BotDiagnosticView view)
        {
            string bot = localization.DiagnosticLabel(player, "BOT", "机器人");
            string state = localization.DiagnosticLabel(player, "STATE", "状态");
            string target = localization.DiagnosticLabel(player, "TARGET", "目标");
            string nav = localization.DiagnosticLabel(player, "NAV", "导航");
            return $"<size={config.SpectatorTextSize}><b>{bot}</b> {LocalizeDiagnosticValue(player, view.Bot)}\n" +
                   $"<b>{state}</b> {LocalizeDiagnosticValue(player, view.State)}\n" +
                   $"<b>{target}</b> {LocalizeDiagnosticValue(player, view.Target)}\n" +
                   $"<b>{nav}</b> {LocalizeDiagnosticValue(player, view.Navigation)}";
        }

        private string LocalizeDiagnosticValue(Player player, string value)
        {
            if (localization.IsEnglish(player) || string.IsNullOrEmpty(value))
            {
                return value;
            }

            return value
                .Replace("surface idle", "地表待机")
                .Replace("order target", "指令目标")
                .Replace("runner healthy", "调度器正常")
                .Replace("runner stopped", "调度器已停止")
                .Replace("human_combat", "人类战斗")
                .Replace("scp_combat", "SCP 战斗")
                .Replace("no path", "无路径")
                .Replace("ordered", "执行指令")
                .Replace("held", "原地待命")
                .Replace("combat", "战斗中")
                .Replace("hostile", "敌对目标")
                .Replace("visible", "可见")
                .Replace("remembered", "记忆目标")
                .Replace("parked", "暂停恢复")
                .Replace("idle", "待机")
                .Replace("none", "无")
                .Replace("path", "路径")
                .Replace("cells", "单元格");
        }

        private static bool HasPreference(ISet<string> preferences, Player player) =>
            player != null &&
            !string.IsNullOrWhiteSpace(player.UserId) &&
            preferences.Contains(player.UserId);

        private static void SetPreference(ISet<string> preferences, Player player, bool enabled)
        {
            if (player == null || string.IsNullOrWhiteSpace(player.UserId))
            {
                return;
            }

            if (enabled)
            {
                preferences.Add(player.UserId);
            }
            else
            {
                preferences.Remove(player.UserId);
            }
        }
    }

    internal readonly record struct BotDiagnosticView(string Bot, string State, string Target, string Navigation);
}
