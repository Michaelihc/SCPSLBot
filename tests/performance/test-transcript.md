# SCPSLBot performance checks — 2026-08-30

## Pure deterministic checks

Command:

```powershell
dotnet run --project tests\performance\SCPSLBot.Performance.PureTests.csproj -c Release --no-restore
```

Result: `5/5 passed`.

- Cached immutable difficulty profiles and unknown-value fallback.
- Fixed 8 Hz / 125 ms snapshot refresh cadence and reset.
- Stable binary min-heap ordering.
- Retained heap capacity through 100 runs of 4,096 enqueue/dequeue operations.
- Duplicate-entry behavior used by A* decrease-key updates.

## Native plugin build

Command:

```powershell
dotnet build SCPSLBot\SCPSLBot.csproj -c Release -p:Platform=x64 -p:SL_REFERENCES="C:\Program Files (x86)\Steam\steamapps\common\SCP Secret Laboratory Dedicated Server\SCPSL_Data\Managed" --no-restore
```

Result: build succeeded with `0` warnings and `0` errors.

## Remaining live verification

The new target snapshot and pathfinder require a multiplayer soak on the dedicated local bot-test port `8888` to establish the audit's 10-bot/30-player frame-time, linecast, allocation, and network-message budget. No server was started for these pure performance changes.
