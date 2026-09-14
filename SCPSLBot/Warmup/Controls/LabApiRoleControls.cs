#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using LabApi.Features.Extensions;
using LabPlayer = LabApi.Features.Wrappers.Player;
using MapGeneration;
using PlayerRoles;
using UnityEngine;

namespace SCPSLBot.Warmup.Controls;

/// <summary>
/// Integration-owned authority. Implement it from live warmup mode, preset, permission, and capacity state.
/// SSS values must never be used to answer these methods.
/// </summary>
public interface ILabApiRoleControlAuthority
{
    bool IsWarmupActive { get; }

    ArenaPresetDefinition GetActivePreset(LabPlayer player);

    bool HasForceRolePermission(LabPlayer player);

}

/// <summary>Native facade intended for personalized SSS option building and deliberate callbacks.</summary>
public sealed class LabApiRoleControlService
{
    private readonly LabApiRoleSnapshotSource snapshots;
    private readonly RoleEligibilityService eligibility;
    private readonly RoleChangeService changes;

    public LabApiRoleControlService(
        RoleControlsConfig config,
        ILabApiRoleControlAuthority authority,
        PerUserRequestGuard? sharedRequestGuard = null)
    {
        if (config == null)
        {
            throw new ArgumentNullException(nameof(config));
        }

        if (authority == null)
        {
            throw new ArgumentNullException(nameof(authority));
        }

        eligibility = new RoleEligibilityService(config);
        snapshots = new LabApiRoleSnapshotSource(config, authority);
        changes = new RoleChangeService(
            eligibility,
            snapshots,
            new SpawnAnchorResolver(config),
            new LabApiExactRoleChangeExecutor(),
            sharedRequestGuard ?? new PerUserRequestGuard());
    }

    public IReadOnlyList<RoleTypeId> GetEligibleRoles(LabPlayer player, RoleControlSurface surface)
    {
        if (player == null)
        {
            return Array.Empty<RoleTypeId>();
        }

        try
        {
            var adapter = new LabApiRoleChangePlayer(player);
            return eligibility.GetEligibleRoles(snapshots.Capture(adapter), surface)
                .Select(candidate => Enum.TryParse(candidate.RoleId, true, out RoleTypeId role) ? role : RoleTypeId.None)
                .Where(role => role != RoleTypeId.None)
                .ToArray();
        }
        catch
        {
            return Array.Empty<RoleTypeId>();
        }
    }

    public IReadOnlyList<RoleTypeId> GetConfiguredRoles(LabPlayer player, RoleControlSurface surface)
    {
        if (player == null)
        {
            return Array.Empty<RoleTypeId>();
        }

        try
        {
            var adapter = new LabApiRoleChangePlayer(player);
            return eligibility.GetConfiguredRoles(snapshots.Capture(adapter), surface)
                .Select(candidate => Enum.TryParse(candidate.RoleId, true, out RoleTypeId role) ? role : RoleTypeId.None)
                .Where(role => role != RoleTypeId.None)
                .ToArray();
        }
        catch
        {
            return Array.Empty<RoleTypeId>();
        }
    }

    public ControlResult TryChangeRole(
        LabPlayer player,
        string expectedFullUserId,
        RoleTypeId exactRole,
        RoleControlSurface surface)
    {
        if (player == null)
        {
            return ControlResult.Reject(ControlResultCode.InvalidRequest);
        }

        return changes.TryChangeRole(
            new LabApiRoleChangePlayer(player),
            new RoleChangeRequest(expectedFullUserId, exactRole.ToString(), surface));
    }
}

internal sealed class LabApiRoleChangePlayer : IRoleChangePlayer
{
    public LabApiRoleChangePlayer(LabPlayer player)
    {
        Player = player ?? throw new ArgumentNullException(nameof(player));
    }

    public LabPlayer Player { get; }

    public string FullUserId => Player.IsDestroyed ? string.Empty : Player.UserId ?? string.Empty;

    public string CurrentRoleId => Player.IsDestroyed ? string.Empty : Player.Role.ToString();
}

internal sealed class LabApiRoleSnapshotSource : IRoleEligibilitySnapshotSource
{
    private readonly ILabApiRoleControlAuthority authority;
    private readonly SpawnAnchorResolver anchors;
    private readonly RoleTypeId[] candidateRoles;

