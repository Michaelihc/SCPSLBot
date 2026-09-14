using System;
using System.Collections.Generic;
using LabApi.Features.Wrappers;

namespace ScpslPluginStarter.Services;

internal sealed class OwnedDamageRegistry
{
    private readonly HashSet<int> _playerIds = new();

    public bool Contains(int playerId) => _playerIds.Contains(playerId);

    public void Apply(int playerId, Action action)
    {
        _playerIds.Add(playerId);
        try
        {
            action();
        }
        finally
        {
            _playerIds.Remove(playerId);
        }
    }

    /// <summary>
    /// Applies a configured health drain through the native damage pipeline, then makes up only
    /// the portion absorbed by AHP/Hume Shield. This keeps cancellation, godmode, death, logs, and
    /// other damage events native while preventing regenerating Hume Shield from turning a health
    /// drain into an indefinite Surface-blocker bypass.
    /// </summary>
    public bool ApplyHealthDrain(Player player, float amount, string reason)
    {
        if (player == null || player.IsDestroyed || !player.IsAlive || amount <= 0f)
        {
            return false;
        }

        float healthBefore = player.Health;
        bool applied = false;
        Apply(player.PlayerId, () => applied = player.Damage(amount, reason, string.Empty));
        if (!applied || player.IsDestroyed || !player.IsAlive)
        {
            return applied;
        }

        float healthDamage = Math.Max(0f, healthBefore - player.Health);
        float absorbed = Math.Max(0f, amount - healthDamage);
        if (absorbed <= 0.001f)
        {
            return true;
        }

        if (player.Health > absorbed)
        {
            player.Health -= absorbed;
            return true;
        }

        Apply(player.PlayerId, () => player.Kill(reason, string.Empty));
        return true;
    }

    public void Clear() => _playerIds.Clear();
}
