using System;

namespace SCPSLBot.Api;

/// <summary>The optional management plugin supplies arena isolation; AI keeps no server-mode ownership.</summary>
public static class BotServerPolicy
{
    public static Func<ReferenceHub, ReferenceHub, bool> CanFight { get; set; }
    public static Func<string> PopulationStatus { get; set; }
    public static bool AllowsCombat(ReferenceHub bot, ReferenceHub target) => CanFight?.Invoke(bot, target) ?? true;
}
