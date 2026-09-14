#nullable enable

using System;
using System.Globalization;

namespace SCPSLBot.Warmup.Controls;

/// <summary>
/// Stable result codes shared by the warmup role and item control surfaces.
/// Presentation layers should localize these codes at the last possible moment.
/// </summary>
public enum ControlResultCode
{
    Success,
    InvalidRequest,
    Unauthenticated,
    NotRealPlayer,
    WarmupInactive,
    PlayerUnavailable,
    ArenaSwitchRejected,
    NotAlive,
    SpectatorHasNoRegularRoles,
    PermissionDenied,
    RoleNotConfigured,
    RoleUnavailable,
    ScpForbiddenOnSurface,
    ScpForbiddenByPreset,
    SpawnAnchorUnavailable,
    RoleChangeFailed,
    ExactRoleMismatch,
    ExactRoleMismatchRolledBack,
    ExactRoleMismatchRollbackFailed,
    CatalogEntryNotFound,
    ItemNotAllowedByPreset,
    RoleCannotRequestItem,
    ZoneCannotRequestItem,
    InventoryFull,
    ItemCooldown,
    GroupCooldown,
    LifeLimitReached,
    RoundLimitReached,
    RoundStateUnavailable,
    ConcurrentRequest,
    ActionRateLimited,
    ItemGrantFailed,
}

/// <summary>
/// A transport-neutral policy result. RemainingSeconds is meaningful for cooldown results.
/// Detail is a stable role, catalog, or group ID and must not be treated as localized text.
/// </summary>
public sealed class ControlResult
{
    private ControlResult(ControlResultCode code, bool succeeded, double remainingSeconds, string detail)
    {
        Code = code;
        Succeeded = succeeded;
        RemainingSeconds = Math.Max(0d, remainingSeconds);
        Detail = detail ?? string.Empty;
    }

    public ControlResultCode Code { get; }

    public bool Succeeded { get; }

    public double RemainingSeconds { get; }

    public string Detail { get; }

    public int RoundedRemainingSeconds => (int)Math.Ceiling(RemainingSeconds);

    public static ControlResult Success(string detail = "") =>
        new(ControlResultCode.Success, true, 0d, detail);

    public static ControlResult Reject(
        ControlResultCode code,
        string detail = "",
        double remainingSeconds = 0d) =>
        new(code, false, remainingSeconds, detail);

    public string Localize(string configuredLanguage, string? clientLanguage = null) =>
        ControlResultLocalizer.Localize(this, configuredLanguage, clientLanguage);
}

