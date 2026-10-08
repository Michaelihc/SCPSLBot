using System;

namespace SCPSLBot.Api;

/// <summary>The optional management plugin supplies arena isolation; AI keeps no server-mode ownership.</summary>
public static class BotServerPolicy
{
    public static Func<ReferenceHub, ReferenceHub, bool> CanFight { get; set; }
    public static Func<string> PopulationStatus { get; set; }
    public static Action ManagementStopping { get; set; }
    public static Func<bool> StandardWarmup { get; set; }
    public static bool IsStandardWarmup => StandardWarmup?.Invoke() == true;
    public static event Action ModeChanged;
    public static void NotifyModeChanged() => ModeChanged?.Invoke();
    public static bool AllowsCombat(ReferenceHub bot, ReferenceHub target) => CanFight?.Invoke(bot, target) ?? true;
}
