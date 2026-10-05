#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace SCPSLBot.Warmup.Controls;

public enum RoleControlSurface
{
    Regular,
    AdminForce,
}

public sealed class RoleCandidateDefinition
{
    public RoleCandidateDefinition(
        string roleId,
        bool isScp,
        bool isNativeAssignable,
        bool isAvailableUnderRegularCapacity,
        bool isAdminOnly = false)
    {
        RoleId = roleId ?? string.Empty;
        IsScp = isScp;
        IsNativeAssignable = isNativeAssignable;
        IsAvailableUnderRegularCapacity = isAvailableUnderRegularCapacity;
        IsAdminOnly = isAdminOnly;
    }

    public string RoleId { get; }

    public bool IsScp { get; }

    public bool IsNativeAssignable { get; }

    public bool IsAvailableUnderRegularCapacity { get; }

    public bool IsAdminOnly { get; }
}

/// <summary>One authoritative server snapshot used for both SSS options and execution.</summary>
public sealed class RoleEligibilitySnapshot
{
    private readonly Dictionary<string, RoleCandidateDefinition> candidates;

    public RoleEligibilitySnapshot(
        string fullUserId,
        bool isAuthenticated,
        bool isRealPlayer,
        bool isWarmupActive,
        bool isAlive,
        bool isSpectator,
        bool isOnSurface,
        bool hasAdminForcePermission,
        string currentRoleId,
        ArenaPresetDefinition activePreset,
        IEnumerable<RoleCandidateDefinition> candidates)
    {
        FullUserId = fullUserId ?? string.Empty;
        IsAuthenticated = isAuthenticated;
        IsRealPlayer = isRealPlayer;
        IsWarmupActive = isWarmupActive;
        IsAlive = isAlive;
        IsSpectator = isSpectator;
        IsOnSurface = isOnSurface;
        HasAdminForcePermission = hasAdminForcePermission;
        CurrentRoleId = currentRoleId ?? string.Empty;
        ActivePreset = activePreset ?? throw new ArgumentNullException(nameof(activePreset));
        this.candidates = (candidates ?? throw new ArgumentNullException(nameof(candidates)))
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.RoleId))
            .GroupBy(candidate => candidate.RoleId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
    }

    public string FullUserId { get; }

    public bool IsAuthenticated { get; }

    public bool IsRealPlayer { get; }

    public bool IsWarmupActive { get; }

    public bool IsAlive { get; }

    public bool IsSpectator { get; }

    public bool IsOnSurface { get; }

    public bool HasAdminForcePermission { get; }

    public string CurrentRoleId { get; }

    public ArenaPresetDefinition ActivePreset { get; }

    public IReadOnlyCollection<RoleCandidateDefinition> Candidates => candidates.Values;

    public bool TryGetCandidate(string roleId, out RoleCandidateDefinition? candidate) =>
        candidates.TryGetValue(roleId ?? string.Empty, out candidate);
}

/// <summary>
/// Computes visible exact roles and rechecks a submitted role against the same rules.
/// It never replaces the requested role with a fallback.
/// </summary>
public sealed class RoleEligibilityService
{
    public RoleEligibilityService(RoleControlsConfig config)
    {
        if (config == null)
        {
            throw new ArgumentNullException(nameof(config));
        }

    }

    public IReadOnlyList<RoleCandidateDefinition> GetEligibleRoles(
        RoleEligibilitySnapshot snapshot,
        RoleControlSurface surface)
    {
        if (snapshot == null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }

        return snapshot.Candidates
            .Where(candidate => Evaluate(snapshot, surface, candidate.RoleId).Succeeded)
            .OrderBy(candidate => candidate.RoleId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Returns a stable configured slot list for presentation. Volatile capacity/current-role
    /// checks remain in <see cref="Evaluate"/> at execution time so an old numeric SSS index can
    /// never be reinterpreted as a different role after an earlier option disappears.
    /// </summary>
    public IReadOnlyList<RoleCandidateDefinition> GetConfiguredRoles(
        RoleEligibilitySnapshot snapshot,
        RoleControlSurface surface)
    {
        if (snapshot == null
            || !snapshot.IsAuthenticated
            || !snapshot.IsRealPlayer
            || !snapshot.IsWarmupActive)
        {
            return Array.Empty<RoleCandidateDefinition>();
        }

        if (surface == RoleControlSurface.AdminForce && !snapshot.HasAdminForcePermission)
        {
            return Array.Empty<RoleCandidateDefinition>();
        }

        return snapshot.Candidates
            .Where(candidate => candidate.IsNativeAssignable
                && !candidate.IsAdminOnly)
            .OrderBy(candidate => candidate.RoleId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public ControlResult Evaluate(
        RoleEligibilitySnapshot snapshot,
        RoleControlSurface surface,
        string requestedRoleId)
    {
        if (snapshot == null || string.IsNullOrWhiteSpace(requestedRoleId))
        {
            return ControlResult.Reject(ControlResultCode.InvalidRequest);
        }

        if (!snapshot.IsAuthenticated || string.IsNullOrWhiteSpace(snapshot.FullUserId))
        {
            return ControlResult.Reject(ControlResultCode.Unauthenticated);
        }

        if (!snapshot.IsRealPlayer)
        {
            return ControlResult.Reject(ControlResultCode.NotRealPlayer);
        }

        if (!snapshot.IsWarmupActive)
        {
            return ControlResult.Reject(ControlResultCode.WarmupInactive);
        }

        if (!snapshot.TryGetCandidate(requestedRoleId, out RoleCandidateDefinition? candidate)
            || candidate == null
            || !candidate.IsNativeAssignable)
        {
            return ControlResult.Reject(ControlResultCode.RoleUnavailable, requestedRoleId);
        }

        if (candidate.IsAdminOnly)
        {
            return ControlResult.Reject(ControlResultCode.RoleUnavailable, candidate.RoleId);
        }

        if (surface == RoleControlSurface.AdminForce)
        {
            return snapshot.HasAdminForcePermission
                ? ControlResult.Success(candidate.RoleId)
                : ControlResult.Reject(ControlResultCode.PermissionDenied);
        }

        return ControlResult.Success(candidate.RoleId);
    }
}
