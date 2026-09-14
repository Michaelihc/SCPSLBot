#nullable enable

using System;
using System.Collections.Generic;

namespace SCPSLBot.Warmup.Controls;

/// <summary>
/// Intrinsic exclusions for the permissive native role selector. Configuration, arena presets,
/// player life state, and team capacity must not shrink the regular role catalog.
/// </summary>
public static class WarmupRoleSelectionPolicy
{
    private static readonly HashSet<string> ExcludedRoleIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "None",
        "Spectator",
        "Destroyed",
        "Overwatch",
        "Filmmaker",
        "CustomRole",
        "Tutorial",
    };

    public static bool IsPlayerSelectableRole(string? exactRoleId)
    {
        if (string.IsNullOrWhiteSpace(exactRoleId))
        {
            return false;
        }

        return !ExcludedRoleIds.Contains(exactRoleId!);
    }
}
