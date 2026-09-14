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

## Runtime navmesh gates

All 8891 drivers verify the dedicated-server asset patch (`tools/NavMeshAssetPatcher verify`) before
booting. `scpslbot-runtime-navmesh-gate` reads `bot_status`, `nav status`, `nav probe` and `nav path`
and requires the runtime backend with zero unreadable meshes, zero uncovered rooms, elevator links,
keycard door classes, a bake under 5 s, a room index under 1.5 s, and complete paths for
LczClassDSpawn -> HczWarhead, LczClassDSpawn -> Lcz914, Hcz049 -> EzGateA and EzGateA -> Outside.
`scpslbot-keycard-routing-survey` drives `botspike survey keycard`: for every keycard door a path
across the doorway must be blocked (or clearly longer) without the card and complete with it.
`scpslbot-unity-navmesh-probe` remains the research probe (sources, build time, coverage detail).

```powershell
python tests/playtest/tools/check_connector_survey.py --scenario scpslbot-runtime-navmesh-gate --label navmesh-gate
python tests/playtest/tools/check_connector_survey.py --scenario scpslbot-keycard-routing-survey --label keycard
```

## Navigation load recovery

On the provisioned, stopped Windows test port 8891, run
`python tests/playtest/tools/check_navigation_recovery.py` from the plugin root. The driver
temporarily deploys the Release plugin and scenario DLL, forces `navigation.backend: Authored` for the run (the runtime backend never reads the file), locks the existing navigation file,
and waits for six load failures to prove capped backoff before releasing it. The startup scenario
asserts `nav_ready=False`/`nav_error` while blocked, recovery in the same map generation, exact live
population, ground raycasts/settling, canceled-role repair and native death/respawn. Evidence is
written to `tests/playtest/artifacts/navigation-recovery-<timestamp>/`. The driver stops its exact
LocalAdmin and child, then restores the port's plugin/config files. It does not touch production.

## Destroyed bot recovery

On the provisioned, stopped port 8891, build Release SCPSLBot and this scenario assembly, then run
`python tests/playtest/tools/check_destroyed_bot_recovery.py`. The driver uses the maintained local
LocalAdmin fork, restricts loading to port 8891, verifies/captures the tested DLL, and restores the
port's plugin/config files after stopping its exact processes. Evidence lives under
`tests/playtest/artifacts/destroyed-bot-<timestamp>/`.

`scpslbot-destroyed-bot-recovery` destroys one maintained dummy three times and then the entire
maintained population using native RA. It verifies actual Unity destruction/network removal,
automatic world replacement before diagnostic queries, exact live count in the same map,
working `bot_status`, dense ground raycasts and settling. It needs a Standard Surface profile and
no independent bots or real clients. `--expect-reproduction` is only for the old DLL: it requires
the scenario to fail with the production `PruneMissingEntries` / `GetHashCode` signature.
`--scenario <name>` runs an existing Standard regression with the same isolated driver.

## Connector traversal survey

`scpslbot-connector-survey` (Standard only, excluded from `run all`) disables warmup population,
spawns the `botspike` bot, runs `botspike survey clutter both`, and requires zero failed cases and
zero rooms without navigation cells. The survey teleports the bot only for setup; every traversal
is native FPC walking through `BotOrders`, logged per case as `[BotSurvey] CASE ... verdict=...`
with the connector type, rooms, blocker and order telemetry. `scpslbot-door-survey` does the same
over plain non-keycard doors (forward direction).

Run it on the provisioned, stopped port 8891 from the plugin root after building Release SCPSLBot
and this scenario assembly:

```powershell
python tests/playtest/tools/check_connector_survey.py --rounds 2
python tests/playtest/tools/check_connector_survey.py --rounds 2 --expect-reproduction --label baseline
python tests/playtest/tools/check_connector_survey.py --scenario scpslbot-door-survey --label door-survey
python tests/playtest/tools/check_connector_survey.py --scenario scpslbot-lifecycle --label lifecycle
```

Each round boots a fresh headless server (new map seed), copies the summary, the JSONL event
stream, the full LocalAdmin log and a `survey-round<n>.log` extract (`[BotSurvey]`, `[BotNav]`,
`NAV_*`, order stalls) into `tests/playtest/artifacts/<label>-<timestamp>/`, then stops its exact
process pair and restores the port's plugin/config files. `--expect-reproduction` is the baseline
mode that requires at least one failed traversal.

