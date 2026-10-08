using System.Collections.Generic;
using SCPSLBot.Navigation;
using SCPSLBot.Presentation;

namespace SCPSLBot;

public sealed class BotRuntimeConfig
{
    public bool EnableNetworkRegistryRecovery { get; set; } = true;
    public int NormalRoundBotCount { get; set; }
    public string Language { get; set; } = "cn";
    public HintDisplayConfig HintDisplay { get; set; } = new();
    public NavigationConfig Navigation { get; set; } = new();
    public bool ForceStandardDoorConnectors { get; set; }
    internal IReadOnlyList<string> Normalize()
    {
        var changes = new List<string>();
        int count = System.Math.Max(0, System.Math.Min(10, NormalRoundBotCount));
        if (count != NormalRoundBotCount) changes.Add($"NormalRoundBotCount: {NormalRoundBotCount} -> {count}");
        NormalRoundBotCount = count;
        HintDisplay ??= new();
        Navigation ??= new();
        Navigation.Normalize(changes);
        return changes;
    }
}
