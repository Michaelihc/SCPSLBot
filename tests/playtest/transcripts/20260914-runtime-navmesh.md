# Runtime navmesh backend, 2026-09-14

Plan: `docs/runtime-navmesh-plan.md`. Every run below booted the isolated headless port 8891
through `tests/playtest/tools/check_connector_survey.py` against the patched dedicated-server
assets (`NavMeshAssetPatcher verify` exit 0, tool 1.1.0, Unity 6000.0.43f1). Evidence folders are
under `tests/playtest/artifacts/runtime-*`.

## Server asset patch (phase 1)

| Step | Result |
|---|---|
| `NavMeshAssetPatcher report` (stock server) | 3167 meshes, 2910 unreadable, 84 with streamed vertex data |
| `patch` (readable flag only, tool 1.0.0) | probe: unreadable 0, coverage 90/103, long path NONE; HCZ room shells still reported 0 triangles (streamed vertex data kept no CPU copy) |
| `patch` (readable + inlined vertex data, tool 1.1.0) | 3015 meshes patched, 84 inlined (5.4 MB), 5 files rewritten with `.bak`, manifest written |
| `scpslbot-unity-navmesh-probe` on the inlined patch | sources 5792, unreadable 0, 1,000,504 readable triangles, build 297 ms, coverage 102/104 room centres (the two misses sit under pipes / over a pit with surface within 5 m), 14/14 connector paths |

## Bake (phase 2), read through `bot_status` / `nav status`

Typical `NAV_BAKED` line (seed 1611985961): bake 756 ms wall (async build, main-thread index 277 ms),
4655 sources, 21620 triangles, 11 elevator links, 1 passage link, 0 sealed connectors, 8 keycard
door classes, 22 modifier boxes, 0 unreadable meshes, 0 fallback rooms, 107/107 rooms indexed,
5509 samples (3590 elevated island samples filtered), 248 room entries, no uncovered rooms.
Reconciliation with the order-independent source hash: 1 tile rebuild per lifecycle run (the
warmup arena spawn), where the first implementation rebuilt every 5 s.

## Gates (phases 3 and 4)

| Gate | Command | Result |
|---|---|---|
| Runtime navmesh gate | `--scenario scpslbot-runtime-navmesh-gate` | PASS: runtime backend, unreadable 0, uncovered 0, links 11, door classes 8, bake under 5 s, index under 1.5 s; complete paths LczClassDSpawn to HczWarhead (through the checkpoint elevator), LczClassDSpawn to Lcz914, Hcz049 to EzGateA, EzGateA to Outside (gate elevator), each query under 1 ms |
| Keycard routing | `--scenario scpslbot-keycard-routing-survey` | PASS 20/20 asserted doors (2 skipped: LCZ checkpoint-gate anchors in a floor pocket between the gate bars); no door class lets a bot without the card through, every class lets the holder through |
| Clutter connectors, 2 seeds | `--rounds 2` | PASS 30/30 and 26/26 native walks, huge orange pipes 4/4 and 2/2 through the jump-tier passage link |
| Plain doors | `--scenario scpslbot-door-survey` | PASS 93/93 native walks (2 skipped: the bot's start inside the 914 chamber and the Gate B vestibule only leads onward through card-restricted space; seed 829334572 before the reachable-goal fix showed 11 such cases) |
| Lifecycle suite | `--scenario scpslbot-lifecycle` | PASS 5/5 (1 intentional skip: Surface role routing needs a real client) including the native preset walk LczToilets to Lcz173 |
| Unit tests | `dotnet test SCPSLBot.PolicyTests` | 160/160 (15 new: DoorAreaRegistry, PathReplanPolicy) |
| Scenario lint | `node ..\.tests\lint-scenarios.js` | errors 0 |

Final chain on the shipped build (seeds 1472725197 and later, evidence `runtime-keycardY`, `runtime-gateZ`,
`runtime-connectorZ`, `runtime-lifecycleZ`, `runtime-doorH`): keycard 20/20 asserted (2 surface-gap skips at
LCZ checkpoint gates), gate PASS, connectors 30/30 and 32/32, plain doors 93/93 (2 permission skips),
lifecycle 5/5, 1 tile rebuild per run, 160/160 unit tests, scenario lint errors 0.

## Defects found and fixed during the gates

- Streamed vertex data: readable flag alone leaves streamed meshes with zero triangles at runtime; the patcher now inlines `.resS` data.
- Room survey anchors were teleported to floor-level samples (capsule half inside the floor); teleports now probe the floor and place the capsule one root height above it.
- Combat steers at the target and at a door hit point within one tick; the navigator keeps two goal-keyed plans instead of re-planning twice per tick, and creeping goals re-plan at most once per second.
- Door prefabs carry camera-only (CCTV), glass and interaction colliders; sources use the player collision mask minus door leaves and glass, and invisible doorway blockers (096 chamber) are ignored.
- Checkpoint gate modifier boxes were sized from 10 m frames; boxes are now centred on the door, bounded in depth and widened to the opening for gates, and cached so opening doors never change the source hash.
- Huge orange pipe connectors are sealed by the eroded surface; a capsule-probed jump-tier link bridges them.
- Elevated island samples (pipe tops, ledges) polluted room goals; the index drops them with a reverse path test (0.3 s per index, not 2.8 s).
- The source hash was order-sensitive, so physics iteration order rebuilt tiles every cycle; the hash is now a commutative digest and rebuilds only on real changes (helicopter and capybara movers are excluded).
- A query issued while a tile is swapped returns PathInvalid; plans are kept during a rebuild, transient failures retry next tick, and orders get a 1.5 s no-path grace.
- Stair landings straight above the bot stalled the corner follower; corners within arrival distance are passed regardless of height.

## Known limitations (manual items)

- The Intercom room interior does not voxelize; bots stop at the Intercom doorway.
- Two LCZ checkpoint-gate keycard cases skip because their anchors fall into a floor pocket between the gate bars; the gate scenario's LczClassDSpawn to HczWarhead path proves the gates are crossable.
- Elevator travel by bots (link handover to `CallAndWaitForElevator` / `TravelOnElevator`) and keycard routing with real players remain live/manual verification.
