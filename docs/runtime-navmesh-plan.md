# Runtime navmesh plan (server mod + Unity Recast)

Status: implemented 2026-09-14 (phases 1 to 4). Developer document, English only.

Implementation notes (deviations from the proposal below):

- Readable flags alone were not enough. 84 collider meshes (all HCZ room shells among them) keep
  their vertex data streamed in `.resS`; Unity retains no CPU copy for streamed data even when
  `m_IsReadable = 1`, so those meshes reported zero triangles. `tools/NavMeshAssetPatcher` therefore
  also inlines the streamed bytes into `m_VertexData` and sets `m_KeepVertices/m_KeepIndices`.
  Patched files grow by about 5 MB in total; `.resS` files stay untouched.
- The initial build runs through `UpdateNavMeshDataAsync` on an empty `NavMeshData` (off the main
  thread) instead of the synchronous `BuildNavMeshData`; the room index (samples, entry points,
  island filter) is the main-thread cost.
- Keycard routing uses one navmesh area per distinct permission class (flags + require-all),
  not one per flag, so any-of/all-of policies and `ScpOverride` doors are exact.
- Reconciliation hashes the collected sources and only calls Unity when the hash changed; players,
  ragdolls, pickups and elevator chambers are excluded through build markups so their movement never
  triggers tile rebuilds.
- The navigator keeps two goal-keyed plans per bot because combat steers at the target and at a
  door hit point within one tick, and creeping goals (chased players) re-plan at most once per second.
- The physics-probed room fill survives as `FloorProbeSourceBuilder`, a build source used only for
  rooms whose collider meshes remain unreadable (none on the patched server).
- `navigation.backend: authored` and the cell editor are kept for one release as planned.

## Goal

Replace the hand-authored cell mesh (`Assets/navmesh.slnmf`), the connector passage probes and the
runtime room fill with a navmesh baked on the dedicated server at map load from the real collision
geometry, so that every room, every clutter connector, every seasonal variant and every mid-round
geometry change is covered without per-type logic. Bots keep native FPC movement; only the planner
changes.

## Evidence that it works on this server (probe `scpslbot-unity-navmesh-probe`, 2026-09-14)

| Fact | Measured |
|---|---|
| `UnityEngine.AIModule` present on the dedicated server (Unity 6000.0.43f1) | yes: `NavMeshBuilder`, `BuildNavMeshData`, `UpdateNavMeshDataAsync`, `NavMeshQuery`, `OffMeshLink` |
| Physics collider sources collected | 5,832 in 4 ms (3,545 boxes, 1,599 meshes, 629 capsules, 58 spheres, 1 terrain) |
| Whole-facility bake, voxel 0.09 m, tile 128 | 388 ms synchronous, 14,431 triangles |
| Path query | 0.02 ms average |
| Blocker | 1,087 of 1,599 mesh colliders are non-readable in the server build and are dropped silently, so HCZ rooms have no floor in the result (55 of 105 rooms covered, 1 of 17 connector paths) |
| Where those meshes live | `SCPSL_Data/resources.assets` (178 MB) and `sharedassets2.assets`; e.g. `HCZ_DoorFrame.collider`, `Modular_Small_Pipe_Straight_5m_Collider`, `HCZ_Modular_Wall_Trim` |

Unity documents that meshes with read/write disabled are excluded from runtime navmesh builds, so
the fix is to make the collider meshes readable in the server's asset files. Client files are
untouched.

## Architecture

### 1. Server asset patch (new tool `tools/NavMeshAssetPatcher`)

- .NET 8 console using `AssetsTools.NET` 3.x (MIT) plus the UABE class database for Unity
  6000.0.x (player builds strip type trees). New tool dependency, tools folder only; the plugin
  itself gains no dependency.
- Input: the server's `SCPSL_Data/resources.assets` and `sharedassets*.assets`. For every `Mesh`
  asset referenced by a `MeshCollider` (or, simpler and safe on a headless server, every `Mesh`),
  set `m_IsReadable = 1`. Sizes do not change, so `.resS` streams stay valid.
- Output: patched copies written atomically next to the originals with `.bak` backups, a manifest
  (`navmesh-asset-patch.json`: game build, file hashes before/after, mesh count) and a `--verify`
  mode that reports whether the live files carry the patch.
- Runbook integration: `tools/Start-BotTestServer8888.ps1` and the production deploy scripts call
  `--verify` and refuse to start with an unpatched server (Steam validation restores originals),
  printing the one command to re-apply. Re-apply after every game update; the manifest records
  the build it was made for.
- Acceptance gate: probe reports `unreadableMeshes=0`, coverage 105/105 rooms, every door-less
  connector path OK, LCZ Class-D spawn to HCZ warhead path OK.

