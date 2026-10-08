using LabApi.Features;
using LabApi.Features.Console;
using LabApi.Loader.Features.Plugins;
using LabApi.Loader.Features.Plugins.Enums;
using System;

namespace SCPSLBot.PlaytestScenarios;

/// <summary>Test-only loader glue; this assembly contains no production feature code.</summary>
public sealed class ScpslBotPlaytestScenariosPlugin : Plugin<ScenarioLibraryConfig>
{
    private string? originalTeamQueue;
    public override string Name => "SCPSLBot.PlaytestScenarios";
    public override string Description => "External lifecycle and population scenarios for SCPSLBot.";
    public override string Author => "metarepo";
    public override Version Version => new(1, 0, 0);
    public override Version RequiredApiVersion => new(LabApiProperties.CompiledVersion);
    public override LoadPriority Priority => LoadPriority.High;

    public override void Enable()
    {
        if (!string.IsNullOrEmpty(Config.NativeTeamQueue))
        {
            originalTeamQueue = GameCore.ConfigFile.ServerConfig.GetString("team_respawn_queue", "4014314031441404134041434414");
            GameCore.ConfigFile.ServerConfig.SetString("team_respawn_queue", Config.NativeTeamQueue);
            Logger.Info($"[SCPSLBot.PlaytestScenarios] Isolated native team queue: {Config.NativeTeamQueue}");
        }
        Logger.Info("[SCPSLBot.PlaytestScenarios] Loaded for PlaytestHarness discovery.");
    }

    public override void Disable()
    {
        if (originalTeamQueue != null)
            GameCore.ConfigFile.ServerConfig.SetString("team_respawn_queue", originalTeamQueue);
        originalTeamQueue = null;
    }
}

public sealed class ScenarioLibraryConfig
{
    public bool Enabled { get; set; } = true;
    public string NativeTeamQueue { get; set; } = string.Empty;
}
