# SCPSLBot Playtest Scenarios

This test-only LabAPI plugin contributes external scenarios to the shared `PlaytestHarness`. It has
no compile-time reference to SCPSLBot and never reflects into the plugin. Bot behavior is driven
through the native RA/game-console processors and observed through `bot_status`, LabAPI events,
native player/dummy state, network registration, positions, and physics raycasts.

## Build

Set `SL_REFERENCES` to the dedicated server's `SCPSL_Data\Managed` folder and `SCPSL_METAREPO` to the
metarepo checkout, then build everything with deployment disabled. A missing `SL_REFERENCES` fails
`SCPSLBot.Components` and leaves a stale DLL that the driver would still package.

```powershell
$M = $env:SCPSL_METAREPO
dotnet build SCPSLBotAddon.sln -c Release -p:Platform=x64 -p:DeployToLocalServer=false "-p:HsmAdapterProject=$M\HsmAdapter\HsmAdapter.csproj" "-p:ServerKeybindsProject=$M\ServerKeybinds\ServerKeybinds.csproj"
dotnet build tests/playtest/SCPSLBot.PlaytestScenarios.csproj -c Release -p:DeployToLocalServer=false "-p:PlaytestHarnessAssembly=$M\.tests\Playtest\bin\Release\PlaytestHarness.dll"
```

## Driver

`tests/playtest/tools/check_normal_round.py` is the one driver. It requires a clean `git status`,
packages the committed candidate, verifies the navmesh asset patch on every prepared slot, and runs
on a fresh disposable offline-host slot (it waits for a free one). Gameplay plugins in the baseline
overlay are removed. Evidence lands in `scpsl-bot-plugin/tests/playtest/artifacts/normal-round-<scenario>-<timestamp>/`
under the metarepo (`*.summary.json`, `*.jsonl`, `host.log`); the server log is under the slot's
`runs/<run>/server-logs/`. The exit code is zero only when exactly the named scenario passed with
nothing failed or skipped.

| Option | Effect |
| --- | --- |
| *(none)* | Standard population case (`scpslbot-normal-round`) |
| `--escape` | Autonomous escape (`scpslbot-autonomous-escape`), up to ten minutes |
| `--scenario <name>` | Runs one scenario on an empty `bots_only` SCPSLBot fixture |
| `--backend Runtime\|Authored` | Navigation backend (default `Runtime`) |
| `--level quick\|standard\|e2e` | Fidelity tier (default `standard`); must be one the scenario supports |
| `--limit <seconds>` | How long the scenario may run (default 900) |
| `--management` | With `--scenario`: also loads `BotRoundControl.dll` with `bots_only: false` and `warmup_mode: Standard` |
| `--companions` | With `--management`: also loads WarmupSafezone and StatsBots |
| `--management-config <file>` | With `--management`: replaces BotRoundControl's generated config |
| `--harness-config <file>` | Replaces the generated PlaytestHarness config |
| `--extensions` | Also loads the test-only `SCPSLPluginExtensions.dll` (`plugin_direload`) |

The bundle uses the fixed `plugins/8910/` and `configs/8910/` names and exactly one
`ServerKeybinds.dll`. Scenarios never write `Round.IsLocked` and the Standard population and escape
cases never inject SCPSLBot orders.

## Scenarios that need BotRoundControl

`bot_warmup`, the `mode` field of `bot_status` and the maintained warmup population exist only with
`BotRoundControl.dll` loaded, so these run with `--management`:

```powershell
python tests/playtest/tools/check_normal_round.py --scenario scpslbot-population-recovery --management --limit 150
python tests/playtest/tools/check_normal_round.py --scenario scpslbot-role-death-recovery --management --limit 150
python tests/playtest/tools/check_normal_round.py --scenario scpslbot-native-walk-grounding --management --limit 360
python tests/playtest/tools/check_normal_round.py --scenario scpslbot-destroyed-bot-recovery --management --limit 170
python tests/playtest/tools/check_normal_round.py --scenario scpslbot-surface-managed-chaos-spawn --management --limit 150
python tests/playtest/tools/check_normal_round.py --scenario scpslbot-bot-ownership --management --limit 190
python tests/playtest/tools/check_normal_round.py --scenario scpslbot-warmup-warhead --management --level quick --limit 150 --harness-config tests/playtest/fixtures/warmup-warhead-harness.yml
python tests/playtest/tools/check_normal_round.py --scenario scpslbot-halloween-moon --management --level quick --limit 400 --management-config tests/playtest/fixtures/halloween-moon-management.yml --harness-config tests/playtest/fixtures/halloween-moon-harness.yml
python tests/playtest/tools/check_normal_round.py --scenario scpslbot-connector-survey --management --limit 1650
python tests/playtest/tools/check_normal_round.py --scenario scpslbot-door-survey --management --limit 1650
```

