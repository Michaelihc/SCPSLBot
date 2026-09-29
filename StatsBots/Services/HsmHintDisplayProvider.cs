using System;
using HsmAdapter;
using LabApi.Features.Console;
using LabApi.Features.Wrappers;
using StatsBots.Config;

namespace StatsBots.Services;

internal sealed class HsmHintDisplayProvider : IHintDisplayProvider
{
    private const float MinimumMiddleLineHeight = 12f;
    private readonly HintDisplayConfig _config;
    private HintScope? _scope;
    private bool _loggedFailure;

    public HsmHintDisplayProvider(HintDisplayConfig config) => _config = config;
    public bool IsAvailable => _scope != null && HsmAdapter.Hints.IsReady;
    public void Enable() => _scope ??= HsmAdapter.Hints.Acquire("StatsBots", _config.GroupName);
    public void Disable() { _scope?.Dispose(); _scope = null; }

    public void Show(Player player, string tagId, float x, float y, int size, string message, float durationSeconds = 0f)
    {
        if (_scope == null || player?.ReferenceHub == null ||
            (!player.IsDummy && (!player.IsPlayer || !player.IsReady))) return;
        try
        {
            _scope.ShowHsm(player, Normalize(tagId), new HsmHintLayout(
                message ?? string.Empty, x, y, Math.Max(6, size), anchor: VerticalAnchor.Middle,
                syncSpeed: HsmSyncSpeed.Fast, fastUpdate: _config.ForceFastUpdates,
                lineHeight: Math.Max(MinimumMiddleLineHeight, _config.LineHeight)), durationSeconds);
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

    private string Normalize(string tag) => tag.StartsWith(_config.TagPrefix, StringComparison.Ordinal) ? tag : _config.TagPrefix + tag;
    private void LogFailure(Exception error)
    {
        if (_loggedFailure) return;
        _loggedFailure = true;
        Logger.Error("[StatsBots:Hints] Adapter display failed: " + error.GetBaseException().Message);
    }
}
