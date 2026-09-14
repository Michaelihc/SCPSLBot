#nullable enable

using System;

namespace SCPSLBot.Warmup.Controls;

/// <summary>
/// Maps an explicit regular-player role choice to its evacuation arena when the player is on
/// Surface. Role changes elsewhere preserve the player's exact position.
/// </summary>
public static class WarmupRoleArenaRouting
{
    public static string ResolveArenaId(bool isScp) => isScp ? "lcz" : "pvpve";

    public static bool IsSurfaceAllowedRole(string roleId) => roleId switch
    {
        "FacilityGuard" or "NtfPrivate" or "NtfSergeant" or "NtfCaptain" or "NtfSpecialist" => true,
        _ => false,
    };

    /// <summary>
    /// Room teleport is a facility-navigation aid, not an alternate Surface entry path. Keeping
    /// this rule independent of role also makes stale personalized SSS views harmless.
    /// </summary>
    public static bool CanRoomTeleportEnterZone(string exactZoneId) =>
        !string.Equals(exactZoneId, "Surface", StringComparison.OrdinalIgnoreCase);

    public static string ResolveSurfaceOriginArenaId(bool isSurfaceAllowedRole, bool isScp) =>
        isSurfaceAllowedRole ? "surface" : ResolveArenaId(isScp);

    /// <summary>
    /// Spectator camera coordinates are not a physical player origin. Spectator respawns route from
    /// server-owned arena membership; playable roles prefer their actual generated-map arena.
    /// </summary>
    public static string ResolveRoleChangeOriginArenaId(
        bool isSpectator,
        string logicalArenaId,
        string? physicalArenaId,
        bool nativeZoneIsSurface)
    {
        if (isSpectator)
        {
            return logicalArenaId;
        }

        if (!string.IsNullOrWhiteSpace(physicalArenaId))
        {
            return physicalArenaId!;
        }

        return nativeZoneIsSurface ? "surface" : logicalArenaId;
    }
}
