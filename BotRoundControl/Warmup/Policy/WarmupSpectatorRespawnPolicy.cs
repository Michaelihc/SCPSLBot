using System;

namespace SCPSLBot.Warmup.Policy;

internal enum SpectatorRespawnSource
{
    None,
    JoinOrRecovery,
    Death,
}

internal static class WarmupSpectatorRespawnPolicy
{
    public static bool IsEligiblePlayerState(bool isRealPlayer, bool isExactSpectator) =>
        isRealPlayer && isExactSpectator;

    public static SpectatorRespawnSource ClassifyTransition(
        bool currentIsSpectator,
        bool previousWasSpectator,
        bool previousWasPlayable)
    {
        if (!currentIsSpectator || previousWasSpectator)
        {
            return SpectatorRespawnSource.None;
        }

        return previousWasPlayable
            ? SpectatorRespawnSource.Death
            : SpectatorRespawnSource.JoinOrRecovery;
    }

    public static float DelaySeconds(
        SpectatorRespawnSource source,
        int humanRespawnDelayMilliseconds,
        int spectatorRespawnDelayMilliseconds)
    {
        int milliseconds = source == SpectatorRespawnSource.Death
            ? humanRespawnDelayMilliseconds
            : spectatorRespawnDelayMilliseconds;
        return Math.Max(50, milliseconds) / 1000f;
    }

    public static float NormalizeScanInterval(float seconds) =>
        Math.Max(0.1f, Math.Min(5f, seconds));

    public static float RetryDelaySeconds(float scanIntervalSeconds) =>
        Math.Max(0.5f, NormalizeScanInterval(scanIntervalSeconds));
}
