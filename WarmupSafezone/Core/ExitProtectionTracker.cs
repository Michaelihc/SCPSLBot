using System.Collections.Generic;

namespace ScpslPluginStarter.Core;

internal sealed class ExitProtectionTracker
{
    private readonly Dictionary<int, long> _expiresAtByPlayer = new();

    public void Grant(int playerId, long nowMilliseconds, int durationMilliseconds)
    {
        if (durationMilliseconds <= 0)
        {
            return;
        }

        long candidate = MonotonicDeadline.After(nowMilliseconds, durationMilliseconds);
        if (!_expiresAtByPlayer.TryGetValue(playerId, out long current) || candidate > current)
        {
            _expiresAtByPlayer[playerId] = candidate;
        }
    }

    public bool IsProtected(int playerId, long nowMilliseconds)
    {
        if (!_expiresAtByPlayer.TryGetValue(playerId, out long expiresAt))
        {
            return false;
        }

        if (nowMilliseconds < expiresAt)
        {
            return true;
        }

        _expiresAtByPlayer.Remove(playerId);
        return false;
    }

    public void Forget(int playerId) => _expiresAtByPlayer.Remove(playerId);
    public void Clear() => _expiresAtByPlayer.Clear();
}
