using LabApi.Features.Console;
using LabApi.Features.Wrappers;

namespace SCPSLBot.Presentation
{
    internal sealed class NullHintDisplayProvider : IHintDisplayProvider
    {
        private readonly string reason;
        private bool logged;

        public NullHintDisplayProvider(string reason)
        {
            this.reason = reason;
        }

        public string Name => "disabled";

        public void Enable() => LogOnce();

        public void Disable()
        {
        }

        public void Show(Player player, in HintRequest request) => LogOnce();

        public void Remove(Player player, string tagId)
        {
        }

        public void Clear(Player player)
        {
        }

        private void LogOnce()
        {
            if (logged)
            {
                return;
            }

            logged = true;
            Logger.Warn($"[SCPSLBot:Hints] {reason} Bot gameplay remains enabled, but hints are disabled.");
        }
    }
}
