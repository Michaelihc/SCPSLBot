using LabApi.Features.Wrappers;
using SCPSLBot.AI;
using System.Linq;

namespace SCPSLBot.Api
{
    /// <summary>
    /// Read-only identity boundary for companion plugins. Native RA dummies all share ID_Dummy,
    /// so consumers must use SCPSLBot ownership rather than UserId or Player.IsDummy.
    /// </summary>
    public static class ManagedBotIdentity
    {
        public static bool IsManaged(Player player) =>
            player?.ReferenceHub != null && IsManaged(player.ReferenceHub);

        public static bool IsManaged(ReferenceHub hub) =>
            hub != null && BotManager.Instance.BotPlayers.ContainsKey(hub);

        public static bool IsLive(Player player)
        {
            if (player?.ReferenceHub == null
                || player.IsDestroyed
                || !player.IsAlive
                || !BotManager.Instance.BotPlayers.TryGetValue(player.ReferenceHub, out var bot))
            {
                return false;
            }

            return !bot.IsDisposed && !bot.IsParked && bot.CurrentBotPlayer != null;
        }

        public static int LiveCount => BotManager.Instance.BotPlayers.Count(entry =>
        {
            Player player = Player.Get(entry.Key);
            return IsLive(player);
        });
    }
}
