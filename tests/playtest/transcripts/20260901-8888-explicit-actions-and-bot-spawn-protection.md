# Port 8888 explicit-action and managed-bot spawn-protection verification

Date: 2026-09-01 (Asia/Shanghai)

## Automated checks

- `SCPSLBot.PolicyTests`: 70/70 passed.
- `ServerKeybinds.Compat.PureTests`: 10/10 passed after adding acquisition/change/duplicate classification coverage.
- `SCPSLBot` x64 Release build: zero warnings, zero errors.
- `SCPSLBot.PlaytestScenarios` x64 Release build: zero warnings, zero errors.
- Shared scenario linter: zero new errors; 11 unrelated legacy backlog entries.

The managed Surface-CI scenario now asserts that native `SpawnProtected` is inactive after initial
population creation, death/respawn repair, and a fresh Standard off/on population. The scenario was
compiled and installed on port 8888; its RA-driven death cycle still requires an explicit live
`scptest run bot-ci-spawn` invocation.

## Local deployment

- `SCPSLBot.dll` SHA-256: `D9D8341D3012AF4399F39A42C36BAE4625C28D60927335DD32247616335462BB`
- `SCPSLBot.PlaytestScenarios.dll` SHA-256: `A7FEC4E27C24FFAE32920FA816FA0F68A91D5EDCF755989E9741D3B148AC7065`
- `ServerKeybinds.dll` SHA-256: `0A9F49A3407853B130A24E253068A8818DDA25DE2182917E9FE241CF1A99EB3C`
- Restarted only local port 8888 at 21:27:48.
- LocalAdmin PID 45552; SCPSL PID 42744; UDP 8888 bound on IPv4 and IPv6.
- API 4 loaded successfully. Warmup block IDs registered in UI order:
  `1130001, 1130002, 1130003, 1130006, 1130004, 1130005, 1130007`.
- All three maintained bots reached exact `ChaosRifleman` roles.
- No `TypeLoadException`, `MissingMethodException`, or plugin-enable failure was present in the boot log.

Production port 7777 was not deployed to or restarted.

## Follow-up: visible acquisition and production-like native protection

The first client value after a personalized dropdown send was acquisition-only. That was safe for the
old immediate-action callback, but an explicit Apply workflow could visibly show a persisted non-placeholder
role while having no pending server value. API 4 now offers an opt-in staging-only acquisition callback.
Role, item, and arena use it; Teleport remains acquisition-swallowed because it still executes immediately.

Production was queried read-only and reports:

```yaml
spawn_protect_enabled: true
spawn_protect_time: default
spawn_protect_can_shoot: default
spawn_protect_prevent_all: default
spawn_protect_team: [1, 2]
```

Port 8888 was changed from `spawn_protect_enabled: default` to the same explicit `true`; the remaining
native spawn-protection tuple already matched. Managed bots still clear the resulting native effect after
each role assignment, while authenticated players retain production-like protection.

Final local boot: `LocalAdmin Log 2026-09-01 21.43.05.txt`. Runtime hash
`70E9D8FD2EF9DBFED2E5796A9EB986B94DAEFF27B0437DFD346D2866B2F0076E`; ServerKeybinds hash
`C36E156E23EB9095CA827BA4B500960E248B40448A412137EB3A3E0C5164A9AE`. LocalAdmin PID 47180,
SCPSL PID 29492, UDP 8888 bound. All three initial managed CI bots logged
`SPAWN_PROTECTION_CLEARED` after their native role assignment.

## Follow-up: Surface Foundation roles and varied HCZ/EZ entries

Surface now permits exactly `FacilityGuard`, `NtfPrivate`, `NtfSergeant`, `NtfCaptain`, and
`NtfSpecialist`. Every other human role is evacuated to HCZ/EZ and every SCP role to LCZ. Evacuation
feedback is a four-second native per-player broadcast (EN/CN), not an HSM hint.

HCZ/EZ entry placement now enumerates `DoorNametagExtension.NamedDoors`, resolves the exact position
with native RA `DoorTPCommand.EnsurePositionSafety`, rejects non-finite/non-HCZ-EZ/ungrounded targets,
deduplicates by generated `RoomIdentifier`, and round-robins the resulting rooms. SCP-939's validated
native spawn remains only as a fail-safe. Explicit role changes reuse the same selected entry through
pre-spawn and post-role verification so the rotation does not skip every second candidate.

