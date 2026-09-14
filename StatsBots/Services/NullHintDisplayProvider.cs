using LabApi.Features.Console;
using LabApi.Features.Wrappers;

namespace StatsBots.Services;

internal sealed class NullHintDisplayProvider : IHintDisplayProvider
{
    private readonly string _reason;
    private bool _logged;
    public NullHintDisplayProvider(string reason) => _reason = reason;
    public bool IsAvailable => false;
    public void Enable() => LogOnce();
    public void Disable() { }
    public void Show(Player player, string tagId, float x, float y, int size, string message, float durationSeconds = 0f) => LogOnce();
    public void Remove(Player player, string tagId) { }
    public void Clear(Player player) { }
    private void LogOnce()
    {
        if (_logged) return;
        _logged = true;
        Logger.Warn("[StatsBots:Hints] " + _reason);
    }
}
