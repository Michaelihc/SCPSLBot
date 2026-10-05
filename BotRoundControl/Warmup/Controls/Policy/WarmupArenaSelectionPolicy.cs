namespace SCPSLBot.Warmup.Controls;

public static class WarmupArenaSelectionPolicy
{
    public static bool RequiresNativeTransition(bool isAlreadyActive, bool isExactSpectator) =>
        !isAlreadyActive || isExactSpectator;

    public static bool IsSwitchCooldownApplicable(bool isAlreadyActive) =>
        !isAlreadyActive;
}