The first five are members of the `scpslbot-lifecycle` suite (with `scpslbot-surface-native-role-routing`).
They cover exact desired/live population within 12 seconds, diagnostics permission and readiness
text, off/on churn, canceled role repair, native death/respawn, dense grounding raycasts, dummy
settle checks, and a real multi-room bot walk. `scpslbot-bot-ownership` is excluded from `run all`;
it keeps an independent `bot_add` bot separate from maintained population. The warhead and Halloween
moon cases are `quick` only. Construction-stage fault injection, 100-cycle native-memory/handle
baselines and 20 cold boots/restarts remain separate live/manual gates.

`scpslbot-connector-survey` (excluded from `run all`) disables warmup population, spawns the
`botspike` bot, runs `botspike survey clutter both`, and requires zero failed cases and zero rooms
without navigation cells. The survey teleports the bot only for setup; every traversal is native
FPC walking through `BotOrders`, logged per case as `[BotSurvey] CASE ... verdict=...` with the
connector type, rooms, blocker and order telemetry. `scpslbot-door-survey` does the same over plain
non-keycard doors (forward direction). Each run uses a fresh map seed, which the summary records.

`scpslbot-midround-reload-recovery` additionally needs `--extensions` and the built
`SCPSLPluginExtensions.dll`. Production and release packages intentionally exclude that driver.

## Bots-only scenarios

These run without management on an empty `bots_only` fixture; add `--backend Authored` to
exercise the embedded navmesh:

```powershell
python tests/playtest/tools/check_normal_round.py
python tests/playtest/tools/check_normal_round.py --escape
python tests/playtest/tools/check_normal_round.py --scenario scpslbot-order-passivity --limit 420
python tests/playtest/tools/check_normal_round.py --scenario scpslbot-order-immediate --limit 200
python tests/playtest/tools/check_normal_round.py --scenario scpslbot-autonomous-roam --limit 1020
python tests/playtest/tools/check_normal_round.py --scenario scpslbot-autonomous-roam-939 --limit 700
python tests/playtest/tools/check_normal_round.py --scenario scpslbot-issue2-checkpoints --limit 900
python tests/playtest/tools/check_normal_round.py --scenario scpslbot-issue2-greenhouse --limit 600
```

Add `--backend Authored` to exercise the embedded navmesh, and `--map-seed <n>` to leave the host's
fixed map seed: spatial results only cover other maps when the seed changes. The Standard population case uses the native team queue to assign three independent spectators,
then checks that an admin-selected spectator remains unmanaged. The escape case uses a single
native Scientist spawn and observes autonomous travel, actual item pickups and the native escape
event. The order cases first prove the armed bot lands a real hit without an order, so a bot that
never fights cannot pass. The roam case soaks one uncontested bot per role and fails on a long stop,
on alternating between two rooms, or on a Surface spawn that never enters the facility. Seconds
SCP-939 spends holding native focus are an ambush, not a stop, and are limited separately. The
`-939` variant repeats the SCP-939 soak four times.

## Runtime navmesh gates

The driver's asset check (`tools/NavMeshAssetPatcher verify`) must pass before the server boots.
Both gates read `bot_status` fields that exist only with BotRoundControl loaded, so they run with `--management`.
`scpslbot-runtime-navmesh-gate` reads `bot_status`, `nav status`, `nav probe` and `nav path` and
requires the runtime backend with zero unreadable meshes, zero uncovered rooms, elevator links,
keycard door classes, a bake under 5 s, a room index under 1.5 s, and complete paths for
LczClassDSpawn -> HczWarhead, LczClassDSpawn -> Lcz914, Hcz049 -> EzGateA and EzGateA -> Outside.
`scpslbot-keycard-routing-survey` drives `botspike survey keycard`: for every keycard door a path
across the doorway must be blocked (or clearly longer) without the card and complete with it.
`scpslbot-unity-navmesh-probe` remains the research probe (sources, build time, coverage detail).

```powershell
python tests/playtest/tools/check_normal_round.py --scenario scpslbot-runtime-navmesh-gate --management --limit 300
python tests/playtest/tools/check_normal_round.py --scenario scpslbot-keycard-routing-survey --management --limit 900
```

A successful `nav path` query proves planning only; connector acceptance requires native walks.
