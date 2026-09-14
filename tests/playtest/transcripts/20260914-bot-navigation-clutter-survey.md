# 2026-09-14 Bot navigation rework: clutter connectors, missing room meshes, stuck recovery

Isolated headless port 8891, maintained LocalAdmin fork, fresh map seed per round. Driver:
`python tests/playtest/tools/check_connector_survey.py`. Every traversal is native FPC walking
through `BotOrders`; teleports are setup only. Per-case evidence is in each artifact folder's
`survey-round<n>.log` and the full `la8891-round<n>.log`.

## Offline clutter analysis (client asset export 14.2.7)

The exported connector prefabs (`.references/scpsl-client-asset-export-14.2.7/Assets/PrefabHierarchyObject/*Open Connector.glb`)
were rasterized top-down at walking heights (connector-local frame, +z through-axis, x lateral):

| Connector | Free lateral band at the doorway plane (mid/high band) | Notes |
|---|---|---|
| OpenHallway | whole opening | ceiling seal only |
| Simple Boxes | x in [-0.3, +2.9] | boxes stacked on the -x side, extending 2.5 m into one room |
| Boxes Ladder | x in [-0.4, +0.7] (about 1.1 m) | piles on both sides, low crates (0.25-0.6 m) inside the slot |
| Tank-Supported Shelf | x in [+0.3, +2.9] | shelf across the -x half |
| Angled Fences | S-curve: enter near x=-0.5, exit near x=+0.3 | diagonal fences, no straight free column |
| Huge Orange Pipes | x in [-0.2, +0.6] (about 0.8 m) plus lanes under overhead pipes | pipes at chest height |
| Pipes Long / Pipes Short | x < +1.1 | pipes along the +x wall |
| Broken Electrical Box | x < +1.7 | box on the +x side |

Conclusion: linking rooms at the connector center and steering at the midpoint or corners of the
full doorway edge aims bots into clutter; the passage must be probed per instance and per round.

## Baseline reproduction (pre-rework DLL sha256 87134a54...)

Artifacts: `tests/playtest/artifacts/baseline-connector-survey-20260914-111918/`.

| Round | Cases | Passed | Failed | Rooms without navmesh | Failures |
|---|---|---|---|---|---|
| 1 | 36 | 34 | 2 | 4 (`HCZ_IncineratorWayside`, `HCZ_Intersection_Ramp`, `EZ_Cafeteria`, `EZ_Straight`) | Boxes Ladder at the MicroHID room, both directions (TIMEOUT) |
| 2 | 30 | 29 | 1 | 2 (`HCZ_IncineratorWayside`, `HCZ_Intersection_Ramp`) | Broken Electrical Box from the MicroHID upper level (TIMEOUT) |

Reproduced stuck mechanics (from `[BotOrders] STALL` / breadcrumbs):

- Funnel corner wedged on a door frame: waypoint 1 m away, `blocker=HCZ_Doors_Doorframe (1)@Default:0.01m`, four stalls, replans returned the same corner.
- Nudge oscillation on the MicroHID lower level: the alternating sideways nudge displaced the bot more than the 0.35 m stuck threshold every cycle, so the old ladder never escalated (no stall logged for 25 s, remaining distance never improved).
- Rooms without any cells make every route through them impossible and leave bots inside them with no path.

## Intermediate runs

- Strict straight-column probe (`fixed-connector-survey-20260914-113433`): room fill works (0 rooms without mesh, `NAV_ROOM_GENERATED` 78 + 47 cells), but S-curve and narrow passages were reported sealed and unlinked, so Angled Fences, Huge Orange Pipes and Boxes Ladder cases became `FailedNoPath` or long detours (12/30 and 8/22 failed). Also exposed authored cells on other floors (`HCZ_049` upper area at world y=93, `HCZ_Nuke` warhead level) that the survey must not use as start/goal.
- Corridor probe with capsule-width still double-applied (`corridor-connector-survey-*`): Angled Fences pass again (8/8); Boxes Ladder and Huge Orange Pipes remained sealed because the free-column span (already capsule-center space) was compared against a full capsule width.

- Corridor probe with capsule-center gap semantics (`final-connector-survey-20260914-114546`, `final2-*`, `final3-*`): every clutter type passes on every seed except cases whose start or goal cell the survey had picked on the MicroHID upper platform (4.5 m above the connector floor). Telemetry there: `[BotNav] STUCK ... obstacle=HCZ_Doors_Doorframe (1)@0.13m` repeating with `replans=0` and `stuck=1.5`, i.e. the bot pressed head-on into the door frame beside the platform's 0.5 m authored doorway (`HCZ_MicroHID_New` cell 26) and every forced jump landed a few centimeters nearer, resetting the ladder before back-off/replan could fire. Fixed by demanding a 0.6 m stride as progress while recovering and by choosing the free side when sliding head-on; the survey now also confines start/goal cells to the connector's floor (2.5 m), because the platform descent is a room-interior route rather than a connector traversal.

## Final verification

Verified build: `SCPSLBot.dll` sha256 `6d073e3c1e30e87875fd5709121be6c278f68d246bce32c18368fddcb251d528`
(`SCPSLBot.PlaytestScenarios.dll` c7a8bfd8..., `PlaytestHarness.dll` 54d5b24c...). The committed
source differs from that build only by one removed unused `using` in `BotConnectorSurvey.cs`.

