#nullable enable

using System;

namespace SCPSLBot.Warmup.Controls;

public readonly struct SpawnAnchor
{
    public SpawnAnchor(float x, float y, float z, float horizontalRotation)
    {
        X = x;
        Y = y;
        Z = z;
        HorizontalRotation = horizontalRotation;
    }

    public float X { get; }

    public float Y { get; }

    public float Z { get; }

    public float HorizontalRotation { get; }
}

public sealed class RoleChangeRequest
{
    public RoleChangeRequest(string expectedFullUserId, string requestedRoleId, RoleControlSurface surface)
    {
        ExpectedFullUserId = expectedFullUserId ?? string.Empty;
        RequestedRoleId = requestedRoleId ?? string.Empty;
        Surface = surface;
    }

    public string ExpectedFullUserId { get; }

    public string RequestedRoleId { get; }

    public RoleControlSurface Surface { get; }
}

public interface IRoleChangePlayer
{
    string FullUserId { get; }

    string CurrentRoleId { get; }
}

public interface IRoleEligibilitySnapshotSource
{
    RoleEligibilitySnapshot Capture(IRoleChangePlayer player);
}

public interface ISpawnAnchorProvider
{
    bool TryResolve(
        string exactRoleId,
        bool isScp,
        ArenaPresetDefinition activePreset,
        out SpawnAnchor anchor);
}

public enum ExactRoleChangeExecutionResult
{
    Succeeded,
    Failed,
    MismatchRolledBack,
    MismatchRollbackFailed,
}

public interface IExactRoleChangeExecutor
{
    /// <summary>Assigns only exactRoleId and applies the already-resolved anchor. Never substitutes a role.</summary>
    ExactRoleChangeExecutionResult TrySetExactRole(
        IRoleChangePlayer player,
        string exactRoleId,
        RoleControlSurface surface,
        SpawnAnchor anchor);
}

/// <summary>
/// Server-authoritative exact-role transaction. Eligibility and the spawn anchor are resolved
/// immediately before the native change, and the resulting exact role is verified afterward.
/// </summary>
public sealed class RoleChangeService
{
    private readonly RoleEligibilityService eligibility;
    private readonly IRoleEligibilitySnapshotSource snapshots;
    private readonly ISpawnAnchorProvider anchors;
    private readonly IExactRoleChangeExecutor executor;
    private readonly PerUserRequestGuard requestGuard;

    public RoleChangeService(
        RoleEligibilityService eligibility,
        IRoleEligibilitySnapshotSource snapshots,
        ISpawnAnchorProvider anchors,
        IExactRoleChangeExecutor executor,
        PerUserRequestGuard requestGuard)
    {
        this.eligibility = eligibility ?? throw new ArgumentNullException(nameof(eligibility));
        this.snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
        this.anchors = anchors ?? throw new ArgumentNullException(nameof(anchors));
        this.executor = executor ?? throw new ArgumentNullException(nameof(executor));
        this.requestGuard = requestGuard ?? throw new ArgumentNullException(nameof(requestGuard));
    }

    public ControlResult TryChangeRole(IRoleChangePlayer player, RoleChangeRequest request)
    {
        if (player == null
            || request == null
            || string.IsNullOrWhiteSpace(request.ExpectedFullUserId)
            || string.IsNullOrWhiteSpace(request.RequestedRoleId))
        {
            return ControlResult.Reject(ControlResultCode.InvalidRequest);
        }

        if (!requestGuard.TryEnter(request.ExpectedFullUserId, out IDisposable? lease) || lease == null)
        {
            return ControlResult.Reject(ControlResultCode.ConcurrentRequest);
        }

        using (lease)
        {
            try
            {
                // Protect against a stale PlayerId/wrapper now belonging to another authenticated user.
                if (!string.Equals(player.FullUserId, request.ExpectedFullUserId, StringComparison.Ordinal))
                {
                    return ControlResult.Reject(ControlResultCode.InvalidRequest);
                }

                RoleEligibilitySnapshot snapshot = snapshots.Capture(player);
                if (!string.Equals(snapshot.FullUserId, request.ExpectedFullUserId, StringComparison.Ordinal))
                {
                    return ControlResult.Reject(ControlResultCode.InvalidRequest);
                }

                ControlResult allowed = eligibility.Evaluate(snapshot, request.Surface, request.RequestedRoleId);
                if (!allowed.Succeeded)
                {
                    return allowed;
                }

                if (!snapshot.TryGetCandidate(request.RequestedRoleId, out RoleCandidateDefinition? candidate)
                    || candidate == null
                    || !anchors.TryResolve(
                        candidate.RoleId,
                        candidate.IsScp,
                        snapshot.ActivePreset,
                        out SpawnAnchor anchor))
                {
                    return ControlResult.Reject(ControlResultCode.SpawnAnchorUnavailable, request.RequestedRoleId);
                }

                ExactRoleChangeExecutionResult execution = executor.TrySetExactRole(
                    player,
                    candidate.RoleId,
                    request.Surface,
                    anchor);
                if (execution == ExactRoleChangeExecutionResult.Failed)
                {
                    return ControlResult.Reject(ControlResultCode.RoleChangeFailed, candidate.RoleId);
                }

                if (execution == ExactRoleChangeExecutionResult.MismatchRolledBack)
                {
                    return ControlResult.Reject(ControlResultCode.ExactRoleMismatchRolledBack, candidate.RoleId);
                }

                if (execution == ExactRoleChangeExecutionResult.MismatchRollbackFailed)
                {
                    return ControlResult.Reject(ControlResultCode.ExactRoleMismatchRollbackFailed, candidate.RoleId);
                }

                return string.Equals(player.CurrentRoleId, candidate.RoleId, StringComparison.OrdinalIgnoreCase)
                    ? ControlResult.Success(candidate.RoleId)
                    : ControlResult.Reject(ControlResultCode.ExactRoleMismatch, candidate.RoleId);
            }
            catch
            {
                return ControlResult.Reject(ControlResultCode.RoleChangeFailed, request.RequestedRoleId);
            }
        }
    }
}
