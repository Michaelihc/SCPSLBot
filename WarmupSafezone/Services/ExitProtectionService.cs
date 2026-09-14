using ScpslPluginStarter.Core;

namespace ScpslPluginStarter.Services;

internal sealed class ExitProtectionService
{
    private readonly WarmupSafezoneConfig _config;
    private readonly IMonotonicClock _clock;
    private readonly ExitProtectionTracker _tracker = new();

    public ExitProtectionService(WarmupSafezoneConfig config, IMonotonicClock clock)
    {
        _config = config;
        _clock = clock;
    }

    public void Grant(int playerId)
    {
        if (_config.SafezoneExitSpawnProtectionEnabled)
        {
            _tracker.Grant(playerId, _clock.NowMilliseconds, _config.SafezoneExitSpawnProtectionDurationMs);
        }
    }

    public bool IsProtected(int playerId) => _config.SafezoneExitSpawnProtectionEnabled
        && _tracker.IsProtected(playerId, _clock.NowMilliseconds);

    public void Forget(int playerId) => _tracker.Forget(playerId);
    public void Reset() => _tracker.Clear();
}
