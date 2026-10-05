#nullable enable

using System;
using LabApi.Features.Extensions;
using PlayerRoles;
using UnityEngine;

namespace SCPSLBot.Warmup.Controls;

/// <summary>
/// Resolves a native role spawnpoint before any role mutation. Anchor-role overrides affect only
/// placement; the exact requested role remains unchanged.
/// </summary>
public sealed class SpawnAnchorResolver : ISpawnAnchorProvider
{
    private readonly RoleControlsConfig config;

    public SpawnAnchorResolver(RoleControlsConfig config)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
    }

    public bool TryResolve(
        string exactRoleId,
        bool isScp,
        ArenaPresetDefinition activePreset,
        out SpawnAnchor anchor)
    {
        anchor = default;
        if (!TryParseDefinedRole(exactRoleId, out RoleTypeId exactRole)
            || exactRole is RoleTypeId.None or RoleTypeId.Spectator or RoleTypeId.Destroyed
            || exactRole.IsScp() != isScp)
        {
            return false;
        }

        return TryResolve(exactRole, activePreset, out anchor);
    }

    public bool TryResolve(
        RoleTypeId exactRole,
        ArenaPresetDefinition activePreset,
        out SpawnAnchor anchor)
    {
        anchor = default;
        if (activePreset == null)
        {
            return false;
        }

        RoleTypeId anchorRole = exactRole;
        // A preset's explicit SCP anchor always wins; per-role overrides cannot escape that boundary.
        if (exactRole.IsScp() && !string.IsNullOrWhiteSpace(activePreset.ScpSpawnAnchorRoleId))
        {
            if (!TryParseAnchorRole(activePreset.ScpSpawnAnchorRoleId, out anchorRole))
            {
                return false;
            }
        }
        else if (config.SpawnAnchorRoleOverrides != null
                 && config.SpawnAnchorRoleOverrides.TryGetValue(exactRole.ToString(), out string? overrideRoleId)
                 && !TryParseAnchorRole(overrideRoleId, out anchorRole))
        {
            return false;
        }
        else if (!string.IsNullOrWhiteSpace(activePreset.SpawnAnchorRoleId)
                 && !TryParseAnchorRole(activePreset.SpawnAnchorRoleId, out anchorRole))
        {
            return false;
        }

        // Official LabAPI role extension; this happens before SetRole and fails closed.
        if (!anchorRole.TryGetRandomSpawnPoint(out Vector3 position, out float horizontalRotation)
            || !IsFinite(position.x)
            || !IsFinite(position.y)
            || !IsFinite(position.z)
            || !IsFinite(horizontalRotation))
        {
            return false;
        }

        anchor = new SpawnAnchor(position.x, position.y, position.z, horizontalRotation);
        return true;
    }

    private static bool TryParseAnchorRole(string roleId, out RoleTypeId role) =>
        TryParseDefinedRole(roleId, out role)
        && role is not RoleTypeId.None and not RoleTypeId.Spectator and not RoleTypeId.Destroyed;

    private static bool TryParseDefinedRole(string roleId, out RoleTypeId role) =>
        Enum.TryParse(roleId, true, out role) && Enum.IsDefined(typeof(RoleTypeId), role);

    private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
}