### 2. Runtime bake (`SCPSLBot/Navigation/Runtime/RuntimeNavMeshService`)

- Trigger: same owner as today (`NavigationSystem`, per map generation, after the two-frame
  settle so blocker-deleted clutter is gone). Replaces `LoadMeshes`, `ConnectDoorlessConnectors`,
  `RuntimeRoomMeshBuilder` and the door/elevator cell linking.
- Sources: `NavMeshBuilder.CollectSources` on physics colliders within the facility bounds.
  Excluded layers: Player, Hitbox, Door, InteractableNoPlayerCollision, Ragdoll, Grenade,
  Viewmodel, UI, TransparentFX, Ignore Raycast. Excluded by markup (`ignoreFromBuild`): item
  pickups, elevator chambers, players, admin toys flagged non-blocking.
- Agent settings from `NavigationAgentProfile` (ClassD capsule): radius 0.36, height 1.8,
  climb 0.30, slope 45, voxel 0.09 (radius / 4), tile 128 voxels (about 11.5 m), min region 1 m².
- Build: `BuildNavMeshData` once (0.4 s), published through `NavMesh.AddNavMeshData`. Readiness,
  generation counters, `nav_ready`/`nav_error` and the 1/2/4/8/15 s retry ladder stay as they are.
- Elevators: one `NavMesh.AddLink` per `ElevatorGroup` between the landings in front of its two
  `ElevatorDoor`s, bidirectional, area `Elevator`. The existing `ElevationObstacle` /
  `CallAndWaitForElevator` / `TravelOnElevator` behaviors trigger when the next path corner is a
  link endpoint instead of an edgeless cell segment.
- Doors and keycards: door leaves are not geometry, so doorways are walkable and the existing door
  beliefs and interactions keep opening them. Each keycard door additionally contributes a
  `ModifierBox` source over its frame with an area per permission class (ContainmentLevelOne/Two/
  Three, Armory, Checkpoint, ExitGate, Intercom, AlphaWarhead). Bots plan with an `areaMask` built
  from their inventory, so a bot without a keycard never plans through that door instead of
  failing at it. Locked/unpowered doors are handled at interaction time as today.

### 3. Dynamic updates (`RuntimeNavMeshService.Reconcile`)

- Periodic reconciliation every 5 s: re-collect sources (4 ms) and call
  `UpdateNavMeshDataAsync` with the full list. Unity hashes sources per tile and only rebuilds
  tiles whose inputs changed, off the main thread; unchanged maps cost the hash pass only.
- Immediate triggers: `AdminToyBase.OnAdded/OnRemoved`, `SpawnableRoomConnector.OnAdded/OnRemoved`,
  breakable door/wall destruction events, warmup arena/safezone toy spawns from this suite.
- Bots crossing a tile that is being rebuilt keep their current corners; the navigator replans
  when its stored path status becomes invalid.
- Temporary blockers: `ReportBlockedCrossing` becomes a 45 s carving `NavMeshObstacle` at the
  blocked spot (radius 0.6 m), which is what A* penalties approximate today.

### 4. Navigator (`FpcBotNavigator` rewritten on `NavMesh.CalculatePath`)

- `GetPositionTowards(goal)`: sample start and goal onto the mesh (`NavMesh.SamplePosition`, 2 m),
  `CalculatePath` with the bot's area mask, follow string-pulled corners with the existing
  0.35 m corner arrival and radius inset, replan when the goal moves more than 0.5 m, every
  1 s while the target is a moving player, or when the path status is no longer complete.
- `HasPath` = `PathComplete`; `HasPartialPath` = `PathPartial` (Unity returns the nearest
  reachable corner sequence), which maps directly onto today's order and roam semantics.
- `ProgressStamp` increments on corner advance; the stuck ladder, obstacle slide and
  `BotOrders` telemetry are unchanged.
- `PointsPath`/`PathSegments` come from the corners so `DoorObstacle`, `GlassObstacle` and combat
  door opening keep working; `CellsPath`/`CellPathSegments` are removed.

### 5. Consumers to migrate (found by reference search)

