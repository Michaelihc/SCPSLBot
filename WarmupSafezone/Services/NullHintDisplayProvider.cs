using LabApi.Features.Console;
using LabApi.Features.Wrappers;

namespace ScpslPluginStarter.Services;

internal sealed class NullHintDisplayProvider : IHintDisplayProvider
{
    private readonly string _reason;
    private bool _logged;

    public NullHintDisplayProvider(string reason) => _reason = reason;

    public void Enable() => LogOnce();
    public void Disable() { }
    public void ShowNotice(Player player, string message, float duration) => LogOnce();
    public void ShowPrompt(Player player, string tagId, float y, string message, float duration) => LogOnce();
    public void Remove(Player player, string tagId) { }
    public void Clear(Player player) { }

    private void LogOnce()
    {
        if (_logged)
        {
            return;
        }

        _logged = true;
        Logger.Error($"[WarmupSafezone:Hints] {_reason} No player hint text will be displayed.");
    }
}
