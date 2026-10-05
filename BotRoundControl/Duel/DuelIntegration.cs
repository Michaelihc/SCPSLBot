using LabApi.Features.Wrappers;

namespace DuelArena;

/// <summary>Optional, side-effect-free discovery contract for warmup menu consumers.</summary>
public static class DuelIntegration
{
    public static bool Available(Player player, string userId) => DuelPlugin.Runtime?.Available(player, userId) == true;
    public static bool Owns(ReferenceHub hub) => DuelPlugin.Runtime?.Owns(hub) == true;
    public static bool Locked(Player player) => DuelPlugin.Runtime?.IsFighting(player) == true;
    public static bool CanFight(ReferenceHub left, ReferenceHub right) => DuelPlugin.Runtime?.CanFight(left, right) == true;
    public static bool Enter(Player player, string userId) => DuelPlugin.Runtime?.Enter(player, userId) == true;
    public static void Leave(Player player) => DuelPlugin.Runtime?.Leave(player, "已离开决斗台") ;
}
