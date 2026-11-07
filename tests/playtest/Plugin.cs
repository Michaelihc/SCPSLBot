using RoundStart = GameCore.RoundStart;
using LabApi.Events.Arguments.ServerEvents;
using LabApi.Events.Handlers;
using LabApi.Features;
using LabApi.Features.Console;
using LabApi.Loader.Features.Plugins;
using LabApi.Loader.Features.Plugins.Enums;
using SCPSLBot.PlaytestScenarios.Harness;
using System;

namespace SCPSLBot.PlaytestScenarios;

/// <summary>Test-only loader glue; this assembly contains no production feature code.</summary>
public sealed class ScpslBotPlaytestScenariosPlugin : Plugin<ScenarioLibraryConfig>
{
    private string? originalTeamQueue;
    private bool ownsLobbyLock;
    public override string Name => "SCPSLBot.PlaytestScenarios";
    public override string Description => "External lifecycle and population scenarios for SCPSLBot.";
    public override string Author => "metarepo";
    public override Version Version => new(1, 0, 0);
    public override Version RequiredApiVersion => new(LabApiProperties.CompiledVersion);
    public override LoadPriority Priority => LoadPriority.High;

    public override void Enable()
    {
        if (Config.LockLobby)
            ServerEvents.WaitingForPlayers += LockFixtureLobby;
        if (!string.IsNullOrEmpty(Config.NativeTeamQueue))
        {
            originalTeamQueue = GameCore.ConfigFile.ServerConfig.GetString("team_respawn_queue", "4014314031441404134041434414");
            GameCore.ConfigFile.ServerConfig.SetString("team_respawn_queue", Config.NativeTeamQueue);
            Logger.Info($"[SCPSLBot.PlaytestScenarios] Isolated native team queue: {Config.NativeTeamQueue}");
        }
        if (Config.LogScpAbilityEvents)
            ScpAbilityEventLog.Enable();
        if (Config.MapSeed > 0)
            ServerEvents.MapGenerating += UseConfiguredMapSeed;
        Logger.Info("[SCPSLBot.PlaytestScenarios] Loaded for PlaytestHarness discovery.");
    }

    public override void Disable()
    {
        ServerEvents.WaitingForPlayers -= LockFixtureLobby;
        if (ownsLobbyLock && RoundStart.LobbyLock)
            NativeCommandAdapter.RemoteAdmin("lobbylock");
        ownsLobbyLock = false;
        PursuitKeycardFixture.Restore();
        ScpAbilityEventLog.Disable();
        ServerEvents.MapGenerating -= UseConfiguredMapSeed;
        if (originalTeamQueue != null)
            GameCore.ConfigFile.ServerConfig.SetString("team_respawn_queue", originalTeamQueue);
        originalTeamQueue = null;
    }

    private void LockFixtureLobby()
    {
        // A full lobby can allocate roles before the cap scenario's delayed autorun.
        if (!RoundStart.LobbyLock)
        {
            NativeCommandResult result = NativeCommandAdapter.RemoteAdmin("lobbylock");
            if (!result.Success)
                throw new InvalidOperationException($"Cannot lock cap fixture lobby: {result.Response}");
            ownsLobbyLock = true;
        }
    }

    private void UseConfiguredMapSeed(MapGeneratingEventArgs args)
    {
        args.Seed = Config.MapSeed;
        Logger.Info($"[SCPSLBot.PlaytestScenarios] MAP_SEED {args.Seed}");
    }
}

public sealed class ScenarioLibraryConfig
{
    public bool Enabled { get; set; } = true;
    public bool LockLobby { get; set; }
    public string NativeTeamQueue { get; set; } = string.Empty;
    public bool LogScpAbilityEvents { get; set; }
    /// <summary>Replaces the host's fixed map seed for this server, so spatial checks can vary the map.</summary>
    public int MapSeed { get; set; }
}
