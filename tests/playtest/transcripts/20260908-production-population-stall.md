# Production population stall diagnosis

Read-only inspection on 2026-09-08, approximately 19:17–19:21 Asia/Shanghai.
Target: bot / scpsl-warmup-hk / 47.76.117.246 / UDP 7777.

## Observed

- Service active/running, LocalAdmin PID 163379, game PID 163397 (started September 7 00:16:44 host time), service NRestarts=0.
- Two serialized `scpsl-ctl ra bot_status` queries failed with the same stack:

```text
System.NullReferenceException: Object reference not set to an instance of an object.
UnityEngine.Component.get_gameObject()
ReferenceHub.GetHashCode()
System.Collections.Generic.Dictionary`2.Remove(TKey key)
SCPSLBot.Warmup.BotPopulationController.PruneMissingEntries()
SCPSLBot.Warmup.BotPopulationController.GetDiagnostics()
SCPSLBot.AI.Commands.BotStatusCommand.Execute(...)
```

- The last successful BotOrders role/spawn-protection records in the current service process journal were September 8 16:46:39–16:46:40. A bot reached Destroyed at 16:49:08. Two bot Spectator transitions at 17:20:02 and 17:20:19 have no subsequent bot role-recovery records through inspection time. Exact initial failure time and which destroyed bot poisoned the collection are not established.
- Real players still joined and were respawned onto Surface during inspection.
- Deployed SCPSLBot SHA-256 is `821d5978bd70ed3533b21a182c601aff5571628b5354a831e8c8f937251a3790`, matching the September 6 navigation-recovery release. The running command stack has module ID `49e9800c449f4396b9ec7f4854743d75`; game start follows that deployment.
- No NAV_LOAD retry/recovery records were found in the inspected current-process journal. Current navigation readiness cannot be read because bot_status fails first.
- An additional arena placement NullReferenceException was logged at 16:46:39 in `WarmupArenaService.PlaceAtArenaEntry`, through native FPC position override on a missing Transform. Its relationship to the population stall is unproven.

## Source trace

- `SCPSLBot/Warmup/BotPopulationController.cs:20` uses default ReferenceHub dictionary equality/hash behavior.
- `PruneMissingEntries`, lines 496–509, detects Unity-null destroyed keys, then calls `entries.Remove(pair.Key)` at line 502, invoking the destroyed key's hash override.
- Local dedicated-server decompiled evidence: `../.references/Decompiled/DedicatedServer/Assembly-CSharp/ReferenceHub.cs:412`, `GetHashCode()` returns `base.gameObject.GetHashCode()`. This requires a surviving native GameObject.
- `Reconcile()` calls pruning at line 244 before navigation gating, role repair, and spawning. `RunReconciler()` catches exceptions but retries the same poisoned collection every 0.25 seconds. Catching does not remove the bad entry. This explains the persistent server-wide managed-population blockage.
- Reconciler errors use Unity Debug logging rather than LabAPI logging; no recurring reconciler stack was visible in the inspected LocalAdmin journal. The two live diagnostic failures directly establish the poisoned collection; the repeated maintenance failure follows from the source path.

## Result and remaining work

Confirmed persistent destroyed-reference cleanup defect. A durable repair should use stable managed-reference identity for retained hub keys, audit related bot lifetime collections, and verify recovery after native dummy destruction plus replacement/grounding in an isolated local server. Improve fault visibility without logging the same exception four times per second.

No code/config/DLL changes, production restart, or gameplay test commands were performed. No automated behavior tests apply to this read-only diagnosis; verification consisted of live serialized diagnostics, service/process state, artifact hash, journal history, and source tracing. A repair and its runtime tests remain outstanding.
