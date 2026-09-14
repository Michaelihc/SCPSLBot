#nullable enable

using System;
using System.Collections.Generic;

namespace SCPSLBot.Warmup.Controls;

/// <summary>Chooses native spawn-anchor roles for managed bots without changing player routing.</summary>
public static class WarmupBotSpawnAnchorPolicy
{
    private static readonly HashSet<string> ChaosRoleIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "ChaosConscript",
        "ChaosRifleman",
        "ChaosMarauder",
        "ChaosRepressor",
    };

    public static string ResolveAnchorRoleId(string? arenaId, string? exactBotRoleId)
    {
        if (string.Equals(arenaId, "surface", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(exactBotRoleId)
            && ChaosRoleIds.Contains(exactBotRoleId!))
        {
            return exactBotRoleId!;
        }

        if (string.Equals(arenaId, "lcz", StringComparison.OrdinalIgnoreCase))
        {
            return "ClassD";
        }

        return string.Equals(arenaId, "pvpve", StringComparison.OrdinalIgnoreCase)
            ? "Scp939"
            : "NtfPrivate";
    }

    public static bool UsesExactNativeRoleSpawn(string? arenaId, string? exactBotRoleId) =>
        !string.IsNullOrWhiteSpace(exactBotRoleId)
        && string.Equals(
            ResolveAnchorRoleId(arenaId, exactBotRoleId),
            exactBotRoleId,
            StringComparison.OrdinalIgnoreCase);
}
