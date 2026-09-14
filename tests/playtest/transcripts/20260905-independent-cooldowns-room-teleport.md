# Independent panel cooldowns and native room teleport

Date: 2026-09-05 (Asia/Shanghai)

## Automated checks

- `SCPSLBotAddon.sln` x64 Release build: zero warnings, zero errors.
- `SCPSLBot.PlaytestScenarios` Release build: zero warnings, zero errors.
- `SCPSLBot.PolicyTests`: 96/96 passed.
- Shared scenario linter: zero new errors; 11 unrelated legacy backlog entries.
- `git diff --check`: no whitespace errors.

## Isolated port 8888 verification

- Deployed `SCPSLBot.dll` SHA-256:
  `D16AA8B76EF654FCBEC94E1AFC195BF425B8636E2C696C6B34B342F7643E591A`.
- `ptest run scpslbot-native-door-teleports standard`: 1/1 passed in 2.25 seconds.
  The scenario moved a dummy through all 35 native RA named-door destinations and verified each
  exact native resolved position, expected room, and a valid ground raycast.
- The first run moved through all 35 destinations but was rejected by the harness because the
  deliberate teleports were not registered as expected feature transitions. The scenario was
  corrected to declare those transitions; no runtime teleport behavior changed for the rerun.
- Persisted port 8888 configuration contains independent role/item/teleport/arena cooldowns of
  6/1/1/5 seconds and all five default high-impact items at one per life and 999 per round.
- Port 8888 was restarted after verification and remains running with UDP port 8888 bound.

## Production deployment

- Deployed only `SCPSLBot.dll` to port 7777 with SHA-256
  `D16AA8B76EF654FCBEC94E1AFC195BF425B8636E2C696C6B34B342F7643E591A`.
- Preserved the unrelated deployed `SCPSLBot.Components.dll`; no Components source changed.
- Rollback directory:
  `/home/scpsl/.config/SCP Secret Laboratory/LabAPI/backups/7777/20260905-023303-scpslbot-panel-doors/`.
- Three connected humans and six bots received a verified 30-second bilingual RA broadcast before
  restart. The service again required an exact-service forced termination after ignoring its
  graceful exit request.
- Startup verification passed: service active, UDP 7777 bound, SCPSLBot loaded and enabled, and
  three managed bots alive with no controller fault.
- Persisted production configuration contains independent role/item/teleport/arena cooldowns of
  6/1/1/5 seconds. GrenadeHE, GrenadeFlash, MicroHID, ParticleDisruptor, and Jailbird each retain a
  one-per-life limit and now have a 999-per-round limit.
- The fresh production log contains zero SCPSLBot errors, zero `Runtime loop recovered`, and zero
  `ReferenceHub.GetHashCode` occurrences.

## Remaining connected-client acceptance

- Confirm the SSS teleport dropdown presents rooms/native named doors instead of players, stages a
  selection, and teleports only after **Apply Teleport** is pressed.
- Confirm role, item, teleport, and arena actions throttle independently and that the role cooldown
  lasts six seconds. These client UI interactions cannot be driven by the server dummy harness.
