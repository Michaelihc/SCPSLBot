using System;
using HsmAdapter;
using LabApi.Features.Console;
using LabApi.Features.Wrappers;

namespace SCPSLBot.Presentation
{
    internal sealed class HsmHintDisplayProvider : IHintDisplayProvider
    {
        private readonly HintDisplayConfig config;
        private HintScope scope;
        private bool loggedFailure;

        public HsmHintDisplayProvider(HintDisplayConfig config) => this.config = config;
        public string Name => "hsm-adapter";
        public void Enable() => scope ??= HsmAdapter.Hints.Acquire("SCPSLBot", config.GroupName);
        public void Disable() { scope?.Dispose(); scope = null; }

        public void Show(Player player, in HintRequest request)
        {
            if (scope == null || player?.ReferenceHub == null || player.IsDestroyed ||
                (!player.IsDummy && (!player.IsPlayer || !player.IsReady))) return;
            if (request.Duration <= 0f) { Remove(player, request.TagId); return; }
            try
            {
                scope.ShowHsm(player, Normalize(request.TagId), new HsmHintLayout(
                    request.Message ?? string.Empty, request.X, request.Y, Math.Max(6, request.TextSize),
                    anchor: VerticalAnchor.Middle, syncSpeed: HsmSyncSpeed.Fast,
                    fastUpdate: config.ForceFastUpdates, lineHeight: 12f), request.Duration);
            }
            catch (Exception error) { LogFailure(error); }
        }

        public void Remove(Player player, string tagId)
        {
            if (scope == null || player?.ReferenceHub == null) return;
            try { scope.Remove(player, Normalize(tagId)); }
            catch (Exception error) { LogFailure(error); }
        }

        public void Clear(Player player)
        {
            if (scope == null || player?.ReferenceHub == null) return;
            try { scope.Clear(player); }
            catch (Exception error) { LogFailure(error); }
        }

        private string Normalize(string tag)
        {
            string prefix = string.IsNullOrWhiteSpace(config.TagPrefix) ? "scpslbot." : config.TagPrefix;
            return tag.StartsWith(prefix, StringComparison.Ordinal) ? tag : prefix + tag;
        }

        private void LogFailure(Exception error)
        {
            if (loggedFailure) return;
            loggedFailure = true;
            Logger.Error("[SCPSLBot:Hints] Adapter display failed: " + error.GetBaseException().Message);
        }
    }
}
