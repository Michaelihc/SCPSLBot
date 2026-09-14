# Uniform Surface exclusion for room teleport

Date: 2026-09-05 (Asia/Shanghai)

## Behavior

- Room teleport excludes every generated Surface named-door destination for every role.
- Apply re-resolves the stable native door tag and rejects Surface independently of role or SSS
  refresh state, closing stale-view and forged-callback paths.
- Deliberate Surface entry remains available through the arena control.

## Automated checks

- `SCPSLBot.PolicyTests`: 101/101 passed, including case-insensitive Surface rejection and allowed
  LCZ/HCZ/EZ room destinations.
- Reference-aware `SCPSLBotAddon.sln` x64 Release build: zero warnings, zero errors.
- `SCPSLBot.PlaytestScenarios` Release build: zero warnings, zero errors.
- Shared scenario linter: zero new errors; 11 unrelated legacy backlog entries.
- `git diff --check`: no whitespace errors.
- The existing native named-door spatial scenario remains applicable because this change filters
  destinations without changing native RA position resolution or geometry.

## Production staging and activation

- Staged artifact:
  `/home/scpsl/.config/SCP Secret Laboratory/LabAPI/staging/7777/20260905-170900-uniform-room-teleport/SCPSLBot.dll`
- Staged SHA-256:
  `D12C707DD1F82DFF70D85630497B7F35635F851E3B789600A71C515C067825DF`
- Staged ownership/mode: `scpsl:scpsl:644`.
- Initial staging did not replace the active DLL or restart production. After explicit activation
  authorization, the staged artifact replaced the active DLL with verified `scpsl:scpsl:644`
  ownership.
- Pre-activation rollback directory:
  `/home/scpsl/.config/SCP Secret Laboratory/LabAPI/backups/7777/20260905-171328-before-uniform-room-teleport/`.
  Its `SCPSLBot.dll` retains SHA-256
  `E6BAB48FBFF69EE4F13AC8185E19101AA1DBD55992BBE1283DBA52B85AB277FD`.
- Sixteen listed connections received a verified 30-second bilingual warning.
- Native RA `nextround` initially reported normal round restart. Native RA `rnr` then confirmed
  `Server WILL restart after next round`, and native RA `restart` confirmed `Round restart forced`
  while reporting that RestartNextRound was used.
- LocalAdmin remained PID `154153`, while the game child changed from PID `154170` to `155091`.
  This confirms the requested RA-channel game-server restart rather than a systemd service restart.
- Post-restart log:
  `/home/scpsl/.config/SCP Secret Laboratory/LocalAdminLogs/7777/LocalAdmin Log 2026-09-05 17.14.39.txt`.
  SCPSLBot enabled successfully at 17:14:42, the active on-disk hash is the staged hash, UDP 7777 is
  bound, and six managed bots are alive with no controller or AI fault.
- The fresh log contains zero SCPSLBot errors, zero `Runtime loop recovered`, and zero
  `ReferenceHub.GetHashCode` occurrences. `nextround` returned to its normal state after activation.

## Activation acceptance

Confirm that every player role sees no Surface rooms in the room-teleport dropdown and that a
previously staged Surface door ID is rejected.
