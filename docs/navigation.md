# Navigation

## Choosing a backend

The default `navigation.backend: Runtime` bakes the navmesh from the server's real collision geometry.
A stock dedicated server ships its collider meshes unreadable (and streamed), so Unity's navmesh builder
silently drops most of the facility. Patch the server assets once per game update with the tools-only
patcher, then verify before every start:

```powershell
dotnet build tools\NavMeshAssetPatcher\NavMeshAssetPatcher.csproj -c Release
dotnet tools\NavMeshAssetPatcher\bin\Release\net8.0\NavMeshAssetPatcher.dll patch  --server "C:\Program Files (x86)\Steam\steamapps\common\SCP Secret Laboratory Dedicated Server"
dotnet tools\NavMeshAssetPatcher\bin\Release\net8.0\NavMeshAssetPatcher.dll verify --server "C:\Program Files (x86)\Steam\steamapps\common\SCP Secret Laboratory Dedicated Server"
```

What the patcher does:

- It rewrites `SCPSL_Data/*.assets` so every `Mesh` becomes readable with its vertex data inlined.
  The `.bak` originals stay beside them.
- It writes `SCPSL_Data/navmesh-asset-patch.json`, and `restore` puts the originals back.
- `verify` exits 2 when a game update or Steam file validation has restored the stock files.
  `tools\Start-BotTestServer8888.ps1`, the isolated 8891 drivers and the production deploy script
  refuse to start an unpatched server.
- Client files are never touched.

To skip patching, set `navigation.backend: Authored`. Bots then use the hand-authored navmesh embedded
in `SCPSLBot.dll`, which is what our production server runs.

## How the runtime backend works

The runtime backend works in stages:

1. **Collect.** Two frames after map generation, it collects the live colliders the human capsule
   collides with. Players, door leaves and glass, pickups, ragdolls, elevator chambers, invisible
   doorway blockers, the Surface helicopter, the capybara and non-collidable admin toys are excluded.
2. **Build.** It builds asynchronously with the human capsule: radius 0.36 m plus 5 cm clearance,
   height 1.8 m, 0.3 m step, 45° slope, 9 cm voxels.
3. **Fill gaps.** Rooms whose meshes still cannot be read fall back to a probed floor
   (`NAV_ROOM_PROBED`).
4. **Link.** Each elevator group gets a bidirectional link between its landings. Door-less clutter
   connectors that the bake leaves sealed are probed with the capsule and bridged with a jump link.
   Connectors impassable at every tier log `NAV_CONNECTOR_SEALED` and are routed around.
5. **Keycard areas.** Keycard doors contribute one navmesh area per permission class
   (`navigation.keycard_area_routing`). Locked and unpowered doors are still handled when a bot
   interacts with them.

Keeping the navmesh current:

- The navmesh is reconciled with live geometry every `navigation.reconcile_interval_seconds` (5 s)
  and immediately after admin toys, room connectors or breakable doors change. Unchanged geometry
  costs only a source hash, and changed tiles rebuild asynchronously. While no bot exists the
  periodic re-scan is skipped; the next pass after a bot appears catches up.
- When a bot reports a blocked crossing, that spot is carved out for
  `navigation.blocked_crossing_seconds` (45 s).
- A bake that fails twice on a map falls back to the authored backend for that map
  (`NAV_BAKE_FALLBACK`).
- Failed loads and bakes retry after 1, 2, 4 and 8 s, then every 15 s, until they succeed or the map
  changes. Managed bot spawning resumes once navigation is ready.

How bots move and recover:

- Bots follow string-pulled corners nudged into each turn and slide around whatever collider is
  directly ahead.
- When they stop making progress they escalate: door interaction and sideways nudges after 0.7 s,
  a native jump after 1.5 s, a short back-off, a re-plan after 2.5 s, then the crossing is carved out.
- Roam targets come only from reachable points. An unreachable goal produces a partial path toward
  the closest reachable point.

## Authored backend

`navigation.backend: Authored` uses the embedded `Assets/navmesh.slnmf`, installed on a fresh
configuration. It quarantines invalid live nav data with backup recovery, fills rooms that have no
authored cells from live floor probes, and keeps the `nav` cell editor.

## Custom maps

- `nav rebuild <centerX> <centerY> <centerZ> <sizeX> <sizeY> <sizeZ>` re-bakes runtime navigation
  including one custom region.
- Load the geometry first, then wait for `nav status` to report `ready=True`, `built=True` and
  `active_backend=runtime`.
- Sizes are 1–1024 m on X/Z and 1–256 m on Y, within ±20,000 m on each axis.
- Only stationary toy hierarchies are baked: mark platforms and their parents `IsStatic=true`, and
  non-static parents exclude their children.
- A region lasts until round restart, new map generation, plugin unload or `nav rebuild clear`.
- On runtime navigation, `BotOrders.MoveTo` accepts either a floor point or the actor's native
  standing-root position ([plugin API](plugin-api.md)).

## Logs and status fields

- Server logs record `NAV_BAKE_START`, `NAV_BAKED`, `NAV_RECONCILE_START`, `NAV_RECONCILED`,
  `NAV_UNREADABLE_MESH`, `NAV_LINK`, `NAV_BAKE_FAILED`, `NAV_LOAD_RETRY` and `NAV_LOAD_RECOVERED`.
- Bot movement logs record `[BotNav] STUCK`, `CROSSING_PENALIZED`, `PLAN_FAILED`, `PLAN_PARTIAL` and
  `REPLAN_STORM`.
- `bot_status` exposes `nav_ready`, `nav_error`, `nav_backend` and the bake, link and reconcile counters.
  `nav status`, `nav probe` and `nav path` give the details.

## Limits

- The runtime backend needs the patched dedicated-server assets, and a game update or Steam file
  validation restores the stock files.
- Bots stop at the Intercom doorway because its interior floor does not voxelize.
- `force_standard_door_connectors: true` rewrites map connectors and can conflict with map-layout
  plugins. It is off by default.