    public LabApiRoleSnapshotSource(RoleControlsConfig config, ILabApiRoleControlAuthority authority)
    {
        this.authority = authority;
        anchors = new SpawnAnchorResolver(config);
        candidateRoles = Enum.GetValues(typeof(RoleTypeId))
            .Cast<RoleTypeId>()
            .Where(IsPlayerSelectableRole)
            .Distinct()
            .OrderBy(role => role.ToString(), StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public RoleEligibilitySnapshot Capture(IRoleChangePlayer player)
    {
        if (player is not LabApiRoleChangePlayer adapter)
        {
            throw new ArgumentException("A LabAPI player adapter is required.", nameof(player));
        }

        LabPlayer native = adapter.Player;
        bool available = !native.IsDestroyed;
        ArenaPresetDefinition preset = authority.GetActivePreset(native)
            ?? throw new InvalidOperationException("The active arena preset cannot be null.");
        var candidates = new List<RoleCandidateDefinition>(candidateRoles.Length);

        foreach (RoleTypeId role in candidateRoles)
        {
            string roleId = role.ToString();
            bool assignable = role.TryGetRoleBase<PlayerRoleBase>(out _)
                && anchors.TryResolve(roleId, role.IsScp(), preset, out _);
            candidates.Add(new RoleCandidateDefinition(
                roleId,
                assignable && role.IsScp(),
                assignable,
                assignable && available));
        }

        string userId = available ? native.UserId ?? string.Empty : string.Empty;
        bool isRealPlayer = available && native.IsPlayer && !native.IsDummy && !native.IsHost;
        bool authenticated = available && native.IsReady && isRealPlayer && !string.IsNullOrWhiteSpace(userId);

        return new RoleEligibilitySnapshot(
            userId,
            authenticated,
            isRealPlayer,
            authority.IsWarmupActive,
            available && native.IsAlive,
            !available || native.Role == RoleTypeId.Spectator,
            available && native.Zone == FacilityZone.Surface,
            available && authenticated && authority.HasForceRolePermission(native),
            available ? native.Role.ToString() : string.Empty,
            preset,
            candidates);
    }

    private static bool IsPlayerSelectableRole(RoleTypeId role) =>
        WarmupRoleSelectionPolicy.IsPlayerSelectableRole(role.ToString());
}

internal sealed class LabApiExactRoleChangeExecutor : IExactRoleChangeExecutor
{
    public ExactRoleChangeExecutionResult TrySetExactRole(
        IRoleChangePlayer player,
        string exactRoleId,
        RoleControlSurface surface,
        SpawnAnchor anchor)
    {
        if (player is not LabApiRoleChangePlayer adapter
            || adapter.Player.IsDestroyed
            || !Enum.TryParse(exactRoleId, true, out RoleTypeId exactRole)
            || !Enum.IsDefined(typeof(RoleTypeId), exactRole))
        {
            return ExactRoleChangeExecutionResult.Failed;
        }

        LabPlayer native = adapter.Player;
        RoleTypeId originalRole = native.Role;
        Vector3 originalPosition = native.Position;
        Vector2 originalLookRotation = native.LookRotation;
        RoleChangeReason reason = surface == RoleControlSurface.AdminForce
            ? RoleChangeReason.RemoteAdmin
            : RoleChangeReason.Respawn;

        try
        {
            // The anchor has already been resolved. Disable native random placement but retain inventory.
            native.SetRole(exactRole, reason, RoleSpawnFlags.AssignInventory);
            if (native.IsDestroyed)
            {
                return ExactRoleChangeExecutionResult.Failed;
            }

            if (native.Role != exactRole)
            {
                // A cancellation leaves the original role intact. A substitution is rolled back explicitly.
                return native.Role == originalRole
                    ? ExactRoleChangeExecutionResult.Failed
                    : TryRollback(native, originalRole, originalPosition, originalLookRotation);
            }

            native.Position = new Vector3(anchor.X, anchor.Y, anchor.Z);
            native.LookRotation = new Vector2(0f, anchor.HorizontalRotation);
            if (!native.IsDestroyed && native.Role == exactRole)
            {
                return ExactRoleChangeExecutionResult.Succeeded;
            }

            return TryRollback(native, originalRole, originalPosition, originalLookRotation);
        }
        catch
        {
            return TryRollback(native, originalRole, originalPosition, originalLookRotation);
        }
    }

    private static ExactRoleChangeExecutionResult TryRollback(
        LabPlayer player,
        RoleTypeId originalRole,
        Vector3 originalPosition,
        Vector2 originalLookRotation)
    {
        try
        {
            if (player.IsDestroyed)
            {
                return ExactRoleChangeExecutionResult.MismatchRollbackFailed;
            }

            if (player.Role != originalRole)
            {
                player.SetRole(originalRole, RoleChangeReason.RemoteAdmin, RoleSpawnFlags.AssignInventory);
            }

            if (player.IsDestroyed || player.Role != originalRole)
            {
                return ExactRoleChangeExecutionResult.MismatchRollbackFailed;
            }

            if (originalRole is not RoleTypeId.None and not RoleTypeId.Spectator and not RoleTypeId.Destroyed)
            {
                player.Position = originalPosition;
                player.LookRotation = originalLookRotation;
            }

            return ExactRoleChangeExecutionResult.MismatchRolledBack;
        }
        catch
        {
            return ExactRoleChangeExecutionResult.MismatchRollbackFailed;
        }
    }
}
