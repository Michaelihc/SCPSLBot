using System.Collections.Generic;
using System.ComponentModel;
using SCPSLBot.Diagnostics;
using SCPSLBot.Navigation;
using SCPSLBot.Presentation;

namespace SCPSLBot;

public sealed class BotRuntimeConfig
{
    public bool EnableNetworkRegistryRecovery { get; set; } = true;
    [Description("Bot budget for bot_add and normal-round spawning. Counts all tracked SCPSLBot bots, including spectators. Zero disables these spawns; restart after changing.")]
    public int MaxBotCount { get; set; } = 10;
    public int NormalRoundBotCount { get; set; }
    public string Language { get; set; } = "cn";
    public HintDisplayConfig HintDisplay { get; set; } = new();
    public NavigationConfig Navigation { get; set; } = new();
    public RoundTraceConfig RoundTrace { get; set; } = new();
    public bool ForceStandardDoorConnectors { get; set; }
    internal IReadOnlyList<string> Normalize()
    {
        var changes = new List<string>();
        if (MaxBotCount < 0)
        {
            changes.Add($"MaxBotCount: {MaxBotCount} -> 0");
            MaxBotCount = 0;
        }
        int count = System.Math.Max(0, System.Math.Min(MaxBotCount, NormalRoundBotCount));
        if (count != NormalRoundBotCount) changes.Add($"NormalRoundBotCount: {NormalRoundBotCount} -> {count}");
        NormalRoundBotCount = count;
        HintDisplay ??= new();
        Navigation ??= new();
        Navigation.Normalize(changes);
        RoundTrace ??= new();
        RoundTrace.Normalize(changes);
        return changes;
    }
}
