#nullable enable

using LabApi.Features.Wrappers;
using PlayerRoles;
using WarmupShared;

namespace SCPSLBot.Warmup;

/// <summary>
/// Shared boundary for per-player warmup behavior. Role events must pass their destination role;
/// ordinary actions and delayed work must check the player's current role at execution time.
/// World-wide warmup rules and native RA authority are independent of player participation.
/// </summary>
internal static class WarmupParticipation
{
    public static bool IsManagedRole(RoleTypeId role) =>
        WarmupParticipationPolicy.IsManagedRole(role.ToString());

    public static bool IsParticipant(Player? player) =>
        player != null && !player.IsDestroyed && IsParticipant(player, player.Role);

    public static bool IsParticipant(Player? player, RoleTypeId role) =>
        IsRealPlayer(player) && IsManagedRole(role);

    public static bool IsRealPlayer(Player? player) =>
        player != null && !player.IsDestroyed && player.IsReady && player.IsPlayer
        && !player.IsHost && !player.IsDummy && !string.IsNullOrWhiteSpace(player.UserId);
}
