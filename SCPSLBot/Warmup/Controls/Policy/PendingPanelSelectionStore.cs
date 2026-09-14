#nullable enable

using System;
using System.Collections.Generic;

namespace SCPSLBot.Warmup.Controls.Policy;

public enum PendingPanelAction
{
    Role,
    Item,
    Arena,
}

/// <summary>
/// Holds non-authoritative SSS selections until the matching explicit action button is pressed.
/// Numeric player ids are always paired with the full authenticated UserId to prevent reconnect reuse.
/// </summary>
public sealed class PendingPanelSelectionStore
{
    private readonly Dictionary<PendingKey, string> selections = new();

    public void Stage(int playerId, string fullUserId, PendingPanelAction action, string stableValue)
    {
        ValidateIdentity(fullUserId);
        PendingKey key = new(playerId, fullUserId, action);
        if (string.IsNullOrWhiteSpace(stableValue))
        {
            selections.Remove(key);
            return;
        }

        selections[key] = stableValue;
    }

    public bool TryGet(
        int playerId,
        string fullUserId,
        PendingPanelAction action,
        out string stableValue)
    {
        stableValue = string.Empty;
        if (string.IsNullOrWhiteSpace(fullUserId))
        {
            return false;
        }

        return selections.TryGetValue(new PendingKey(playerId, fullUserId, action), out stableValue!);
    }

    public void Clear(int playerId, string fullUserId, PendingPanelAction action)
    {
        if (!string.IsNullOrWhiteSpace(fullUserId))
        {
            selections.Remove(new PendingKey(playerId, fullUserId, action));
        }
    }

    public void ForgetPlayer(int playerId)
    {
        foreach (PendingKey key in new List<PendingKey>(selections.Keys))
        {
            if (key.PlayerId == playerId)
            {
                selections.Remove(key);
            }
        }
    }

    public void ClearAll() => selections.Clear();

    private static void ValidateIdentity(string fullUserId)
    {
        if (string.IsNullOrWhiteSpace(fullUserId))
        {
            throw new ArgumentException("A full authenticated UserId is required.", nameof(fullUserId));
        }
    }

    private readonly struct PendingKey : IEquatable<PendingKey>
    {
        public PendingKey(int playerId, string fullUserId, PendingPanelAction action)
        {
            PlayerId = playerId;
            FullUserId = fullUserId;
            Action = action;
        }

        public int PlayerId { get; }
        private string FullUserId { get; }
        private PendingPanelAction Action { get; }

        public bool Equals(PendingKey other) =>
            PlayerId == other.PlayerId
            && Action == other.Action
            && string.Equals(FullUserId, other.FullUserId, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is PendingKey other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = PlayerId;
                hash = (hash * 397) ^ (int)Action;
                return (hash * 397) ^ StringComparer.Ordinal.GetHashCode(FullUserId);
            }
        }
    }
}