- `SCPSLBot.PolicyTests`: 81/81 passed.
- `ServerKeybinds.Compat.PureTests`: 10/10 passed.
- Full suite and playtest builds: zero warnings, zero errors.
- Shared scenario linter: zero new errors.
- Final local boot: `LocalAdmin Log 2026-09-01 22.03.00.txt`.
- Final local `SCPSLBot.dll` SHA-256:
  `9E3145018D743F7CBF96B327D6CB6E9F2F9E7B4ED77C9FBD1161F2EE46B1E0D9`.
- LocalAdmin PID 50528; SCPSL PID 45888; UDP 8888 bound on IPv4 and IPv6.
- Loader, API 4 registration, three initial managed roles, spawn-protection clears, respawn scanner,
  and overflow-cleanup baseline are all present with no type/method/plugin-enable failure.

The empty-server baseline is Surface, so this boot did not naturally select an HCZ/EZ door. A connected
real-player Surface evacuation remains the final manual check; every selection emits a grep-friendly
`Arena spawn selected` record with door, room, zone, grid coordinates, position, and candidate count.
Production port 7777 was not modified or restarted.

### Immediate evacuation broadcast follow-up

Evacuation broadcasts now set LabAPI/native `shouldClearPrevious: true`. The affected player's stale
broadcast queue is flushed before the four-second evacuation message is enqueued, so it displays
immediately rather than waiting behind older notices. Final local boot:
`LocalAdmin Log 2026-09-01 22.06.00.txt`; SCPSLBot SHA-256
`E04AE36883858380033B3F3FED0085434D7225F333184208E89650F5D8924802`; LocalAdmin PID 36052;
SCPSL PID 25032; UDP 8888 bound. Build remained zero-warning/zero-error and policy tests remained
81/81. Production remained untouched.

### Spectator Surface-origin routing regression

The failing live request was captured before the fix:

```text
SSS respawn-role -> ChaosRepressor; originArena=SurfacePve, targetArena=SurfacePve,
relocateFromSurface=False, finalZone=None, finalPosition=(0.00, 0.00, 0.00)
```

The role transition incorrectly treated the Spectator camera as a physical actor origin. Spectators
now route from server-owned arena membership; playable roles continue to prefer the generated-map room.
A logical Surface spectator requesting CI therefore selects HCZ/EZ and its RA-door entry before native
assignment. The same event-owned rule covers the global spectator respawn scanner.

- Added five spectator-vs-playable origin matrix cases; policy tests now pass 86/86.
- Release build: zero warnings, zero errors.
- Final local boot: `LocalAdmin Log 2026-09-01 22.10.34.txt`.
- SCPSLBot SHA-256: `F87A9CCE11C8B452B561991075CE6CBFA424B532389FC5D5FACE92CFF730812C`.
- LocalAdmin PID 28900; SCPSL PID 16948; UDP 8888 bound on IPv4 and IPv6.
- Loader, API 4 registration, maintained roles, spawn-protection clears, and overflow cleanup are clean.

Production remained untouched.

### Persistent staged selections and immediate button feedback

The server previously consumed the staged Role/Item/Arena value after a successful button action (and
also forgot it during death or role-change refreshes) while the client continued to display that value.
This produced the misleading `select an item` response on the next Grant click. Successful actions now
retain the visible staged selection, including across personalized death/role refreshes. Every click
still revalidates the current role, item, zone, identity, cooldown, and configured limits before acting.

All SSS button feedback now uses native `shouldClearPrevious: true`, immediately flushing stale queued
broadcasts. Surface-evacuation feedback is scheduled for the next server tick after Apply feedback and
also flushes the queue, ensuring the important evacuation notice is the final visible message.

- Added a repeated-read regression test for staged item selections; policy tests pass 87/87.
- Release build: zero warnings, zero errors.
- Final local boot: `LocalAdmin Log 2026-09-01 22.17.13.txt`.
- SCPSLBot SHA-256: `9A6A189E79FD696A9F0C14551C6597FD52C6EE485CE122F5B755FA9810B37238`.
- LocalAdmin PID 48932; SCPSL PID 52964; UDP 8888 bound on IPv4 and IPv6.
- Loader, API 4 registration, all three managed CI bots, spawn-protection clearing, and cleanup baseline
  are clean with no type-load, missing-method, or plugin-enable failure.

Production remained untouched.
