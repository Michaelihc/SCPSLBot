# Managed and independent bot ownership

Date: 2026-09-05 (Asia/Shanghai)

## Automated checks

- `SCPSLBotAddon.sln` x64 Release build: zero warnings, zero errors.
- `SCPSLBot.PlaytestScenarios` Release build: zero warnings, zero errors.
- `SCPSLBot.PolicyTests`: 93/93 passed.
- Shared scenario linter: zero new errors; 11 unrelated legacy backlog entries.
- `git diff --check`: no whitespace errors.

## Isolated port 8888 verification

- Deployed `SCPSLBot.dll` SHA-256:
  `2ACDBC4C2EE87C59387367F306CB987B4C1C4831AEEADAA6448BFD5D5797141C`.
- `ptest run scpslbot-bot-ownership standard`: 1/1 passed in 3.64 seconds.
  It verified that `bot_add` does not change the managed population, RA role changes persist on an
  independent bot, `bot_manage` transfers a configured managed slot to the selected bot, managed
  role drift is repaired, and `bot_unmanage` leaves the selected bot alive while the controller
  creates a replacement for the released slot. The cleanup now also verifies that LabAPI does not
  retain or recreate a wrapper for the destroyed bot hub.
- `ptest run scpslbot-surface-managed-chaos-spawn standard`: 1/1 passed in 0.99 seconds.
  This regression check verified that population-managed bots still receive their assigned role,
  native reinforcement placement, and immediate `SpawnProtected` removal.
- Port 8888 was restarted through `tools/Start-BotTestServer8888.ps1` after the scenarios. The
  visible LocalAdmin and child game processes are running, and UDP port 8888 is bound.
- Port 8888 remained running after verification.

## Production deployment

- Deployed only `SCPSLBot.dll` to port 7777 with SHA-256
  `2ACDBC4C2EE87C59387367F306CB987B4C1C4831AEEADAA6448BFD5D5797141C`.
- Preserved the unrelated deployed `SCPSLBot.Components.dll`; no Components source changed.
- Rollback copy:
  `/home/scpsl/.config/SCP Secret Laboratory/LabAPI/backups/7777/20260905-020123-scpslbot-hotfix/SCPSLBot.dll`.
- One connected human received a verified 30-second bilingual RA broadcast before restart. The
  service required an exact-service forced termination after ignoring its graceful exit request.
- Startup verification passed: service active, UDP 7777 bound, SCPSLBot loaded and enabled, three
  managed bots alive, and native `SpawnProtected` cleared from all three bots.
- Production smoke test created independent bot 5, assigned `ClassD` through native RA, and
  confirmed the role persisted. `bot_manage 5`, `bot_unmanage 5`, and a final `bot_manage 5` all
  succeeded; the final state was `tracked=3; owned=3; independent=0`.
- The fresh production log contained zero `Runtime loop recovered` and zero
  `ReferenceHub.GetHashCode` occurrences after bot replacement/destruction.

## Manual RA surface

- `bot_add` creates an independent bot and reports its player ID.
- `bot_manage <player ID>` adopts an SCPSLBot dummy into population management. If all configured
  slots are occupied, one existing managed dummy is despawned and its slot is transferred.
- `bot_unmanage <player ID>` releases a managed dummy without despawning it; the population
  controller backfills the now-empty configured slot.
