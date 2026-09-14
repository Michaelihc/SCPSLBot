using System;
using LabApi.Features;
using LabApi.Features.Console;
using LabApi.Loader.Features.Plugins;
using LabApi.Loader.Features.Plugins.Enums;
using StatsBots.Config;
using StatsBots.Integration;
using StatsBots.Services;

namespace StatsBots;

public sealed class StatsBotsPlugin : Plugin<StatsBotsConfig>
{
    private StatsBotsRuntime? _runtime;
    private IHintDisplayProvider? _hints;
    private ServerKeybindsAdapter? _sss;

    public static StatsBotsPlugin? Instance { get; private set; }
    internal StatsBotsRuntime? Runtime => _runtime;

    public override string Name => "StatsBots";
    public override string Description => "Warmup bot scoring, titles, localized HUD, and bounded onboarding over StatsSystem.";
    public override string Author => "SCPSLBot contributors";
    public override Version Version { get; } = new(1, 0, 0);
    public override Version RequiredApiVersion => new(LabApiProperties.CompiledVersion);
    public override LoadPriority Priority => LoadPriority.Lowest;
    public override bool IsTransparent => true;

    public override void Enable()
    {
        if (_runtime != null)
        {
            Logger.Warn("[StatsBots] Duplicate Enable ignored.");
            return;
        }
        if (_sss?.HasClaim == true)
        {
            _sss.Disable();
            if (_sss.HasClaim)
                throw new InvalidOperationException("StatsBots cannot enable while a prior Server-Specific Settings claim remains unreleased.");
            _sss = null;
        }

        IHintDisplayProvider? hints = null;
        StatsBotsRuntime? runtime = null;
        ServerKeybindsAdapter? sss = null;
        try
        {
            Config.Validate();
            SaveConfig();
            var stats = new StatsSystemAdapter();
            var bots = new ScpslBotAdapter();
            var text = new Localization(Config);
            var preferences = new PlayerPreferences();
            hints = HintDisplayProviderFactory.Create(Config.HintDisplay);
            hints.Enable();

            runtime = new StatsBotsRuntime(Config, stats, bots, hints, text, preferences);
            sss = new ServerKeybindsAdapter(Config, runtime, text, preferences);
            runtime.SetSss(sss);
            runtime.Enable();
            sss.Enable(); // Optional: one logged fallback if the compatibility fork is absent.

            _hints = hints;
            _runtime = runtime;
            _sss = sss;
            Instance = this;
            Logger.Info("[StatsBots] Enabled. StatsSystem, SCPSLBot identity, HSM, and ServerKeybinds integrations are late-bound.");
        }
        catch (Exception ex)
        {
            try { sss?.Disable(); } catch { }
            try { runtime?.Disable(); } catch { }
            try { hints?.Disable(); } catch { }
            _sss = sss?.HasClaim == true ? sss : null;
            _runtime = null;
            _hints = null;
            Instance = null;
            Logger.Error("[StatsBots] Enable rolled back after partial failure: " + ex);
            throw;
        }
    }

    public override void Disable()
    {
        if (_runtime == null && _hints == null && _sss == null)
        {
            Instance = null;
            return;
        }

        ServerKeybindsAdapter? sss = _sss;
        StatsBotsRuntime? runtime = _runtime;
        IHintDisplayProvider? hints = _hints;
        _runtime = null;
        _hints = null;
        Instance = null;

        try { sss?.Disable(); }
        catch (Exception ex) { Logger.Warn("[StatsBots] Server-Specific Settings cleanup failed: " + ex.GetBaseException().Message); }
        _sss = sss?.HasClaim == true ? sss : null;
        try { runtime?.Disable(); }
        catch (Exception ex) { Logger.Warn("[StatsBots] Runtime cleanup failed: " + ex.GetBaseException().Message); }
        try { hints?.Disable(); }
        catch (Exception ex) { Logger.Warn("[StatsBots] Hint-provider cleanup failed: " + ex.GetBaseException().Message); }
        if (_sss?.HasClaim == true)
            Logger.Error("[StatsBots] Runtime disabled, but the optional Server-Specific Settings claim could not be released; a later Disable/Enable will retry cleanup.");
        else
            Logger.Info("[StatsBots] Disabled; provider flush was requested.");
    }
}