/// <summary>
/// EN/CN text for native per-player feedback. An empty language follows the client and falls back to Chinese.
/// </summary>
public static class ControlResultLocalizer
{
    public static string Localize(ControlResult result, string configuredLanguage, string? clientLanguage = null)
    {
        bool chinese = ResolveChinese(configuredLanguage, clientLanguage);
        string detail = string.IsNullOrWhiteSpace(result.Detail) ? "-" : result.Detail;
        int seconds = result.RoundedRemainingSeconds;

        if (chinese)
        {
            return result.Code switch
            {
                ControlResultCode.Success => $"操作成功：{detail}。",
                ControlResultCode.InvalidRequest => "请求无效或已过期。",
                ControlResultCode.Unauthenticated => "身份验证未完成，无法执行此操作。",
                ControlResultCode.NotRealPlayer => "只有已连接的真实玩家可以执行此操作。",
                ControlResultCode.WarmupInactive => "热身模式当前未启用。",
                ControlResultCode.PlayerUnavailable => "玩家状态已变化，请重试。",
                ControlResultCode.ArenaSwitchRejected => $"竞技场切换失败：{detail}",
                ControlResultCode.NotAlive => "你必须存活才能请求物品。",
                ControlResultCode.SpectatorHasNoRegularRoles => "观察者不能使用普通角色选择。",
                ControlResultCode.PermissionDenied => "你没有使用管理员强制角色功能的权限。",
                ControlResultCode.RoleNotConfigured => "服务器未允许该角色。",
                ControlResultCode.RoleUnavailable => "该角色当前不可用。",
                ControlResultCode.ScpForbiddenOnSurface => "地表区域不允许普通玩家选择 SCP。",
                ControlResultCode.ScpForbiddenByPreset => "当前预设不允许玩家选择 SCP。",
                ControlResultCode.SpawnAnchorUnavailable => "无法解析该角色的安全出生点。",
                ControlResultCode.RoleChangeFailed => "角色切换被取消或失败。",
                ControlResultCode.ExactRoleMismatch => "角色切换结果不匹配，未使用替代角色。",
                ControlResultCode.ExactRoleMismatchRolledBack => "角色切换结果不匹配，已恢复原角色。",
                ControlResultCode.ExactRoleMismatchRollbackFailed => "角色切换结果不匹配，且恢复原角色失败。",
                ControlResultCode.CatalogEntryNotFound => "该物品请求不存在或已失效。",
                ControlResultCode.ItemNotAllowedByPreset => "当前预设不允许该物品。",
                ControlResultCode.RoleCannotRequestItem => "当前角色不能请求该物品。",
                ControlResultCode.ZoneCannotRequestItem => "当前区域不能请求该物品。",
                ControlResultCode.InventoryFull => "背包已满。",
                ControlResultCode.ItemCooldown => $"该物品冷却中，还需 {seconds.ToString(CultureInfo.InvariantCulture)} 秒。",
                ControlResultCode.GroupCooldown => $"同组物品冷却中，还需 {seconds.ToString(CultureInfo.InvariantCulture)} 秒。",
                ControlResultCode.LifeLimitReached => "本次生命的请求次数已达上限。",
                ControlResultCode.RoundLimitReached => "本回合的请求次数已达上限。",
                ControlResultCode.RoundStateUnavailable => "回合状态已变化，请重新打开设置。",
                ControlResultCode.ConcurrentRequest => "已有一个请求正在处理，请勿重复操作。",
                ControlResultCode.ActionRateLimited => $"操作过快，请等待 {seconds.ToString(CultureInfo.InvariantCulture)} 秒。",
                ControlResultCode.ItemGrantFailed => "服务器未能添加该物品，未消耗冷却。",
                _ => "请求被服务器拒绝。",
            };
        }

        return result.Code switch
        {
            ControlResultCode.Success => $"Action completed: {detail}.",
            ControlResultCode.InvalidRequest => "The request is invalid or stale.",
            ControlResultCode.Unauthenticated => "Authentication is not complete.",
            ControlResultCode.NotRealPlayer => "Only a connected real player can use this control.",
            ControlResultCode.WarmupInactive => "Warmup is not active.",
            ControlResultCode.PlayerUnavailable => "Your player state changed; try again.",
            ControlResultCode.ArenaSwitchRejected => $"Arena switch failed: {detail}",
            ControlResultCode.NotAlive => "You must be alive to request an item.",
            ControlResultCode.SpectatorHasNoRegularRoles => "Spectators cannot use regular role selection.",
            ControlResultCode.PermissionDenied => "You do not have permission to force a role.",
            ControlResultCode.RoleNotConfigured => "That role is not allowed by server configuration.",
            ControlResultCode.RoleUnavailable => "That exact role is not available now.",
            ControlResultCode.ScpForbiddenOnSurface => "Regular players cannot select an SCP on Surface.",
            ControlResultCode.ScpForbiddenByPreset => "The active preset does not allow player SCPs.",
            ControlResultCode.SpawnAnchorUnavailable => "A safe spawn anchor could not be resolved for that role.",
            ControlResultCode.RoleChangeFailed => "The role change was cancelled or failed.",
            ControlResultCode.ExactRoleMismatch => "The resulting role did not match; no fallback was used.",
            ControlResultCode.ExactRoleMismatchRolledBack => "The resulting role did not match; the original role was restored.",
            ControlResultCode.ExactRoleMismatchRollbackFailed => "The resulting role did not match and restoring the original role failed.",
            ControlResultCode.CatalogEntryNotFound => "That item request does not exist or is stale.",
            ControlResultCode.ItemNotAllowedByPreset => "The active preset does not allow that item.",
            ControlResultCode.RoleCannotRequestItem => "Your current role cannot request that item.",
            ControlResultCode.ZoneCannotRequestItem => "That item cannot be requested in your current zone.",
            ControlResultCode.InventoryFull => "Your inventory is full.",
            ControlResultCode.ItemCooldown => $"That item is on cooldown for {seconds.ToString(CultureInfo.InvariantCulture)} more seconds.",
            ControlResultCode.GroupCooldown => $"That item group is on cooldown for {seconds.ToString(CultureInfo.InvariantCulture)} more seconds.",
            ControlResultCode.LifeLimitReached => "You reached this item's per-life limit.",
            ControlResultCode.RoundLimitReached => "You reached this item's per-round limit.",
            ControlResultCode.RoundStateUnavailable => "The round changed; reopen the settings panel.",
            ControlResultCode.ConcurrentRequest => "Another request is already in progress.",
            ControlResultCode.ActionRateLimited => $"Actions are rate-limited; wait {seconds.ToString(CultureInfo.InvariantCulture)} more seconds.",
            ControlResultCode.ItemGrantFailed => "The server could not add the item; no cooldown was consumed.",
            _ => "The server rejected the request.",
        };
    }

    public static bool ResolveChinese(string configuredLanguage, string? clientLanguage)
    {
        if (string.Equals(configuredLanguage, "en", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(configuredLanguage, "cn", StringComparison.OrdinalIgnoreCase)
            || string.Equals(configuredLanguage, "zh", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(configuredLanguage))
        {
            return true;
        }

        string resolvedClientLanguage = clientLanguage ?? string.Empty;
        return string.IsNullOrWhiteSpace(resolvedClientLanguage)
            || resolvedClientLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            || resolvedClientLanguage.StartsWith("cn", StringComparison.OrdinalIgnoreCase);
    }
}
