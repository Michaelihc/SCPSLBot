using LabApi.Features.Wrappers;

namespace ScpslPluginStarter.Services;

internal interface IHintDisplayProvider
{
    void Enable();
    void Disable();
    void ShowNotice(Player player, string message, float duration);
    void ShowPrompt(Player player, string tagId, float y, string message, float duration);
    void Remove(Player player, string tagId);
    void Clear(Player player);
}
