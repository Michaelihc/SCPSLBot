using LabApi.Features.Wrappers;
using System;
using System.Collections.Generic;

namespace SCPSLBot.Presentation
{
    internal enum BotMessage
    {
        DisarmingDisabled,
        WarheadDisabled,
        SurfaceEvacuatedToHeavyEntrance,
        SurfaceEvacuatedToLight,
        EditorStoppedOnCellCreation,
        EditorStoppedOnFirstVertex,
    }

    internal sealed class BotLocalization
    {
        private readonly string configuredLanguage;
        private readonly Dictionary<string, string> playerLanguages = new(StringComparer.OrdinalIgnoreCase);

        public BotLocalization(string configuredLanguage)
        {
            this.configuredLanguage = (configuredLanguage ?? string.Empty).Trim().ToLowerInvariant();
        }

        public string Get(Player player, BotMessage message)
        {
            return IsEnglish(player) ? English(message) : Chinese(message);
        }

        public bool IsEnglish(Player player)
        {
            if (configuredLanguage is "en" or "english")
            {
                return true;
            }

            if (configuredLanguage is "cn" or "zh" or "chinese")
            {
                return false;
            }

            // SCP:SL does not currently expose the client's game-language preference to the
            // dedicated server. A companion SSS/profile service can supply a per-player choice;
            // otherwise the project-wide required fallback is Chinese.
            return player != null &&
                   !string.IsNullOrWhiteSpace(player.UserId) &&
                   playerLanguages.TryGetValue(player.UserId, out string language) &&
                   language == "en";
        }

        public void SetPlayerLanguage(string userId, string language)
        {
            if (string.IsNullOrWhiteSpace(userId))
            {
                return;
            }

            string normalized = (language ?? string.Empty).Trim().ToLowerInvariant();
            if (normalized is "en" or "english")
            {
                playerLanguages[userId] = "en";
            }
            else if (normalized is "cn" or "zh" or "chinese")
            {
                playerLanguages[userId] = "cn";
            }
            else
            {
                playerLanguages.Remove(userId);
            }
        }

        public void ForgetPlayer(string userId)
        {
            if (!string.IsNullOrWhiteSpace(userId))
            {
                playerLanguages.Remove(userId);
            }
        }

        public string DiagnosticLabel(Player player, string english, string chinese) =>
            IsEnglish(player) ? english : chinese;

        private static string English(BotMessage message) => message switch
        {
            BotMessage.DisarmingDisabled => "<b><color=#FFB347>WARMUP</color></b>\nDisarming is disabled.",
            BotMessage.WarheadDisabled => "<b><color=#FFB347>WARMUP</color></b>\nThe Alpha Warhead is disabled.",
            BotMessage.SurfaceEvacuatedToHeavyEntrance => "<b><color=#FFB347>WARMUP EVACUATION</color></b>\nThat role is not allowed on Surface. You were moved to HCZ / EZ.",
            BotMessage.SurfaceEvacuatedToLight => "<b><color=#FFB347>WARMUP EVACUATION</color></b>\nSCP roles are not allowed on Surface. You were moved to LCZ.",
            BotMessage.EditorStoppedOnCellCreation => "<b>Navigation editor</b>\nVertex auto-selection stopped after creating the cell.",
            BotMessage.EditorStoppedOnFirstVertex => "<b>Navigation editor</b>\nVertex auto-selection stopped at the first selected vertex.",
            _ => string.Empty,
        };

        private static string Chinese(BotMessage message) => message switch
        {
            BotMessage.DisarmingDisabled => "<b><color=#FFB347>热身模式</color></b>\n已禁用解除武装。",
            BotMessage.WarheadDisabled => "<b><color=#FFB347>热身模式</color></b>\n已禁用阿尔法核弹。",
            BotMessage.SurfaceEvacuatedToHeavyEntrance => "<b><color=#FFB347>热身疏散</color></b>\n该角色不允许留在地表。你已被移动至重收 / 入口区。",
            BotMessage.SurfaceEvacuatedToLight => "<b><color=#FFB347>热身疏散</color></b>\nSCP 角色不允许留在地表。你已被移动至轻收区。",
            BotMessage.EditorStoppedOnCellCreation => "<b>导航网格编辑器</b>\n创建单元格后已停止自动选择顶点。",
            BotMessage.EditorStoppedOnFirstVertex => "<b>导航网格编辑器</b>\n回到首个顶点后已停止自动选择。",
            _ => string.Empty,
        };
    }
}
