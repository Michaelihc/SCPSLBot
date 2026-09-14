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
- `ptest run bot-ci-spawn standard`: 1/1 passed in 1.15 seconds. It covered initial creation,
  native RA death/respawn of the same maintained identity, CI spawn-pad routing, Standard off/on
  recreation, and absence of native `SpawnProtected` after each bot role assignment.
- The first scenario invocation exposed a pre-existing acceptance race: its alive-role predicate
  could complete before `BotManager`'s documented next-MEC-tick protection clear. The scenario now
  includes absence of `SpawnProtected` in each readiness predicate; the rerun passed.
- Port 8888 was stopped after that boot check.

## Subsequent production deployment

- The death-only player spawn-protection policy was later included in the port 7777 deployment
  recorded in `20260905-bot-ownership.md`.
- Production startup confirmed that managed bots remain unaffected: all three logged
  `SPAWN_PROTECTION_CLEARED` after their role assignment.
- Connected-client acceptance for the real-player death/loadout sequence remains required because
  production was empty after restart and the runtime deliberately ignores hosts and dummies.

## Remaining connected-client acceptance

A real authenticated client is required because the runtime deliberately ignores hosts and dummies:

1. While alive in Standard warmup, apply another role/loadout and confirm native `SpawnProtected` is
   absent; the server should log `PLAYER_SPAWN_PROTECTION_CLEARED`.
2. Die through normal gameplay or RA damage, wait for the warmup respawn, and confirm native
   `SpawnProtected` is active; the server should log
   `PLAYER_SPAWN_PROTECTION_DEATH_RESPAWN ... active=True`.
3. Change role/loadout again while alive and confirm the effect is cleared.
