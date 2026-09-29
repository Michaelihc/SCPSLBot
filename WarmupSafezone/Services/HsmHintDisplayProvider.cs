using System;
using System.Linq;
using HsmAdapter;
using LabApi.Features.Console;
using LabApi.Features.Wrappers;

namespace ScpslPluginStarter.Services;

internal sealed class HsmHintDisplayProvider : IHintDisplayProvider
{
    private readonly HintDisplayConfig _config;
    private HintScope? _scope;
    private bool _loggedFailure;

    public HsmHintDisplayProvider(HintDisplayConfig config) => _config = config;
    public void Enable() => _scope ??= HsmAdapter.Hints.Acquire("WarmupSafezone", _config.GroupName);
    public void Disable() { _scope?.Dispose(); _scope = null; }
    public void ShowNotice(Player player, string message, float duration) =>
        ShowPrompt(player, "notice", _config.NoticeY, message, duration);

    public void ShowPrompt(Player player, string tagId, float y, string message, float duration)
    {
        if (_scope == null || player?.ReferenceHub == null) return;
        try
        {
            _scope.ShowHsm(player, Normalize(tagId), new HsmHintLayout(
                AddGhostTailToRows(message ?? string.Empty), _config.DefaultX, y,
                Math.Max(6, _config.PromptTextSize), anchor: VerticalAnchor.Middle,
                syncSpeed: HsmSyncSpeed.Fast, fastUpdate: _config.ForceFastUpdates,
                lineHeight: Math.Max(0f, _config.LineHeight)), duration);
        }
        catch (Exception error) { LogFailure(error); }
    }

    public void Remove(Player player, string tagId)
    {
        if (_scope == null || player?.ReferenceHub == null) return;
        try { _scope.Remove(player, Normalize(tagId)); }
        catch (Exception error) { LogFailure(error); }
    }

    public void Clear(Player player)
    {
        if (_scope == null || player?.ReferenceHub == null) return;
        try { _scope.Clear(player); }
        catch (Exception error) { LogFailure(error); }
    }

    private string AddGhostTailToRows(string message)
    {
        int columns = Math.Max(0, Math.Min(80, _config.GhostTailColumns));
        if (columns == 0 || string.IsNullOrEmpty(message)) return message;
        string tail = $"<color=#00000000><mspace=1em>{new string(' ', columns)}</mspace></color>";
        return string.Join("\n", message.Replace("\r\n", "\n").Replace('\r', '\n')
            .Split(new[] { '\n' }, StringSplitOptions.None).Select(row => row + tail));
    }

    private string Normalize(string tag)
    {
        string prefix = string.IsNullOrWhiteSpace(_config.TagPrefix) ? "warmupsafezone." : _config.TagPrefix;
        return tag.StartsWith(prefix, StringComparison.Ordinal) ? tag : prefix + tag;
    }

    private void LogFailure(Exception error)
    {
        if (_loggedFailure) return;
        _loggedFailure = true;
        Logger.Error("[WarmupSafezone:Hints] Adapter display failed: " + error.GetBaseException().Message);
    }
}
