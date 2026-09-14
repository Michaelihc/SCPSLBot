using System;
using LabApi.Features.Console;
using MEC;
using ScpslPluginStarter.Core;

namespace ScpslPluginStarter.Services;

internal sealed class SafezoneLifecycleService
{
    private const int SchedulerResolutionMilliseconds = 100;

    private readonly IMonotonicClock _clock;
    private readonly ResilientSchedule _schedule = new();
    private int _generation;
    private bool _running;

    public SafezoneLifecycleService(IMonotonicClock clock) => _clock = clock;

    public void Add(string name, int intervalMilliseconds, Action action) =>
        _schedule.Add(name, Math.Max(SchedulerResolutionMilliseconds, intervalMilliseconds), action, _clock.NowMilliseconds);

    public void Start()
    {
        if (_running)
        {
            return;
        }

        _running = true;
        int generation = ++_generation;
        ScheduleNext(generation);
    }

    public void Stop()
    {
        _running = false;
        _generation++;
    }

    private void ScheduleNext(int generation) => Timing.CallDelayed(
        SchedulerResolutionMilliseconds / 1000f,
        () => RunPass(generation));

    private void RunPass(int generation)
    {
        if (!_running || generation != _generation)
        {
            return;
        }

        try
        {
            _schedule.RunDue(
                _clock.NowMilliseconds,
                (name, exception) => Logger.Error($"[WarmupSafezone:Lifecycle] Tick '{name}' failed and was isolated: {exception}"));
        }
        catch (Exception exception)
        {
            Logger.Error($"[WarmupSafezone:Lifecycle] Scheduler pass failed and will continue: {exception}");
        }
        finally
        {
            if (_running && generation == _generation)
            {
                ScheduleNext(generation);
            }
        }
    }
}
