# Surface room-teleport role guard

Date: 2026-09-05 (Asia/Shanghai)

## Regression and build checks

- `SCPSLBot.PolicyTests`: 104/104 passed, including direct-movement checks for allowed Foundation
  roles, SCP, Chaos, Class-D, and non-Surface facility zones.
- Reference-aware `SCPSLBotAddon.sln` x64 Release build: zero warnings, zero errors.
- `SCPSLBot.PlaytestScenarios` Release build: zero warnings, zero errors.
- Shared scenario linter: zero new errors; 11 unrelated legacy backlog entries.
- `git diff --check`: no whitespace errors.
- An initial broad solution invocation omitted `SL_REFERENCES` and failed to resolve the local SCP:SL
  assemblies. Re-running through the documented reference-aware build produced the clean result above.

## Behavior and threat model

- Surface room choices are now filtered through the same Facility Guard/four-NTF allowlist used by
  role-change and spawn routing.
- Apply re-resolves the exact native door tag and repeats the policy against both the player's live
  role and the destination's generated-map zone. A Surface choice staged before a role change, or a
  forged callback containing a Surface door tag, therefore fails closed.
- Facility room teleport remains role-permissive.
- The existing 35/35 native named-door spatial scenario remains applicable; this change does not
  alter native destination calculation or geometry.

## Isolated port 8888

- Deployed `SCPSLBot.dll` SHA-256:
  `E6BAB48FBFF69EE4F13AC8185E19101AA1DBD55992BBE1283DBA52B85AB277FD`.
- Local startup exposed an unrelated stale global `ServerKeybinds.dll` shadowing the correct port
  dependency. `tools/Start-BotTestServer8888.ps1` incorrectly selected an obsolete `bin/x64`
  artifact even though the AnyCPU project emits its canonical build under `bin/Release`; the tool
  now uses the canonical path.
- Port 8888's LabAPI dependency order was changed to `$port`, then `global`, preserving unrelated
  global dependencies while ensuring its isolated API-4 compatibility assembly wins binding.
- Final boot log:
  `%APPDATA%\SCP Secret Laboratory\LocalAdminLogs\8888\LocalAdmin Log 2026-09-05 15.03.03.txt`.
  SCPSLBot enabled successfully and UDP 8888 is bound.

## Production deployment

- All 19 listed players received a verified 30-second bilingual RA restart broadcast.
- Deployed only `SCPSLBot.dll` to port 7777 with SHA-256
  `E6BAB48FBFF69EE4F13AC8185E19101AA1DBD55992BBE1283DBA52B85AB277FD`.
- Rollback directory:
  `/home/scpsl/.config/SCP Secret Laboratory/LabAPI/backups/7777/20260905-150412-surface-teleport-hotfix/`.
- The service ignored graceful shutdown and required the exact-unit `SIGKILL` fallback after the
  warning window. It restarted active with UDP 7777 bound and correct `scpsl:scpsl:644` ownership.
- Fresh production log:
  `/home/scpsl/.config/SCP Secret Laboratory/LocalAdminLogs/7777/LocalAdmin Log 2026-09-05 15.04.57.txt`.
  SCPSLBot enabled successfully; three managed bots are alive with no controller/AI fault, and the
  log contains zero SCPSLBot errors, zero `Runtime loop recovered`, and zero
  `ReferenceHub.GetHashCode` occurrences.

## Remaining connected-client acceptance

1. As an SCP, confirm the room dropdown contains no Surface destination.
2. As an allowed NTF/Facility Guard, stage a Surface room, change to SCP without selecting another
   destination, and confirm Apply rejects the retained stale choice without moving the player.
