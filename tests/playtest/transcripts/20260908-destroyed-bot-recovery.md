# Destroyed bot recovery: reproduction and acceptance

Date: 2026-09-08, Asia/Shanghai. All runtime work used isolated local port 8891.

## Reproduce before fixing

The baseline DLL SHA-256 exactly matched production:
`821d5978bd70ed3533b21a182c601aff5571628b5354a831e8c8f937251a3790`.

`python tests/playtest/tools/check_destroyed_bot_recovery.py --expect-reproduction`
ran the new external scenario against that unmodified DLL. Initial state was four live, grounded
Surface ChaosRifleman bots. Native RA `dummies destroy <player-id>` destroyed one actual Unity
object and removed its native network identity. The world failed to replenish for 12 seconds.
The next `bot_status` failed with the same production stack:

```text
UnityEngine.Component.get_gameObject
ReferenceHub.GetHashCode
Dictionary<ReferenceHub, BotPopulationEntry>.Remove
BotPopulationController.PruneMissingEntries
BotPopulationController.GetDiagnostics
BotStatusCommand.Execute
```

Expected red result: 0 passed / 1 failed / 0 skipped, no monitor violations.
Evidence: `../artifacts/destroyed-bot-20260908-192647/`, including the baseline DLL, LocalAdmin log,
`20260908-192706-standard.summary.json`, and corresponding JSONL.

## Fix

- Added plugin-local `ManagedReferenceComparer<T>` using managed object identity and a stable
  runtime hash. Population entries, AI bot ownership/orders, and bot arena lifetime collections
  no longer invoke the native ReferenceHub hash while removing destroyed objects.
- Arena release clears its retained collections even if Unity treats the hub as null. Gameplay
  access still checks Unity validity; no LabAPI wrapper is recreated and no numeric player ID
  becomes a lifetime key.
- Population reconciliation retains its 0.25-second retry, logs continuous faults through LabAPI
  at most every 15 seconds, and emits a recovery marker after a successful reconciliation.
- Native behavior source: `../.references/Decompiled/DedicatedServer/Assembly-CSharp/ReferenceHub.cs:412`
  defines the GameObject-dependent hash; native
  `CommandSystem/Commands/RemoteAdmin/Dummies/DestroyDummyCommand.cs` uses NetworkServer.Destroy.
  Both paths are relative to the plugin root's parent reference workspace as documented there.

## Acceptance

Final SCPSLBot Release x64 SHA-256:
`8b4163539d8342c4d817ed4af9d835ebe7d0908c338bf2fe01b7183023cd8d26`.

New scenario assembly SHA-256:
`46e6cebd2a19a1dbd93038765c47b5a255a4fb72eecebf6d9849649d48f000c0`.

All five native runtime acceptances passed with zero failures, skips, or monitor violations.
All used the same final DLL. No `Exception`, `[ERROR]`, or `BOT_POPULATION_FAULT` records appeared
in their LocalAdmin logs.

| Scenario | Result | Artifact directory under `tests/playtest/artifacts` |
| --- | --- | --- |
| Destroyed bot recovery | 1/1 passed; 6.896 s | `destroyed-bot-20260908-192914` |
| Role repair cancellation and death/respawn | 1/1 passed | `destroyed-bot-20260908-193019` |
| Population off/on and diagnostics permissions | 1/1 passed | `destroyed-bot-20260908-193041` |
| Native Surface CI spawn placement | 1/1 passed | `destroyed-bot-20260908-193102` |
| Independent bot ownership transfers | 1/1 passed | `destroyed-bot-20260908-193121` |

The destruction acceptance ran three individual destructions followed by destruction of all four
maintained bots. Each recovered in the existing map without restarting or toggling warmup. It
waited for live world replacements before issuing bot_status, so diagnostic pruning could not
cause the observed recovery. Each replacement was a new managed identity. Every initial/recovered
bot passed 13/13 downward raycasts, surrounding raycast probes, and a 1.25-second settle check.

Additional checks:

- SCPSLBot Release x64 and playtest scenario builds: zero warnings/errors. Supply
  `-p:SL_REFERENCES=<dedicated-server>/SCPSL_Data/Managed` to the SCPSLBot build; the initial build
  without this machine-specific path failed reference resolution before compilation.
- Policy tests: **109/109 passed**, including destruction-safe removal and recycled numeric-ID
  isolation. Final run had no warnings.
- Shared scenario lint: exit 0, zero errors, 11 unrelated legacy entries. The shared scanner does
  not cover this plugin-local scenario; manual review verified native RA dispatch/public world
  observations and no reflection into SCPSLBot.
- Python driver compilation and `git diff --check`: passed.
- Driver stopped its exact LocalAdmin before its SCPSL child. Final process/socket inspection
  showed no test process or UDP 8891 binding. Both plugin DLLs, harness config, SCPSLBot config,
  and per-port loader config hash-match their saved original bytes.

## Scope and limits

Production was not changed or restarted. This is server-side dummy/raycast acceptance, with no
authenticated multiplayer-client test. Long-duration churn/soak and the new continuous-fault
logging interval were not separately fault-injected. The additional production arena placement
exception noted in the read-only diagnosis is outside this fix; it did not occur in these runs.
