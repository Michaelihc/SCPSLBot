using LabApi.Features.Wrappers;

namespace SCPSLBot.Presentation
{
    internal interface IHintDisplayProvider
    {
        string Name { get; }

        void Enable();

        void Disable();

        void Show(Player player, in HintRequest request);

        void Remove(Player player, string tagId);

        void Clear(Player player);
    }
}
