using SCPSLBot.Infrastructure;
using Xunit;

namespace SCPSLBot.PolicyTests;

public sealed class ManagedReferenceComparerTests
{
    private sealed class NativeLifetimeKey(int playerId)
    {
        public bool Destroyed { get; set; }
        public override int GetHashCode() => Destroyed ? throw new InvalidOperationException("Native object destroyed") : playerId;
        public override bool Equals(object? obj) => throw new InvalidOperationException("Native equality must not be called");
    }

    [Fact]
    public void DestroyedKeyRemainsRemovableAndDoesNotAliasRecycledPlayerId()
    {
        var comparer = ManagedReferenceComparer<NativeLifetimeKey>.Instance;
        var destroyed = new NativeLifetimeKey(7);
        var replacement = new NativeLifetimeKey(7);
        var dictionary = new Dictionary<NativeLifetimeKey, string>(comparer) { [destroyed] = "old" };
        var pending = new HashSet<NativeLifetimeKey>(comparer) { destroyed };
        int initialHash = comparer.GetHashCode(destroyed);
        destroyed.Destroyed = true;
        dictionary.Add(replacement, "new");
        pending.Add(replacement);

        Assert.Equal(initialHash, comparer.GetHashCode(destroyed));
        Assert.False(comparer.Equals(destroyed, replacement));
        Assert.True(dictionary.Remove(destroyed));
        Assert.True(pending.Remove(destroyed));
        Assert.Equal("new", Assert.Single(dictionary).Value);
        Assert.Same(replacement, Assert.Single(pending));
        Assert.False(dictionary.Remove(destroyed));
    }
}
