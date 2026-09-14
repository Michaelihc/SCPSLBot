# Production destroyed-bot recovery deployment

Date: 2026-09-08, Asia/Shanghai. Target: bot / scpsl-warmup-hk / 47.76.117.246 / port 7777.
User authorized deploying the accepted fix, arming native RNR, and restarting if fewer than
three real players were connected. Subsequent steering explicitly confirmed native restart.

## Artifact installation

The host has no `scpsl-ops` executable on PATH and no matching installation found within the
bounded /opt, /usr/local, /etc, /var/lib search. Used the existing single-artifact fallback
pattern, implemented as `tools/Deploy-DestroyedBotRecovery.py`, rather than commissioning a
new operations installation. It validates hostname, source path, accepted payload hash, and
the prior production hash; creates and read-verifies a rollback ZIP; preserves ownership;
and atomically replaces only `LabAPI/plugins/7777/SCPSLBot.dll` with mode 644.

- Previous SHA-256: `821d5978bd70ed3533b21a182c601aff5571628b5354a831e8c8f937251a3790`.
- Accepted/deployed SHA-256: `8b4163539d8342c4d817ed4af9d835ebe7d0908c338bf2fe01b7183023cd8d26`.
- Installed at 19:41:34; verified rollback archive:
  `/home/scpsl/.config/SCP Secret Laboratory/LabAPI/backups/7777/destroyed-bot-recovery-20260908-114134.zip.bak`.
- No companion DLL, configuration, or persistent gameplay data was replaced.
- Before deployment, the old process had already recovered to 3/3 bots in map generation 4;
  this does not invalidate the earlier captured poisoned-collection failure or local reproduction.

## Native restart

The complete native roster showed one real player and three bots. The native header said four,
because it includes dummies. Rechecked the roster after installation and after arming RNR;
it still contained one real player, meeting the user's strict `< 3` condition.

All commands used the serialized canonical wrapper:

```text
scpsl-ctl ra nextround
NEXTROUND#Round will normally restart.
scpsl-ctl ra rnr
RNR#Server WILL restart after next round.
scpsl-ctl ra nextround
NEXTROUND#Server will RESTART.
scpsl-ctl console restart
```

At 19:42:04, the journal records `>>> restart`, StatsSystem saving on shutdown, native server
shutdown initiation, query-server shutdown and the shutdown broadcast. Native RNR plus native
round restart caused the game to exit and LocalAdmin to launch the replacement at 19:42:08.
No systemctl/service restart, forced process kill, or second restart was performed.

Native command source (decompiled dedicated-server evidence):
`../.references/Decompiled/DedicatedServer/Assembly-CSharp/CommandSystem/Commands/Shared/RestartNextRoundCommand.cs`
and `CommandSystem/Commands/Console/RoundRestartCommand.cs`; the latter calls
`RoundRestart.InitiateRoundRestart()`. RNR is a toggle, so readback used `nextround`.

## Startup acceptance

- LocalAdmin PID **163379** remained unchanged; service active/running, NRestarts=0.
- Game PID changed from **163397** to **172903**, started 19:42:08.
- LabAPI enabled SCPSLBot at 19:42:12; waiting-for-players and first heartbeat at 19:42:18.
- UDP 7777 bound on IPv4 and IPv6 to the new game PID.
- Live DLL hash matched the accepted artifact after restart.
- `bot_status` at 19:42:27 and 19:43:07 showed **3/3 live**, `arenas=surface:3`, network/navigation
  ready, current generation 2, and no spawn, reconciliation, navigation, or AI fault.
- Real players reconnected successfully. No production dummy-destruction fault injection was
  performed; destruction recovery and grounding were verified by the five local acceptances.
- The one-shot RNR was consumed by the restart. Final `nextround` reports normal round restart;
  no additional restart was armed.
- Only observed startup error: the pre-existing duplicate `getleaderboard` registration from
  StatsSystem. No post-restart population, navigation, or exception records were found through
  19:43:07. Authenticated client visual/gameplay QA remains an operator check.

Deployment and activation are complete. Local test evidence:
`20260908-destroyed-bot-recovery.md` in this directory.