Connector survey, `tests/playtest/artifacts/verified-connector-survey-20260914-120758/`:

| Round | Cases | Passed | Failed | Rooms without navmesh | Sealed links | Stuck episodes |
|---|---|---|---|---|---|---|
| 1 | 22 | 22 | 0 | 0 (`NAV_ROOM_GENERATED`: IncineratorWayside 23, Intersection_Ramp 60, EZ_Cafeteria 28, EZ_Straight 28 cells) | 0 | 0 |
| 2 | 32 | 32 | 0 | 0 (IncineratorWayside 23, Intersection_Ramp 60) | 0 | 0 |

All 27 probed connectors across both seeds linked on the Walk tier (no Jump-tier fallback was
needed); every clutter type present on the seeds passed in both directions: Angled Fences, Boxes
Ladder, Broken Electrical Box, Huge Orange Pipes, Pipes Short, Simple Boxes, Tank-Supported Shelf,
Open Hallway. Unit tests: `SCPSLBot.PolicyTests` 145/145 passed.

Lifecycle suite and door survey on that build (`lifecycle-regression-20260914-121041`,
`door-survey-20260914-*`): `scpslbot-destroyed-bot-recovery`, `population-recovery`,
`role-death-recovery` and `surface-managed-chaos-spawn` passed, `surface-native-role-routing`
skipped (needs a real client), but `scpslbot-native-walk-grounding` timed out on the SCP-173 upper
walkway and 10 of 96 plain doors timed out with the bot frozen 0.45 m (capsule radius plus leaf)
in front of a closed door, `stalls=0`, no `[BotNav]` lines. Breadcrumb telemetry (`intent=1.00
stuck=0.0`, path index stepping back and forth) showed the mechanism: when the bot stood within
0.35 m of a portal corner the funnel advanced the path index, the next tick's alignment stepped it
back, and each advance bumped the progress stamp, so the recovery ladder that owns door opening
never started. Fixed by never aligning the index backwards, counting progress only on a real edge
crossing, and falling back to the door's nearest interactable collider when the camera ray hits
the leaf. One `FailedNoPath` into `HCZ_IncineratorWayside` (a generated dead-end side room whose
door reports a single room) remains and is documented below.

### Alignment / door fix build

`SCPSLBot.dll` sha256 `57dedfa9060f8f41fcf5ed530a954136c99c75fe27bf060431375b07bdd666ff` (adds the
no-backward-alignment rule, real-crossing progress stamp, direct door interaction fallback and
position-based room resolution for doors whose native registration lists one room).

| Run | Artifacts | Result |
|---|---|---|
| Lifecycle suite (`scpslbot-lifecycle`) | `final-lifecycle-regression-20260914-*` | 5 passed, 0 failed, 1 skipped (`surface-native-role-routing` needs a real client); `scpslbot-native-walk-grounding` walked LczToilets -> Lcz173 in 64.9 s with 0 stalls |
| Door survey (`scpslbot-door-survey`, forward) | `final-door-survey-20260914-*` (build 711bae51, same IL plus the penalized last-resort link) | 96/97 plain doors passed (BreakableDoor 88/89, PryableDoor 8/8); the single `FailedNoPath` is the door into the generated `HCZ_IncineratorWayside` side room, whose doorway cells were carved out by the closed leaf during room-fill probing |

### Final source builds

| Run | Build | Artifacts | Result |
|---|---|---|---|
| Connector survey, two seeds | sha256 `5ad3cd863fe107cf9bb6e568556e34381ae7351013061c700e33bb5c034d9367` | `final-build-connector-survey-20260914-124856` | 28/28 and 32/32 passed, 0 sealed links, 0 rooms without mesh (generated: IncineratorWayside 23, Intersection_Ramp 60, EZ_Straight 28, EZ_Cafeteria 28 cells), one recovered stuck episode logged in round 1 |
| Door survey, forward | sha256 `05c6b9370fef024a71b0b61f0b0cfbcf1f0f47d8af4c2c40f310cfe8515d361f` (adds the Door-layer exclusion in room-fill probing; otherwise identical source) | `final-build-door-survey-20260914-125213` | 97/98 plain doors passed (BreakableDoor 89/90, PryableDoor 8/8); 53 recovered stuck episodes (door waits) logged, none fatal |

Unit tests on the final source: `SCPSLBot.PolicyTests` 145/145 passed. `node .tests/lint-scenarios.js`: errors=0.

## Known residual

- The door into the generated `HCZ_IncineratorWayside` side room (a dead end) still yields
  `FailedNoPath` for a bot ordered into it, on every seed it appeared (`HCZ_Intersection`,
  `HCZ_Corner_Deep`, `HCZ_Tesla_Rework`, `HCZ_Crossing` sides). The room is filled (23 cells) and
  both position-based room resolution and the Door-layer exclusion were tried; the remaining suspect
  is the wayside's nearest-edge/height relation to that door point, which needs in-game inspection
  with `nav` diagnostics. Roaming bots simply never enter that side room.
- Authored cells on other floors (`HCZ_049` upper area at world y=93, `HCZ_Nuke` warhead level,
  `LCZ_173` walkway) are reachable only through their native elevators/stairs; the survey confines
  start and goal cells to the connector floor and does not exercise them.