| Consumer | Today | After |
|---|---|---|
| `FpcZoneRoam` | picks random cell centers in foreign/zone rooms | picks random mesh samples inside a room's bounds (`NavMesh.SamplePosition` on a lattice cached per room at bake time) |
| `RoomSightSense.ForeignRoomsCells` | foreign cells through room links | `RoomIdentifier.ConnectedRooms` plus one cached entry point per room pair (sampled at the shared doorway) |
| `ZoneEnterLocation`, `Scp914Location` beliefs | cell lookups | the same cached room entry points |
| `ElevationObstacle` | edgeless cell segments | link endpoints on the path |
| `BotManager.IssueMoveToRoomOrder` | nearest cell center | nearest cached room sample to the bot |
| `BotPopulationController`, `LabApiPlugin`, `BotStatusCommand` | readiness/counters | same fields from the new service (`nav_generated_rooms` etc. replaced by `nav_tiles`, `nav_unreadable_meshes`, `nav_links`, `nav_last_bake_ms`) |
| `BotConnectorSurvey` | cell centers as start/goal | mesh samples 3.5 m either side of the connector |
| `RoomConnectorSpawnpointBasePatches` | opt-in door rewrite | unchanged (still opt-in) |
| `nav` RA commands (22 files), `NavigationMeshEditor`, `NavigationMeshVisuals` | cell editor | retired; replaced by `nav status`, `nav rebuild`, `nav probe <x y z>` and `nav path <target>` |
| `Assets/navmesh.slnmf`, `NavigationMeshDocument`, `Mesh/*` | authored data | removed after one release behind a config flag |

### 6. Configuration and rollout

- `navigation.backend: runtime | authored` (default `runtime` once the gates pass; `authored`
  keeps the current code path for one release as a fallback). Removal of the authored path and
  the editor is a separate follow-up.
- `navigation.reconcile_interval_seconds` (5), `navigation.voxel_size` (0.09), `navigation.
  keycard_area_routing` (true).
- README EN/CN: navigation section rewritten (bake, dynamic updates, keycard routing, elevator
  links, the asset patch requirement and its verify command). `DEPLOYMENT_NOTES.md` and the
  deploy scripts gain the patch step. AGENTS.md snapshot updated.

### 7. Performance budget (measured or bounded)

| Item | Budget | Basis |
|---|---|---|
| Initial bake | ≤ 1 s wall time on map load, main thread ≤ 0.5 s (rest async) | measured 388 ms synchronous for the full facility |
| Reconciliation | ≤ 5 ms main thread per 5 s, tile rebuilds async | measured 4 ms source collection |
| Path query | ≤ 0.1 ms, one per bot per replan (not per tick) | measured 0.02 ms |
| Memory | navmesh data ≤ 10 MB; readable meshes add the CPU copies of about 1,600 collider meshes (to be measured with the patched server) | 14k polygons today |
| Per tick per bot | unchanged from today (one capsule cast, steering) | existing |

### 8. Risks and mitigations

- Asset patch drifts after a game update: manifest records the build; launcher/deploy verify
  before start; the probe scenario is the regression gate.
- A mesh stays unreadable (new prefab, missed file): the bake logs `NAV_UNREADABLE_MESH name=...`
  per distinct mesh, `bot_status` exposes the count, and the physics-probed room fill remains as
  the fallback for rooms without any polygon under their centre.
- Keycard area boxes mis-sized: sized from the door's collider bounds plus the agent radius, and
  the door survey asserts that a ClassD bot never receives a complete path through a keycard door
  while a bot holding the card does.
- Elevator link endpoints off-mesh (chamber floor at a different level at bake time): endpoints
  are sampled on the landing outside the shaft, not inside the chamber.
- Unity builder behaviour in batchmode: proven by the probe; a failed bake falls back to the
  authored backend for that map and logs `NAV_BAKE_FAILED`.

## Work breakdown and gates

1. Asset patcher tool, manifest, verify mode, launcher/deploy integration. Gate: probe on port
   8891 reports `unreadableMeshes=0`, 105/105 coverage, all connector paths, long path OK.
2. `RuntimeNavMeshService` (bake, links, keycard areas, reconciliation, diagnostics) behind the
   `navigation.backend` flag; `bot_status` and `nav` diagnostics. Gate: bake and reconcile budgets
   met on port 8891, logged per phase.
3. Navigator on `CalculatePath`; consumer migration (section 5); survey harness on mesh samples.
   Gate: connector survey 2 seeds 100 %, door survey 100 % including keycard-door routing
   assertions, lifecycle suite green, no `[BotNav] REPLAN_STORM`.
4. Docs, runbook, transcript; default flip to `runtime`; follow-up ticket to delete the authored
   backend and editor.

Estimated effort: 1 day (phase 1), 2 days (phase 2), 2 to 3 days (phase 3), half a day (phase 4).

## Decisions needed before phase 1

- Confirm the server asset patch is acceptable operationally (per-update re-apply, verify step in
  launchers and deploy scripts, `AssetsTools.NET` as a tools-only dependency).
- Confirm scope of keycard-aware routing (area masks per permission) is wanted in the first
  release rather than a follow-up.
- Confirm the authored backend is kept for one release as a fallback rather than deleted now.
