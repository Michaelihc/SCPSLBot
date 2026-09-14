#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;

namespace SCPSLBot.Warmup.Controls;

/// <summary>
/// Round-owned cooldown/counter state keyed only by full UserId plus stable catalog/group IDs.
/// It intentionally has no disconnect, death, spectator, role, teleport, or UI reset API.
/// </summary>
public sealed class CooldownLedger
{
    private readonly object sync = new();
    private readonly IMonotonicClock clock;
    private readonly Dictionary<UserCatalogKey, long> itemDeadlines = new();
    private readonly Dictionary<UserGroupKey, long> groupDeadlines = new();
    private readonly Dictionary<UserCatalogLifeKey, int> lifeCounts = new();
    private readonly Dictionary<UserCatalogKey, int> roundCounts = new();
    private string activeRoundId = string.Empty;

    public CooldownLedger(IMonotonicClock clock)
    {
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        if (clock.Frequency <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(clock), "Monotonic clock frequency must be positive.");
        }
    }

    public string ActiveRoundId
    {
        get
        {
            lock (sync)
            {
                return activeRoundId;
            }
        }
    }

    /// <summary>Starts a new round and clears only this ledger's round-owned state.</summary>
    public void BeginRound(string roundId)
    {
        if (string.IsNullOrWhiteSpace(roundId))
        {
            throw new ArgumentException("A stable round ID is required.", nameof(roundId));
        }

        lock (sync)
        {
            if (string.Equals(activeRoundId, roundId, StringComparison.Ordinal))
            {
                return;
            }

            activeRoundId = roundId;
            itemDeadlines.Clear();
            groupDeadlines.Clear();
            lifeCounts.Clear();
            roundCounts.Clear();
        }
    }

    public ControlResult GetAvailability(
        string roundId,
        string fullUserId,
        ItemCatalogEntry entry,
        string lifeId)
    {
        lock (sync)
        {
            return CheckLocked(roundId, fullUserId, entry, lifeId, clock.Timestamp);
        }
    }

    /// <summary>
    /// Atomically reserves an eligible grant while holding the ledger generation stable.
    /// Dispose without Commit after a failed native AddItem call; that consumes no state.
    /// </summary>
    internal bool TryReserve(
        string roundId,
        string fullUserId,
        ItemCatalogEntry entry,
        string lifeId,
        out Reservation? reservation,
        out ControlResult result)
    {
        reservation = null;
        Monitor.Enter(sync);
        try
        {
            result = CheckLocked(roundId, fullUserId, entry, lifeId, clock.Timestamp);
            if (!result.Succeeded)
            {
                Monitor.Exit(sync);
                return false;
            }

            reservation = new Reservation(this, fullUserId, entry, lifeId);
            return true;
        }
        catch
        {
            Monitor.Exit(sync);
            throw;
        }
    }

    private ControlResult CheckLocked(
        string roundId,
        string fullUserId,
        ItemCatalogEntry entry,
        string lifeId,
        long now)
    {
        if (entry == null || string.IsNullOrWhiteSpace(fullUserId))
        {
            return ControlResult.Reject(ControlResultCode.InvalidRequest);
        }

        if (string.IsNullOrWhiteSpace(roundId)
            || !string.Equals(activeRoundId, roundId, StringComparison.Ordinal))
        {
            return ControlResult.Reject(ControlResultCode.RoundStateUnavailable);
        }

        var itemKey = new UserCatalogKey(fullUserId, entry.StableId);
        if (TryGetRemainingLocked(itemDeadlines, itemKey, now, out double itemRemaining))
        {
            return ControlResult.Reject(ControlResultCode.ItemCooldown, entry.StableId, itemRemaining);
        }

        if (!string.IsNullOrWhiteSpace(entry.SharedCooldownGroup))
        {
            var groupKey = new UserGroupKey(fullUserId, entry.SharedCooldownGroup);
            if (TryGetRemainingLocked(groupDeadlines, groupKey, now, out double groupRemaining))
            {
                return ControlResult.Reject(ControlResultCode.GroupCooldown, entry.SharedCooldownGroup, groupRemaining);
            }
        }

        if (entry.PerLifeLimit > 0)
        {
            if (string.IsNullOrWhiteSpace(lifeId))
            {
                return ControlResult.Reject(ControlResultCode.InvalidRequest);
            }

            var lifeKey = new UserCatalogLifeKey(fullUserId, entry.StableId, lifeId);
            if (lifeCounts.TryGetValue(lifeKey, out int lifeCount) && lifeCount >= entry.PerLifeLimit)
            {
                return ControlResult.Reject(ControlResultCode.LifeLimitReached, entry.StableId);
            }
        }

        if (entry.PerRoundLimit > 0
            && roundCounts.TryGetValue(itemKey, out int roundCount)
            && roundCount >= entry.PerRoundLimit)
        {
            return ControlResult.Reject(ControlResultCode.RoundLimitReached, entry.StableId);
        }

        return ControlResult.Success(entry.StableId);
    }

    private bool TryGetRemainingLocked<TKey>(
        Dictionary<TKey, long> deadlines,
        TKey key,
        long now,
        out double remainingSeconds)
        where TKey : notnull
    {
        if (!deadlines.TryGetValue(key, out long deadline) || deadline <= now)
        {
            deadlines.Remove(key);
            remainingSeconds = 0d;
            return false;
        }

        remainingSeconds = (deadline - now) / (double)clock.Frequency;
        return true;
    }

    private void CommitLocked(string fullUserId, ItemCatalogEntry entry, string lifeId)
    {
        long now = clock.Timestamp;
        var itemKey = new UserCatalogKey(fullUserId, entry.StableId);
        SetDeadline(itemDeadlines, itemKey, now, entry.CooldownSeconds);

        if (!string.IsNullOrWhiteSpace(entry.SharedCooldownGroup))
        {
            SetDeadline(
                groupDeadlines,
                new UserGroupKey(fullUserId, entry.SharedCooldownGroup),
                now,
                entry.SharedCooldownSeconds);
        }

        if (entry.PerLifeLimit > 0)
        {
            var lifeKey = new UserCatalogLifeKey(fullUserId, entry.StableId, lifeId);
            lifeCounts[lifeKey] = lifeCounts.TryGetValue(lifeKey, out int count) ? count + 1 : 1;
        }

        if (entry.PerRoundLimit > 0)
        {
            roundCounts[itemKey] = roundCounts.TryGetValue(itemKey, out int count) ? count + 1 : 1;
        }
    }

    private void SetDeadline<TKey>(Dictionary<TKey, long> deadlines, TKey key, long now, double seconds)
        where TKey : notnull
    {
        if (seconds <= 0d)
        {
            deadlines.Remove(key);
            return;
        }

        double delta = seconds * clock.Frequency;
        long deadline = delta >= long.MaxValue - now ? long.MaxValue : now + (long)Math.Ceiling(delta);
        deadlines[key] = deadline;
    }

    internal sealed class Reservation : IDisposable
    {
        private CooldownLedger? owner;
        private readonly string fullUserId;
        private readonly ItemCatalogEntry entry;
        private readonly string lifeId;
        private bool committed;

        internal Reservation(CooldownLedger owner, string fullUserId, ItemCatalogEntry entry, string lifeId)
        {
            this.owner = owner;
            this.fullUserId = fullUserId;
            this.entry = entry;
            this.lifeId = lifeId;
        }

        public void Commit()
        {
            CooldownLedger? current = owner;
            if (current == null || committed)
            {
                throw new InvalidOperationException("The cooldown reservation is no longer active.");
            }

            current.CommitLocked(fullUserId, entry, lifeId);
            committed = true;
        }

        public void Dispose()
        {
            CooldownLedger? current = owner;
            owner = null;
            if (current != null)
            {
                Monitor.Exit(current.sync);
            }
        }
    }

    private readonly struct UserCatalogKey : IEquatable<UserCatalogKey>
    {
        public UserCatalogKey(string userId, string catalogId)
        {
            UserId = userId;
            CatalogId = catalogId;
        }

        private string UserId { get; }

        private string CatalogId { get; }

        public bool Equals(UserCatalogKey other) =>
            string.Equals(UserId, other.UserId, StringComparison.Ordinal)
            && string.Equals(CatalogId, other.CatalogId, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is UserCatalogKey other && Equals(other);

        public override int GetHashCode() => CombineHash(UserId, CatalogId);
    }

    private readonly struct UserGroupKey : IEquatable<UserGroupKey>
    {
        public UserGroupKey(string userId, string groupId)
        {
            UserId = userId;
            GroupId = groupId;
        }

        private string UserId { get; }

        private string GroupId { get; }

        public bool Equals(UserGroupKey other) =>
            string.Equals(UserId, other.UserId, StringComparison.Ordinal)
            && string.Equals(GroupId, other.GroupId, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is UserGroupKey other && Equals(other);

        public override int GetHashCode() => CombineHash(UserId, GroupId);
    }

    private readonly struct UserCatalogLifeKey : IEquatable<UserCatalogLifeKey>
    {
        public UserCatalogLifeKey(string userId, string catalogId, string lifeId)
        {
            UserId = userId;
            CatalogId = catalogId;
            LifeId = lifeId;
        }

        private string UserId { get; }

        private string CatalogId { get; }

        private string LifeId { get; }

        public bool Equals(UserCatalogLifeKey other) =>
            string.Equals(UserId, other.UserId, StringComparison.Ordinal)
            && string.Equals(CatalogId, other.CatalogId, StringComparison.Ordinal)
            && string.Equals(LifeId, other.LifeId, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is UserCatalogLifeKey other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                return (CombineHash(UserId, CatalogId) * 397) ^ StringComparer.Ordinal.GetHashCode(LifeId);
            }
        }
    }

    private static int CombineHash(string first, string second)
    {
        unchecked
        {
            return (StringComparer.Ordinal.GetHashCode(first) * 397)
                ^ StringComparer.Ordinal.GetHashCode(second);
        }
    }
}
