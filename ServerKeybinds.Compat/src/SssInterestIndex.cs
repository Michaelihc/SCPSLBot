using System;
using System.Collections.Generic;

namespace ServerKeybinds;

/// <summary>State domains that can change a player's personalized SSS view.</summary>
[Flags]
public enum SssInterest
{
    None = 0,
    Role = 1 << 0,
    Item = 1 << 1,
    Cooldown = 1 << 2,
    Title = 1 << 3,
    Language = 1 << 4,
    Zone = 1 << 5,
    Permission = 1 << 6,
    Display = 1 << 7,
    WarmupMode = 1 << 8,
    PopulationBoundary = 1 << 9,
    AllPersonal = Role | Item | Cooldown | Title | Language | Zone | Permission | Display | WarmupMode,
    All = AllPersonal | PopulationBoundary,
}

/// <summary>
/// Pure interest router. Personal events can only return their affected player. Population fan-out is
/// deliberately limited to the 1-to-2 and 2-to-1 visibility boundaries.
/// </summary>
public sealed class SssInterestIndex<TKey> where TKey : notnull
{
    private readonly Dictionary<TKey, SssInterest> _interests = new();
    private readonly IEqualityComparer<TKey> _comparer;

    public SssInterestIndex(IEqualityComparer<TKey>? comparer = null)
    {
        _comparer = comparer ?? EqualityComparer<TKey>.Default;
    }

    public int Count => _interests.Count;

    public void Track(TKey player, SssInterest interests = SssInterest.All) => _interests[player] = interests;

    public bool Untrack(TKey player) => _interests.Remove(player);

    public void Clear() => _interests.Clear();

    public IReadOnlyList<TKey> ResolvePersonal(TKey player, SssInterest changed)
    {
        return _interests.TryGetValue(player, out SssInterest interests) && (interests & changed) != 0
            ? new[] { player }
            : Array.Empty<TKey>();
    }

    /// <summary>
    /// Resolves existing players whose non-admin global-control visibility changed. A joining player gets
    /// its ordinary join send separately and is therefore never returned here.
    /// </summary>
    public IReadOnlyList<TKey> ResolvePopulationBoundary(
        IReadOnlyCollection<TKey> before,
        IReadOnlyCollection<TKey> after)
    {
        if (before == null)
        {
            throw new ArgumentNullException(nameof(before));
        }

        if (after == null)
        {
            throw new ArgumentNullException(nameof(after));
        }

        if (!((before.Count == 1 && after.Count == 2) || (before.Count == 2 && after.Count == 1)))
        {
            return Array.Empty<TKey>();
        }

        HashSet<TKey> afterSet = new(after, _comparer);
        List<TKey> affected = new(1);
        foreach (TKey player in before)
        {
            if (afterSet.Contains(player)
                && _interests.TryGetValue(player, out SssInterest interests)
                && (interests & SssInterest.PopulationBoundary) != 0)
            {
                affected.Add(player);
            }
        }

        return affected;
    }
}
