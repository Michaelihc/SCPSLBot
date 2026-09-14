using LabApi.Features.Wrappers;

namespace StatsBots.Services;

internal interface IHintDisplayProvider
{
    bool IsAvailable { get; }
    void Enable();
    void Disable();
    void Show(Player player, string tagId, float x, float y, int size, string message, float durationSeconds = 0f);
    void Remove(Player player, string tagId);
    void Clear(Player player);
}
