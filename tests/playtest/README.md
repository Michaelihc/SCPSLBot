# SCPSLBot Playtest Scenarios

This test-only LabAPI plugin contributes external scenarios to the shared `PlaytestHarness`. It has
no compile-time reference to SCPSLBot and never reflects into the plugin. Bot behavior is driven
through the native RA/game-console processors and observed through `bot_status`, LabAPI events,
native player/dummy state, network registration, positions, and physics raycasts.

From the `scpsl-bot-plugin` directory, build the shared harness first, then this scenario assembly:

```powershell
dotnet build ..\.tests\Playtest\PlaytestHarness.csproj -c Release
dotnet build .\tests\playtest\SCPSLBot.PlaytestScenarios.csproj -c Release
```

Deploy `PlaytestHarness.dll`, `SCPSLBot.PlaytestScenarios.dll`, SCPSLBot and its runtime dependencies
to one isolated test port. Restart the server, then run:

```text
ptest reload
ptest run scpslbot-lifecycle standard
```

Disruptive/by-name acceptances:

- `ptest run scpslbot-bot-add-cap standard` persists `warmup_bot_count: 10` because the production
  `bot_add` surface intentionally has no decrement command. Run only on an isolated port and reset
  the config afterward.
- `ptest run scpslbot-midround-reload-recovery standard` requires the test-only
  `SCPSLPluginExtensions.dll`. Production and release packages intentionally exclude that driver.

The lifecycle suite covers exact desired/live population within 12 seconds, diagnostics permission
and readiness text, off/on churn, one canceled role repair, native death/respawn, dense grounding
raycasts, dummy settle checks, and a real multi-room bot walk. Construction-stage fault injection,
100-cycle native-memory/handle baselines, 20 cold boots/restarts, and production-free reload driving
remain separate live/manual gates.
