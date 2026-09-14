# CI spawn and infinite-ammo deployment verification — 2026-09-01

## Deterministic checks

- `SCPSLBot.PolicyTests`: 66/66 passed, including all four Surface CI anchor roles and the
  fully-dry/zero-reserve dummy reload trigger.
- `SCPSLBot` x64 Release: 0 warnings, 0 errors.
- `LabAPI_InfiniteAmmo` x64 Release: build succeeded.
- `SCPSLBot.PlaytestScenarios` x64 Release: 0 warnings, 0 errors.
- Shared scenario linter: `errors=0`; pre-existing legacy backlog unchanged.

The new `scpslbot-surface-managed-chaos-spawn` Standard-fidelity scenario captures final LabAPI
`Spawning` coordinates for maintained Chaos Riflemen, then distinguishes sampled native CI and MTF
Surface regions after a death/respawn and after full warmup off/on recreation. It uses public events,
world dummy state, and native RA command dispatch only. The harness discovered all 19 scenarios on
local port 8888. Execution still requires an authenticated RA/game-console caller; Windows app-control
safety rules prohibit automating the LocalAdmin terminal.

## Local port 8888

- `SCPSLBot`, `SCPSLBot.PlaytestScenarios`, `ServerKeybinds` API 4, and Infinite Ammo 1.0.1 loaded.
- Harness discovered 19 scenarios.
- Three maintained bots reached `ChaosRifleman`; no population reconciler or spawn-failure log occurred.
- Overflow cleanup captured its round baseline.

## Production port 7777

- Forced restart completed at `2026-09-01 21:04:01 +08:00`.
- Service active; UDP 7777 bound by the new dedicated process.
- `SCPSLBot` SHA-256: `8fd0b5a536029128968b7867f955d535faa9f9927f26b27b931c92a9c7559825`.
- `LabAPI_InfiniteAmmo_x64.dll` SHA-256:
  `4abb7b23148571e6cace8fd584c809dce52b2351ad5beda9f5c9bf0ba26fdb60`.
- Infinite Ammo 1.0.1 and SCPSLBot enabled successfully.
- Three maintained bots reached `ChaosRifleman`; no SCPSLBot/ammo loader, spawn, or reconciler fault matched.
- Overflow cleanup captured a 294-item live baseline.
- Exactly one port-scoped `ServerKeybinds.dll` exists under `dependencies/7777`.

Rollback backup:

`/home/scpsl/.config/SCP Secret Laboratory/LabAPI/backups/7777/SCPSLBot-20260901-210301-e9bfb07ef35b38fd25611c8f4121e2378e1011dcbfe8b6cc68112ba7fae167d8.dll.bak`

The adjacent manifest records that Infinite Ammo was absent before this deployment, so rollback removes
the newly introduced ammo DLL in addition to restoring the SCPSLBot backup.
