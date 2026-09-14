using System;
using LabApi.Events;
using LabApi.Events.Handlers;
using LabApi.Features.Console;
using LabApi.Loader.Features.Plugins;
using ScpslPluginStarter.Core;
using ScpslPluginStarter.Services;

namespace ScpslPluginStarter;

public sealed class WarmupSafezonePlugin : Plugin<WarmupSafezoneConfig>
{
    private IHintDisplayProvider? _hints;
    private SafezoneOccupancyService? _occupancy;
    private OwnedDamageRegistry? _ownedDamage;
    private SurfaceBlockerService? _blocker;
    private SafezoneEnforcementService? _enforcement;
    private SafezoneVisualService? _visuals;
    private SafezoneLifecycleService? _lifecycle;
    private bool _enabled;
    private bool _serverEventsRegistered;

    public static WarmupSafezonePlugin? Instance { get; private set; }

    public override string Name => "WarmupSafezone";
    public override string Description => "Surface escape and SCP-914 safezone rules and visuals.";
    public override string Author => "Michael";
    public override Version Version => new(1, 0, 0);
    public override Version RequiredApiVersion => new(1, 1, 6);

    public override void Enable()
    {
        if (_enabled)
        {
            return;
        }

        if (Instance != null && Instance != this)
        {
            throw new InvalidOperationException("Another WarmupSafezone instance is already enabled.");
        }

        Instance = this;
        try
        {
            IMonotonicClock clock = new StopwatchMonotonicClock();
            WarmupLocalization localization = new(Config.Language);
            Config.HintDisplay ??= new HintDisplayConfig();
            SafezoneVolumeService volumes = new(Config);
            ExitProtectionService exitProtection = new(Config, clock);
            _occupancy = new SafezoneOccupancyService(volumes, exitProtection);
            _ownedDamage = new OwnedDamageRegistry();
            _hints = HintDisplayProviderFactory.Create(Config.HintDisplay);
            _hints.Enable();
            _blocker = new SurfaceBlockerService(Config, volumes, _ownedDamage, _hints, localization, clock);
            SurfaceHealthDrainService healthDrain = new(Config, volumes, _ownedDamage, _hints, localization);
            _enforcement = new SafezoneEnforcementService(
                Config,
                _occupancy,
                exitProtection,
                _ownedDamage,
                _blocker,
                _hints,
                localization,
                clock);
            _visuals = new SafezoneVisualService(Config, localization);
            _lifecycle = new SafezoneLifecycleService(clock);
            _lifecycle.Add("occupancy-recovery", 100, RecoverOccupancy);
            _lifecycle.Add("dangerous-items", 250, _enforcement.TickDangerousItems);
            _lifecycle.Add("surface-health-drain", 1000, healthDrain.Tick);
            _lifecycle.Add("surface-blocker", 1000, _blocker.Tick);
            _lifecycle.Add("scp096-calm", 1000, _enforcement.TickScp096Calm);
            _lifecycle.Add("visual-recovery", 1000, _visuals.Ensure);

            RegisterServerEvents();
            _enforcement.Enable();
            _visuals.Ensure();
            _lifecycle.Start();
            _enabled = true;
            Logger.Info($"[{Name}] Enabled enabled={Config.Enabled}; configured Surface safezone restored with native Map.EscapeZones fallback.");
        }
        catch
        {
            Cleanup();
            throw;
        }
    }

    public override void Disable()
    {
        if (!_enabled && Instance != this)
        {
            return;
        }

        Cleanup();
        Logger.Info($"[{Name}] Disabled.");
    }

    private void Cleanup()
    {
        _lifecycle?.Stop();
        _enforcement?.Disable();
        UnregisterServerEvents();
        ResetRoundState();
        _visuals?.Destroy();
        _ownedDamage?.Clear();
        _hints?.Disable();

        _lifecycle = null;
        _visuals = null;
        _enforcement = null;
        _blocker = null;
        _ownedDamage = null;
        _occupancy = null;
        _hints = null;
        _enabled = false;
        if (Instance == this)
        {
            Instance = null;
        }

    }

    private void RegisterServerEvents()
    {
        if (_serverEventsRegistered)
        {
            return;
        }

        ServerEvents.WaitingForPlayers += new LabEventHandler(OnWaitingForPlayers);
        ServerEvents.RoundStarted += new LabEventHandler(OnRoundStarted);
        ServerEvents.RoundRestarted += new LabEventHandler(OnRoundRestarted);
        _serverEventsRegistered = true;
    }

    private void UnregisterServerEvents()
    {
        if (!_serverEventsRegistered)
        {
            return;
        }

        ServerEvents.RoundRestarted -= new LabEventHandler(OnRoundRestarted);
        ServerEvents.RoundStarted -= new LabEventHandler(OnRoundStarted);
        ServerEvents.WaitingForPlayers -= new LabEventHandler(OnWaitingForPlayers);
        _serverEventsRegistered = false;
    }

    private void RecoverOccupancy()
    {
        if (Config.Enabled)
        {
            _occupancy?.Recover();
        }
        else
        {
            ResetRoundState();
        }
    }

    private void OnWaitingForPlayers() => _visuals?.Ensure();
    private void OnRoundStarted() => _visuals?.Ensure();

    private void OnRoundRestarted()
    {
        ResetRoundState();
        _visuals?.Destroy();
    }

    private void ResetRoundState()
    {
        _occupancy?.Reset();
        _blocker?.Reset();
    }
}
