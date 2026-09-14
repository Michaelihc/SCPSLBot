using System;

namespace WarmupShared;

/// <summary>Admin Tutorial belongs to the native game, outside warmup player management.</summary>
internal static class WarmupParticipationPolicy
{
    public static bool IsManagedRole(string roleId) =>
        !string.Equals(roleId, "Tutorial", StringComparison.OrdinalIgnoreCase);
}
