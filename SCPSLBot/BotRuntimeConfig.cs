using System.Collections.Generic;
using SCPSLBot.Navigation;
using SCPSLBot.Presentation;

namespace SCPSLBot;

public sealed class BotRuntimeConfig
{
    public bool EnableNetworkRegistryRecovery { get; set; } = true;
    public string Language { get; set; } = "cn";
    public HintDisplayConfig HintDisplay { get; set; } = new();
    public NavigationConfig Navigation { get; set; } = new();
    public bool ForceStandardDoorConnectors { get; set; }
    internal IReadOnlyList<string> Normalize()
    {
        var changes = new List<string>();
        HintDisplay ??= new();
        Navigation ??= new();
        Navigation.Normalize(changes);
        return changes;
    }
}
