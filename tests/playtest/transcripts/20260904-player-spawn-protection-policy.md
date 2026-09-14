# Real-player death-only spawn-protection policy

Date: 2026-09-04 (Asia/Shanghai)

## Automated checks

- `SCPSLBot.PolicyTests`: 93/93 passed, including six player/death/loadout policy cases.
- `SCPSLBotAddon.sln` x64 Release build: zero warnings, zero errors.
- `SCPSLBot.PlaytestScenarios` Release build: zero warnings, zero errors.
- Shared scenario linter: zero new errors; 11 unrelated legacy backlog entries.
- `git diff --check`: no whitespace errors.

## Isolated port 8888 boot

- Deployed `SCPSLBot.dll` SHA-256:
  `20DB6D3E91E4AA40F2652BB409A3E523738F8FBEC8BFD617C6428F5056DC85AD`.
- LabAPI successfully enabled SCPSLBot 1.0.0.
- All three managed bots still logged `SPAWN_PROTECTION_CLEARED`, confirming that the new
  real-player service does not change bot behavior.
- Port 8888 was stopped after the boot check. Production port 7777 was not modified or restarted.

## Remaining connected-client acceptance

A real authenticated client is required because the runtime deliberately ignores hosts and dummies:

1. While alive in Standard warmup, apply another role/loadout and confirm native `SpawnProtected` is
   absent; the server should log `PLAYER_SPAWN_PROTECTION_CLEARED`.
2. Die through normal gameplay or RA damage, wait for the warmup respawn, and confirm native
   `SpawnProtected` is active; the server should log
   `PLAYER_SPAWN_PROTECTION_DEATH_RESPAWN ... active=True`.
3. Change role/loadout again while alive and confirm the effect is cleared.
