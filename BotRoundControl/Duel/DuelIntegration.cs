using LabApi.Features.Wrappers;

namespace DuelArena;

/// <summary>Duel access and ownership contract for management menu consumers.</summary>
public static class DuelIntegration
{
    public static bool Available(Player player, string userId) => global::BotRoundControl.BotRoundControlPlugin.Runtime?.Available(player, userId) == true;
    public static bool Owns(ReferenceHub hub) => global::BotRoundControl.BotRoundControlPlugin.Runtime?.Owns(hub) == true;
    public static bool CanFight(ReferenceHub left, ReferenceHub right) => global::BotRoundControl.BotRoundControlPlugin.Runtime?.CanFight(left, right) == true;
    public static bool Enter(Player player, string userId) => global::BotRoundControl.BotRoundControlPlugin.Runtime?.Enter(player, userId) == true;
    public static void Leave(Player player) => global::BotRoundControl.BotRoundControlPlugin.Runtime?.Leave(player, "已离开决斗台") ;
}
