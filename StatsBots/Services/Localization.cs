using System;
using System.Reflection;
using System.Text;
using LabApi.Features.Wrappers;
using StatsBots.Config;

namespace StatsBots.Services;

internal sealed class Localization
{
    private readonly StatsBotsConfig _config;
    private readonly PropertyInfo? _futurePlayerLanguage = typeof(Player).GetProperty("ClientLanguage", BindingFlags.Public | BindingFlags.Instance)
        ?? typeof(Player).GetProperty("Language", BindingFlags.Public | BindingFlags.Instance);

    public Localization(StatsBotsConfig config) => _config = config;

    public bool Chinese(Player? player)
    {
        string configured = (_config.Language ?? string.Empty).Trim().ToLowerInvariant();
        if (configured == "en") return false;
        if (configured is "cn" or "zh") return true;
        if (_futurePlayerLanguage != null && player != null)
        {
            try
            {
                string? client = _futurePlayerLanguage.GetValue(player)?.ToString()?.Trim().ToLowerInvariant();
                if (client != null && client.StartsWith("en", StringComparison.Ordinal)) return false;
                if (client != null && (client.StartsWith("zh", StringComparison.Ordinal) || client.StartsWith("cn", StringComparison.Ordinal))) return true;
            }
            catch { }
        }
        return true;
    }

    public string Pick(Player? player, string english, string chinese) => Chinese(player) ? chinese : english;

    public string Setup(Player player) => Pick(player,
        "New here? Open Settings > Server-Specific Settings to configure warmup controls.",
        "第一次来？打开“设置 > 服务器专属设置”即可配置热身控制。" );

    public string Community(Player player) => Pick(player,
        "Join QQ 897907125 to report bugs, discuss the server, or apply for admin and other perks.",
        "加入 QQ 群 897907125：反馈问题、交流服务器，或申请管理员及其他福利。" );

    public string Pick(Player player, LocalizedTextConfig text) => Pick(player, text.English, text.Chinese);

    public static string EscapeRichText(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var result = new StringBuilder(value!.Length);
        foreach (char c in value)
        {
            switch (c)
            {
                case '<': result.Append("&lt;"); break;
                case '>': result.Append("&gt;"); break;
                case '&': result.Append("&amp;"); break;
                default: result.Append(c); break;
            }
        }
        return result.ToString();
    }
}
