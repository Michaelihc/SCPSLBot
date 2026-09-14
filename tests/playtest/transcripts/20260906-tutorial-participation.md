# Tutorial outside warmup participation

Date: 2026-09-06 (Asia/Shanghai).

## Rule and scope

`Shared/WarmupParticipationPolicy.cs` contains the only Tutorial role decision, compiled into both
SCPSLBot and WarmupSafezone without a shared runtime DLL. Tutorial remains native and non-selectable:
no arena placement/evacuation, population contribution, auto-respawn role restoration, spawn-protection
clearing, warmup gameplay controls, warmup bot targeting, safezone protection or blocker drain.
Native RA and world-wide warmup settings remain independent of player participation.

Role events check the incoming role; action callbacks check current participation. Completed exits
discard old arena, respawn, protection and staged-selection state. This replaces the incomplete
Surface allowance and removes the obsolete Tutorial-to-ClassD spawn-anchor default.

The user explicitly requested that native protection stay unchanged. The live gameplay config still
has `spawn_protect_team: [1, 2]`; no protection setting was edited and no protection was granted.

## Automated checks

- SCPSLBot policy tests: 108/108 passed.
- WarmupSafezone deterministic logic tests: 54/54 passed.
- Reference-aware x64 Release solution build and playtest build: zero warnings/errors.
- Scenario lint: zero new errors; 11 unrelated legacy entries.
- `git diff --check`: passed.

## Local runtime setup

Temporary automated port 8891 uses the existing local dedicated server and plugin-specific evidence
under `tests/playtest/artifacts/tutorial-20260906/`. Cement owner membership was present before boot.
The global ServerKeybinds copy initially shadowed the compatibility DLL and prevented SCPSLBot from
enabling; that initial native-only run is not accepted as warmup verification. The per-port
`LabApi-8891.yml` now loads only that port's plugins/dependencies. No shared dependency was changed.

The stock LocalAdmin reader crashed when sent a command with redirected console handles. The final
suite uses only the harness's automatic runner, without interactive command injection.

An isolated Standard run with SCPSLBot and WarmupSafezone successfully enabled verified the native
Tutorial tower at `(40, 314.08, -32.6)`, distinct from the NTF anchor, with floor settling, a two-second
soak and no invented SpawnProtected effect. Final artifact suite results are recorded below.

## Final runtime results

- Final artifact suite: `tutorial-participation`, Quick, 2/2 passed, zero skipped/failures.
- Native tower scenario: passed, 3.99 seconds; native tower retained through settling and soak.
- Safezone boundary scenario: passed, 4.2 seconds. Both actors were probe-placed inside native
  SCP-914. Tutorial received the five-point test damage; the ClassD control retained its health,
  proving protection still applied to participating roles.
- Final summary: `tests/playtest/artifacts/tutorial-20260906/20260906-014143-quick.summary.json`.
- Final full log: `tests/playtest/artifacts/tutorial-20260906/la8891.log`.
- Both plugins enabled successfully in the final run. Test port 8891 was stopped afterward.

## Live deployment without restart

Replaced only SCPSLBot.dll and WarmupSafezone.dll under
`/home/scpsl/.config/SCP Secret Laboratory/LabAPI/plugins/7777` on bot `47.76.117.246`.
Each upload and final file was verified by SHA-256 and deployed as `scpsl:scpsl:644`.

- SCPSLBot SHA-256: `7718BD0E454805C6A497F1F65E0FA58437876EADF725FDBBEE9D8C0B988A220B`.
- WarmupSafezone SHA-256: `A9C23FA1D2F451F1C852103FABCE6A7EC19E23D68DBD2EC8A37B850499E34B86`.
- Verified ZIP rollback archive:
  `/home/scpsl/.config/SCP Secret Laboratory/LabAPI/backups/7777/tutorial-participation-20260905-174247.zip.bak`.
- Service active/running before and after; LocalAdmin PID 154153 and game PID 157248 unchanged.
  UDP 7777 remained bound on IPv4/IPv6; player queries responded (8 before, 9 afterward).
- `spawn_protect_team: [1, 2]` remained unchanged. No live config, restart, reload, restart intent,
  or player broadcast was issued. New behavior activates at a future game-process restart.

## Remaining client acceptance

Harness actors are dummies, deliberately outside authenticated-player management. A connected admin
must still verify native RA Tutorial assignments from both Surface and the facility, role re-entry,
warmup SSS hiding/stale callback rejection, and population accounting. Server dummies cannot establish
those authenticated paths without changing the production identity boundary.
