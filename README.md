[English](#english) | [中文](#中文)

# English

## SCPSLBot warmup suite

This repository builds a LabAPI `net48` warmup suite for SCP: Secret Laboratory:

- `SCPSLBot` maintains native RA dummy bots, warmup respawns and hazards, periodic native overflow cleanup, navigation, combat, exact-role and item policy, personalized Server-Specific Settings (SSS), and admin diagnostics.
- `WarmupSafezone` independently owns surface/SCP-914 volumes, protection, blocker/drain rules, and visuals. It never changes native godmode or process-wide spawn-protection settings.
- `StatsBots` records authenticated players' warmup bot score through the existing StatsSystem `player_stats` store, renders an HSM profile, manages unlockable titles, and schedules beginner/community notices.
- `LabAPI_InfiniteAmmo` supplies reload-time reserve ammunition so warmup firefights do not end when finite role ammo is exhausted.
- `ServerKeybinds.Compat` is the pinned, drop-in `ServerKeybinds.dll` API 4 build used for personalized SSS. It is not a second registry.

The old `WarmupPlayerPanel` design is obsolete and must not be deployed with this suite.

## Install

Build or copy these runtime products for the target LabAPI port:

```text
plugins/<port>/SCPSLBot.dll
plugins/<port>/SCPSLBot.Components.dll
plugins/<port>/WarmupSafezone.dll
plugins/<port>/StatsBots.dll
plugins/<port>/LabAPI_InfiniteAmmo_x64.dll
dependencies/<port>/ServerKeybinds.dll       # from ServerKeybinds.Compat
dependencies/<port>/0Harmony.dll
```

The default `runtime` navigation backend bakes the bot navmesh from the server's real collision
geometry at map load. A stock dedicated server build stores its collider meshes unreadable (and
streamed), so Unity's navmesh builder silently drops most of the facility. Patch the server assets
once per game update with the tools-only patcher, then verify before every start:

```powershell
dotnet build tools\NavMeshAssetPatcher\NavMeshAssetPatcher.csproj -c Release
dotnet tools\NavMeshAssetPatcher\bin\Release\net8.0\NavMeshAssetPatcher.dll patch  --server "C:\Program Files (x86)\Steam\steamapps\common\SCP Secret Laboratory Dedicated Server"
dotnet tools\NavMeshAssetPatcher\bin\Release\net8.0\NavMeshAssetPatcher.dll verify --server "C:\Program Files (x86)\Steam\steamapps\common\SCP Secret Laboratory Dedicated Server"
```

The patcher rewrites `SCPSL_Data/*.assets` (every `Mesh` becomes readable with its vertex data
inlined), keeps `.bak` originals beside them, writes `SCPSL_Data/navmesh-asset-patch.json`, and
`restore` puts the originals back. `verify` exits 2 when a game update or Steam file validation
restored the stock files; `tools\Start-BotTestServer8888.ps1`, the isolated 8891 drivers and the
production deploy script refuse to start an unpatched server. Client files are never touched. With
`navigation.backend: authored` the patch is not needed and the hand-authored `navmesh.slnmf` is used.

Declared process-wide companions are `HintServiceMeow.dll` for owned HSM text and the existing StatsSystem plugin/provider for persistence. StatsBots fails honestly as loading/unavailable when StatsSystem is missing; HSM text quietly disables when HSM is absent. Do not deploy upstream and compatibility-fork `ServerKeybinds.dll` files together.

Surface PvE managed CI bots use their exact native CI reinforcement spawn. Real players may remain on Surface as Facility Guard, NTF Private, Sergeant, Captain, or Specialist. Admin-assigned Tutorial is outside all per-player warmup management: it retains native spawning and effects, contributes no arena population, and has no warmup controls. Native RA remains available. Other human roles are evacuated to HCZ/EZ, while SCP roles are evacuated to LCZ, with a clear localized per-player broadcast that flushes stale queued broadcasts and displays immediately.

Navigation (default `navigation.backend: runtime`) is a Unity navmesh baked on the server for every generated map: two frames after generation the live physics colliders the human capsule collides with are collected (players, door leaves and glass, pickups, ragdolls, elevator chambers, invisible doorway blockers, the Surface helicopter, the capybara and non-collidable admin toys are excluded), built asynchronously off the main thread with the human capsule (radius 0.36 m plus 5 cm clearance, height 1.8 m, 0.3 m step, 45 degree slope, 9 cm voxels) and published as one navmesh. Every room, clutter connector, seasonal variant and mid-round geometry change is covered without per-type logic; rooms whose collider meshes still cannot be read fall back to a probed floor and are logged as `NAV_ROOM_PROBED`. Each elevator group receives a bidirectional link between the landings in front of its doors, so LCZ to HCZ and Surface routes plan through the checkpoint and gate elevators and the existing elevator behaviors take over at the link. Door-less clutter connectors the baked surface leaves sealed (huge pipes and similar jumpable clutter) are probed with the capsule and bridged with a jump-tier link through the probed band, which the stuck ladder's native jump crosses; connectors impassable at every tier are logged as `NAV_CONNECTOR_SEALED` and routed around. Keycard doors contribute an area per distinct permission class; a bot plans with an area mask built from its inventory and role, so a Class-D never plans through a Containment Level 2 door while a Scientist holding the card does (`navigation.keycard_area_routing`). Locked and unpowered doors are still handled at interaction time.

The navmesh is reconciled with the live geometry every `navigation.reconcile_interval_seconds` (5 s) and immediately after admin toys, room connectors or breakable doors change; unchanged geometry costs only a source hash, and changed tiles are rebuilt asynchronously. A bot that reports a blocked crossing carves that spot out of the navmesh for `navigation.blocked_crossing_seconds` (45 s) so the next plan prefers another route. Server logs record `NAV_BAKE_START`, `NAV_BAKED`, `NAV_RECONCILE_START`, `NAV_RECONCILED`, `NAV_UNREADABLE_MESH`, `NAV_LINK` and `NAV_BAKE_FAILED`; `bot_status` exposes `nav_backend`, `nav_triangles`, `nav_unreadable_meshes`, `nav_links`, `nav_passage_links`, `nav_sealed_connectors`, `nav_last_bake_ms`, `nav_door_classes`, `nav_fallback_rooms`, `nav_uncovered_rooms`, `nav_island_samples`, `nav_reconciles`, `nav_reconcile_rebuilds` and `nav_obstacles`; `nav status`, `nav probe` and `nav path` give the details.

A bake that fails twice on a map falls back to the authored backend for that map (`NAV_BAKE_FALLBACK`). `navigation.backend: authored` keeps the previous hand-authored cell mesh for one release: SCPSLBot installs its embedded `Assets/navmesh.slnmf` on a fresh configuration, quarantines invalid live nav data with backup recovery, fills rooms without authored cells from live floor probes, links door-less connectors through capsule-probed passages and keeps the `nav` cell editor; `bot_status` then exposes `nav_generated_rooms`, `nav_probed_connectors`, `nav_sealed_connectors` and `nav_penalized_links`.

Failed navigation loads and bakes retry automatically after 1, 2, 4, 8, then every 15 seconds until successful. Managed bot spawning/respawning resumes once navigation is ready. `bot_status` exposes `nav_ready` and `nav_error`; server logs record `NAV_LOAD_RETRY` and `NAV_LOAD_RECOVERED`. Retries stop when the map changes or the plugin is disabled.

Bots follow string-pulled corners nudged into each turn, slide around whatever collider is directly ahead, and escalate when they stop making progress: door interaction and sideways nudges after 0.7 s, a native jump after 1.5 s, a short back-off, a re-plan after 2.5 s, then the attempted crossing is carved out for 45 s so the next plan prefers another route, and after repeated failures the roam target is abandoned. Roam targets are drawn from reachable points only, unreachable goals produce a partial path toward the closest reachable point instead of standing still, and a chased target is re-planned at most once per second. Server logs record `[BotNav] STUCK`, `CROSSING_PENALIZED`, `PLAN_FAILED`, `PLAN_PARTIAL` and `REPLAN_STORM`.

Destroying maintained dummies through native RA automatically replenishes the configured population. Destroyed bot references are cleared safely without blocking other bots or `bot_status`. Population maintenance faults log `BOT_POPULATION_FAULT` at most every 15 seconds during a continuous failure; successful recovery logs `BOT_POPULATION_RECOVERED`.

## Player controls

While Standard warmup is active, SSS provides personalized controls:

- `Respawn as` + `Apply`: the dropdown stages any currently registered native gameplay role, with only `None`, `Spectator`, `Destroyed`, `Overwatch`, `Filmmaker`, `CustomRole`, and `Tutorial` excluded; `Apply` performs the revalidated exact-role change. Configuration allowlists, arena presets, team capacity, current role, and spectator state do not shrink this list. Facility Guard and all four NTF ranks may remain on Surface; Tutorial is outside warmup participation. Any other native role change that begins there—including CI selection and item-driven transformations—evacuates a human to HCZ/EZ or an SCP to LCZ and displays a localized per-player broadcast. Role changes already inside the facility preserve the player's exact position.
- `Request item` + `Grant`: the dropdown stages one item from the complete safe native list (excluding `None` and `DebugRagdollMover`); `Grant` rechecks full-UserId cooldowns and life/round limits before one native grant.
- `Teleport room` + `Apply`: stages one generated room destination from the complete native RA
  named-door registry. Labels are room-first and include the exact door tag when a room has multiple
  doors; Apply re-resolves the tag through the same collision-safe destination calculation as RA
  `doortp` before moving the player. Surface destinations are hidden for every role, and Apply
  rechecks the resolved destination zone so a stale or forged selection cannot bypass that uniform
  restriction. Use the arena control for deliberate Surface entry.
- `Arena preset` + `Apply`: always personal and available during Standard warmup. The dropdown stages Surface PvE, HCZ/EZ PvPvE, or LCZ SCP; `Apply` moves only that player, applies the arena default role, and refreshes that player's menu.

Native spawn protection for real players is retained only on the first playable respawn after a confirmed death. Other role/loadout assignments during Standard warmup clear it. Managed bots keep their separate existing behavior.

The arena dropdown always carries all three choices and marks the current one. Surface and LCZ placement use
the game's native NTF Private and Class-D role spawnpoints. HCZ/EZ entry rotates across distinct generated
rooms using the native named-door registry and the exact collision-safe position resolver used by RA `doortp`,
with SCP-939's native spawn as a fail-safe if no door target is available. It does not invent coordinates or
place actors at room centers. Selecting the already-active arena is a no-op for an alive player and respawns an exact Spectator;
a real arena switch commits only after the exact default role and native destination are verified, and
otherwise rolls back.

During Standard warmup, only the native Gate A/Gate B Surface elevator doors receive a plugin-owned lock.
All other doors retain their native state, and the owned lock is removed when warmup is disabled.

Opening or refreshing SSS performs no action. Role, item, room teleport, and arena dropdowns only stage a server-side selection; their explicit `Apply`/`Grant` button executes it. A visibly retained selection remains staged after successful execution, so pressing the button again revalidates and applies/grants that same value rather than falsely requesting another selection. All button feedback clears that player's stale native broadcast queue and displays immediately. Personalized refreshes are fingerprinted, targeted, debounced by 500 ms, spaced by at least two seconds, and capped at six sends per player per minute.

Clients may submit buttons or dropdown indices from a stale SSS view. Pending role/item/teleport/arena selections use stable IDs tied to PlayerId, full UserId, and action type. Room teleport IDs are exact native door tags and are re-resolved at Apply time, so stale map destinations fail closed. Button presses recheck identity, warmup state, native assignability, limits, and cooldowns. Role, item, teleport, and arena mutations have independent per-user monotonic cooldowns, so one action type never delays another. Rejected callbacks log their exact result code and detail. No player loadout control is registered.

Debug, bot-diagnostic, and navigation-authoring settings are never sent to player SSS; those workflows stay in Remote Admin. StatsBots adds Display toggles and an unlocked-only warmup-title selector. The Player Console fallback is `warmuptitle [list|none|<titleId>]`.

Arena occupancy also owns population: LCZ occupancy maintains at least one SCP bot, HCZ/EZ occupancy maintains at least two human bots (one Foundation and one Chaos before higher configured counts), and Surface retains its classic player-factor population. Empty servers keep the configured baseline population. These population-created bots remain role/arena reconciled. `bot_add` instead creates an independent AI bot: RA role changes persist because the population controller does not adopt it unless an admin explicitly runs `bot_manage`.

A round-owned service scans all participating ready real players every `respawn_scan_interval_seconds`. Only the exact native `Spectator` role is eligible: first-observed spectators use `spectator_respawn_delay_ms`, while a playable-role-to-Spectator death uses `human_respawn_delay_ms` and restores the previous playable role. Spectators route from their server-owned arena membership rather than the non-physical spectator camera position, so CI cannot respawn through the Surface preset. `None`, `Destroyed`, `Overwatch`, alive roles, hosts, and dummies are ignored. Failed native assignments remain scheduled and are retried with explicit server logs.

## Remote Admin commands

| Command | Purpose | Permission |
|---|---|---|
| `bot_status` | Readiness, desired/tracked/owned/independent/live bots, nav generation, faults, runner heartbeat, resources | `FacilityManagement` |
| `bot_add` | Spawn an independent AI bot (maximum 10 independent bots); RA role changes persist | `PlayersManagement` |
| `bot_manage <player ID>` | Adopt an independent bot into a maintained population slot; at full population it replaces one managed bot | `PlayersManagement` |
| `bot_unmanage <player ID>` | Release a maintained bot without despawning it; the controller creates a replacement | `PlayersManagement` |
| `bot_warmup [none|standard]` | Query or change persisted mode | Query: none; change: `PlayersManagement` |
| `bot_difficulty [easy|normal|hard|hardest]` | Query or change combat difficulty | Query: none; change: `PlayersManagement` |
| `bot_path`, `botspike ...` | Pathing and native movement diagnostics | Mutation: `PlayersManagement`; spike status: `GameplayData` |
| `botspike survey <clutter|doors|all|keycard> [both|forward]`, `botspike survey_status`, `botspike survey_stop` | Walk the spike bot natively across every door-less connector (or plain door) of the generated map and log per-case `[BotSurvey]` verdicts; `keycard` asserts keycard-aware routing with path queries only | Survey: `PlayersManagement`; status: `GameplayData` |
| `nav status` | Active/configured backend, readiness, bake and reconcile diagnostics | `GameplayData` |
| `nav rebuild` | Re-bake (runtime) or re-load (authored) navigation for the current map | `ServerConfigs` |
| `nav probe [x y z|RoomName]` | Is a point on the navigation surface, nearest surface point, navmesh area / door class | `GameplayData` |
| `nav path <from> <to> [perms <hex>|all]` | Runtime path query between points or room anchors with a permission mask | `GameplayData` |
| `nav edit|load|save|vertex ...` | Authored-backend cell editor (kept for one release) | Read: `GameplayData`; mutation: `ServerConfigs` |
| `statsbots status|grant|revoke <fullUserId> ...` | Inspect or administer warmup titles | configurable `statsbots.manage` |

StatsBots admin commands require an exact full authenticated UserId; ambiguous nicknames and `ID_Dummy` are rejected.

## Configuration

The primary SCPSLBot defaults are:

```yaml
language: ""
default_warmup_mode: Standard
warmup_mode: Standard
human_respawn_delay_ms: 1200
bot_respawn_delay_ms: 2500
spectator_respawn_delay_ms: 5000
respawn_scan_interval_seconds: 0.5
warmup_bot_count: 3
warmup_bot_role: ChaosRifleman
warmup_human_role: NtfPrivate
default_warmup_arena: SurfacePve
surface_pve_bot_factor: 1.2
surface_pve_max_bot_count: 6
heavy_entrance_pvpve_bot_count: 2
light_containment_scp_bot_count: 1
disable_warhead_in_warmup: true
disable_lcz_decontamination_in_warmup: true
disable_disarming_in_warmup: true
disable_scp207_health_drain_in_warmup: true
enable_overflow_cleanup: true
cleanup_item_threshold: 80
cleanup_check_interval_seconds: 10
force_standard_door_connectors: false
navigation:
  backend: Runtime            # Runtime (baked at map load, needs the patched server assets) or Authored
  reconcile_interval_seconds: 5
  voxel_size: 0.09
  keycard_area_routing: true
  blocked_crossing_seconds: 45
panel:
  role_change_cooldown_seconds: 6
  item_grant_cooldown_seconds: 1
  teleport_cooldown_seconds: 1
  arena_switch_cooldown_seconds: 5
```

`enable_overflow_cleanup` checks loose pickups on the configured interval. When the count grows by more than `cleanup_item_threshold` above the current round baseline, SCPSLBot invokes the game's native item, corpse, blood, and bullet-hole cleanup commands, then captures a new baseline. `controls` contains item policy, native spawn-anchor overrides, the three physical arena presets, cooldown groups, allowed item roles/zones, and limits; its legacy role allowlists no longer gate role selection. `panel` contains SSS presentation plus legacy loadout data retained only for config compatibility; no loadout control is shown. High-impact items share a 60-second cooldown, remain limited to one per life, and use a practical per-round ceiling of 999 for long-running warmup rounds; debug entries are filtered. Review these gameplay defaults before production deployment.

Every product exposes `language`, where `"en"` forces English, `"cn"` forces Chinese, and `""` uses a client-language seam when available with Chinese fallback. See [WarmupSafezone/README.md](WarmupSafezone/README.md) and [StatsBots/README.md](StatsBots/README.md) for their full configuration.

Recommended native warmup settings remain:

```yaml
auto_warhead_start_minutes: 0
dms_enabled: false
stamina_balance_use: 0
spawn_protect_enabled: true
```

## Build and verify

```powershell
$env:SL_REFERENCES = 'C:\Program Files (x86)\Steam\steamapps\common\SCP Secret Laboratory Dedicated Server\SCPSL_Data\Managed'
dotnet build SCPSLBotAddon.sln -c Release -p:Platform=x64 -p:DeployToLocalServer=false
dotnet test SCPSLBot.PolicyTests\SCPSLBot.PolicyTests.csproj -c Release
node ..\.tests\lint-scenarios.js
```

Build deployment is opt-in; the production solution excludes the in-server test and reload plugins. Test evidence and remaining live/manual gates are recorded in each product's `tests` directory. The navigation gates boot the isolated port 8891 against the patched server assets: `python tests/playtest/tools/check_connector_survey.py --scenario scpslbot-runtime-navmesh-gate` (readable meshes, coverage, links, door classes, budgets, long paths), `--scenario scpslbot-keycard-routing-survey` (denied/granted paths per keycard door), `--rounds 2` (native walking across every door-less connector on two map seeds) and `--scenario scpslbot-door-survey`; every driver verifies the asset patch first. See [tests/playtest/README.md](tests/playtest/README.md).

The dedicated local bot-testing deployment is port `8888`. It carries the runtime suite, HSM,
PlaytestHarness, and the bot/safezone scenario assemblies without DummyRoleFiller.
Start it with `tools\Start-BotTestServer8888.ps1`; the launcher supplies the lane-specific
`SCPSL_OPS_STATE_ROOT` required by StatsSystem persistence and restores the compatibility fork under
`dependencies/8888`. Local deployments keep `dependencies/global` empty so one lane cannot replace
another lane's SSS ABI.

## Known conflicts and limits

- Do not deploy `WarmupPlayerPanel`, legacy `ScpslPluginStarter.dll`, or both upstream/fork ServerKeybinds assemblies.
- `force_standard_door_connectors: true` rewrites map connectors and can conflict with map-layout plugins; it is off by default.
- The runtime navigation backend needs the patched dedicated-server assets (see Install); a game update or Steam file validation restores the stock files and the launchers/deploy script then refuse to start until `NavMeshAssetPatcher patch` is re-applied. Elevator travel by bots and keycard routing in live multiplayer remain manual verification items. The Intercom room interior is not part of the baked surface (its floor does not voxelize), so bots stop at the Intercom doorway.
- This suite never owns native badges/player names. StatsBots titles stay in its HSM profile.
- HSM uses stable owned tags and never clears the shared vanilla hint/broadcast channels.
- Final multiplayer/manual checks remain appropriate for client-language presentation, real weapon/SCP ability behavior, and long 10-bot performance soak.

# 中文

## SCPSLBot 热身套件

本仓库构建一套面向 SCP: Secret Laboratory、目标为 LabAPI `net48` 的热身服务器组件：

- `SCPSLBot` 负责原生 RA 假人机器人、热身复活与危险项、定期原生溢出清理、导航、战斗、服务器权威的角色/物品策略、个性化服务器专属设置（SSS）以及管理员诊断。
- `WarmupSafezone` 独立负责地表与 SCP-914 安全区、保护、堵门惩罚/扣血和可视化；不会修改原生无敌状态或进程级出生保护设置。
- `StatsBots` 通过现有 StatsSystem 的 `player_stats` 存储记录已认证玩家的热身机器人积分，并提供 HSM 资料卡、可解锁称号和新手/社区通知。
- `LabAPI_InfiniteAmmo` 在换弹时补充备用弹药，避免热身交火因角色初始弹药耗尽而永久停止。
- `ServerKeybinds.Compat` 是固定上游提交、可直接替换的 `ServerKeybinds.dll` API 4；它不是第二套注册表。

旧的 `WarmupPlayerPanel` 方案已经废弃，不能与本套件一起部署。

## 安装

为目标 LabAPI 端口构建或复制以下运行时文件：

```text
plugins/<端口>/SCPSLBot.dll
plugins/<端口>/SCPSLBot.Components.dll
plugins/<端口>/WarmupSafezone.dll
plugins/<端口>/StatsBots.dll
plugins/<端口>/LabAPI_InfiniteAmmo_x64.dll
dependencies/<端口>/ServerKeybinds.dll       # 来自 ServerKeybinds.Compat
dependencies/<端口>/0Harmony.dll
```

默认的 `runtime` 导航后端会在地图加载时根据服务器真实碰撞几何烘焙机器人导航网格。原版专用服务器的碰撞网格是不可读（且流式存储）的，Unity 导航网格构建器会静默丢弃设施的大部分区域。每次游戏更新后，用工具目录中的补丁程序打一次补丁，并在每次启动前校验：

```powershell
dotnet build tools\NavMeshAssetPatcher\NavMeshAssetPatcher.csproj -c Release
dotnet tools\NavMeshAssetPatcher\bin\Release\net8.0\NavMeshAssetPatcher.dll patch  --server "C:\Program Files (x86)\Steam\steamapps\common\SCP Secret Laboratory Dedicated Server"
dotnet tools\NavMeshAssetPatcher\bin\Release\net8.0\NavMeshAssetPatcher.dll verify --server "C:\Program Files (x86)\Steam\steamapps\common\SCP Secret Laboratory Dedicated Server"
```

补丁程序会重写 `SCPSL_Data/*.assets`（所有 `Mesh` 变为可读并内联顶点数据），在旁边保留 `.bak` 原件，写入 `SCPSL_Data/navmesh-asset-patch.json`；`restore` 可恢复原件。游戏更新或 Steam 文件校验恢复原版文件后，`verify` 返回 2；`tools\Start-BotTestServer8888.ps1`、隔离端口 8891 的驱动脚本和生产部署脚本都会拒绝启动未打补丁的服务器。客户端文件不会被修改。使用 `navigation.backend: authored` 时无需补丁，将使用手工网格 `navmesh.slnmf`。

进程级依赖包括用于独占 HSM 文本的 `HintServiceMeow.dll`，以及用于持久化的现有 StatsSystem 插件/提供器。StatsSystem 缺失时，StatsBots 会明确显示“加载中/不可用”；HSM 缺失时只会安静停用文字层。严禁同时部署上游版和兼容分支版 `ServerKeybinds.dll`。

地表 PvE 的托管 CI 机器人使用其精确 CI 角色的原生增援出生点。真实玩家可作为设施警卫、九尾狐列兵、中士、指挥官或收容专家留在地表；管理员分配的 Tutorial 角色完全不参与个人热身管理：保留原生出生与效果，不计入竞技场人数，也不显示热身控制；原生 RA 仍然可用；其他人类角色会被疏散至重收/入口，SCP 会被疏散至轻收。个人本地化广播会先清空该玩家的旧广播队列并立即显示。

导航（默认 `navigation.backend: runtime`）是每张生成地图在服务器上烘焙的 Unity 导航网格：地图生成两帧后收集人类胶囊体会碰撞的实时物理碰撞体（排除玩家、门扇与门玻璃、掉落物、尸体、电梯轿厢、门口的隐形阻挡体、地表直升机、水豚以及不可碰撞的管理员道具），用人类胶囊体（半径 0.36 米加 5 厘米余量、高 1.8 米、台阶 0.3 米、坡度 45 度、体素 9 厘米）在主线程外异步构建并发布为一张导航网格。所有房间、杂物连接处、季节变体和回合中的几何变化都被覆盖，无需按类型特殊处理；碰撞网格仍无法读取的房间会回退为探测地面，并记录 `NAV_ROOM_PROBED`。每组电梯在两侧门前的落脚点之间建立双向链接，因此轻收到重收以及地表路线会经由检查点和大门电梯规划，现有电梯行为在链接处接管。烘焙表面仍封死的无门杂物连接处（大型管道等可跳越杂物）会用胶囊体探测，并在探测出的通道带上建立跳跃级链接，由卡住恢复阶梯的原生跳跃穿过；任何层级都无法通过的连接处记录为 `NAV_CONNECTOR_SEALED` 并绕行。门禁门按不同权限组合各占一个导航区域；机器人用背包和角色生成的区域掩码规划路径，因此 D 级人员绝不会规划穿过二级收容门，而持卡的科学家可以（`navigation.keycard_area_routing`）。上锁和断电的门仍在交互时处理。

导航网格每 `navigation.reconcile_interval_seconds`（5 秒）与实时几何对账，管理员道具、房间连接处或可破坏门变化时立即对账；几何未变时只计算一次来源哈希，变化的分块异步重建。机器人报告通道受阻后，该处会从导航网格中挖除 `navigation.blocked_crossing_seconds`（45 秒），使下一次规划优先选择其他路线。服务器日志记录 `NAV_BAKE_START`、`NAV_BAKED`、`NAV_RECONCILE_START`、`NAV_RECONCILED`、`NAV_UNREADABLE_MESH`、`NAV_LINK` 和 `NAV_BAKE_FAILED`；`bot_status` 显示 `nav_backend`、`nav_triangles`、`nav_unreadable_meshes`、`nav_links`、`nav_passage_links`、`nav_sealed_connectors`、`nav_last_bake_ms`、`nav_door_classes`、`nav_fallback_rooms`、`nav_uncovered_rooms`、`nav_island_samples`、`nav_reconciles`、`nav_reconcile_rebuilds` 和 `nav_obstacles`；`nav status`、`nav probe` 和 `nav path` 提供细节。

同一张地图烘焙失败两次后会回退到手工后端（`NAV_BAKE_FALLBACK`）。`navigation.backend: authored` 保留上一版本的手工网格一个版本周期：首次启动时安装内嵌的 `Assets/navmesh.slnmf`，损坏的实时导航文件会被隔离并尝试备份恢复，没有手工网格的房间根据实时地面探测填充，无门连接处通过胶囊体探测的通道连接，并保留 `nav` 网格编辑器；此时 `bot_status` 显示 `nav_generated_rooms`、`nav_probed_connectors`、`nav_sealed_connectors` 和 `nav_penalized_links`。

导航加载或烘焙失败后会在 1、2、4、8 秒后自动重试，此后每 15 秒重试一次，直到成功。导航就绪后，托管机器人的生成和复活会自动恢复。`bot_status` 显示 `nav_ready` 和 `nav_error`；服务器日志记录 `NAV_LOAD_RETRY` 和 `NAV_LOAD_RECOVERED`。地图切换或插件停用时会取消重试。

机器人沿着向转弯内侧微调的拉直路径拐点前进，沿着正前方的任何碰撞体滑行绕开，并在停止前进时逐级升级：0.7 秒后尝试开门并左右侧移，1.5 秒后原生跳跃，短暂后退，2.5 秒后重新规划，之后把正在尝试的通道挖除 45 秒，使下一次规划优先选择其他路线；多次失败后放弃当前漫游目标。漫游目标只从可达点中抽取，无法到达的目标会生成通往最近可达点的部分路径而不是原地不动，追击移动目标时每秒最多重新规划一次。服务器日志记录 `[BotNav] STUCK`、`CROSSING_PENALIZED`、`PLAN_FAILED`、`PLAN_PARTIAL` 和 `REPLAN_STORM`。

通过原生 RA 销毁托管机器人后，系统会自动补足配置人数。已销毁机器人的引用会被安全清理，不会阻塞其他机器人的生成或 `bot_status`。人数维护持续出错时，每 15 秒最多记录一次 `BOT_POPULATION_FAULT`；恢复成功后记录 `BOT_POPULATION_RECOVERED`。

## 玩家控制

Standard 热身模式启用时，SSS 会显示个性化控件：

- `复活为` + `应用`：下拉框暂存当前注册的任一原生游戏角色，仅排除 `None`、`Spectator`、`Destroyed`、`Overwatch`、`Filmmaker`、`CustomRole` 和 `Tutorial`；点击应用后才重新校验并切换到精确角色。配置允许列表、竞技场预设、阵营容量、当前角色及观察者状态都不会缩减此列表。设施警卫和全部四种九尾狐军衔可留在地表，管理员分配的 Tutorial 角色完全不参与热身管理；其他从地表开始的原生角色变化（包括选择混沌角色和物品触发的变身）会将人类送入重收/入口、将 SCP 送入轻收，并显示本地化个人广播。玩家已在设施内时保持原地切换。
- `请求物品` + `发放`：下拉框从完整安全原生物品列表（排除 `None` 和 `DebugRagdollMover`）暂存一个物品；点击发放后才按完整 UserId 重新校验冷却、每条生命和每回合次数，并执行一次原生添加。
- `传送房间` + `应用`：下拉框从完整的原生 RA 具名门注册表中暂存一个已生成房间目标。标签优先显示房间，并附带精确门标签以区分同一房间内的多扇门；点击应用时会重新解析门标签，并使用 RA `doortp` 相同的碰撞安全位置算法移动玩家。所有角色都不会看到地表目标；应用时还会重新检查解析后的目标区域，因此过期或伪造的选择无法绕过这项统一限制。需要主动进入地表时请使用竞技场控制。
- `竞技场预设` + `应用`：Standard 热身中始终是个人选项。下拉框暂存地表 PvE、重收/入口 PvPvE 或轻收 SCP；点击应用后才移动该玩家、应用区域默认角色并刷新该玩家的菜单。

真实玩家仅在确认死亡后的第一次可玩角色复活时保留原生出生保护；Standard 热身期间的其他角色/配装切换会清除该效果。托管机器人继续使用原有的独立规则。

竞技场下拉框始终包含全部三个选项并标出当前区域。地表和轻收分别使用九尾狐列兵与 D 级的原生出生点；重收/入口会遍历原生具名门列表，使用 RA `doortp` 相同的碰撞安全位置算法，在生成地图的不同房间间轮换；若没有可用门目标才回退至 SCP-939 原生出生点。系统不会自定义坐标或把角色放到房间中心。存活玩家重复选择当前区域不会重置角色、生命或装备；精确处于 `Spectator` 的玩家会在当前区域复活。真正的区域切换只有在默认角色和原生目标都验证成功后才提交，否则回滚。

Standard 热身期间，仅原生 Gate A/Gate B 地表电梯门会加上插件自有锁；其他门保持原生状态，关闭热身时会移除该锁。

打开或刷新 SSS 不会执行操作。角色、物品、房间传送与竞技场下拉框只在服务器暂存选择，必须点击对应的 `应用`/`发放` 才会执行。成功后若界面仍显示该选项，服务器也会继续保留它；再次点击按钮会重新校验并执行同一选项，不会错误要求重新选择。所有按钮反馈都会清空该玩家的旧原生广播队列并立即显示。个性化刷新包含指纹去重、定向路由、500 毫秒防抖、至少两秒间隔，以及每名玩家每分钟最多六次的限制。

客户端可能提交旧版 SSS 页面中的按钮或下拉索引。待处理的角色、物品、传送与竞技场选择使用 PlayerId、完整 UserId、操作类型和稳定 ID 绑定。房间传送 ID 是精确的原生门标签，并会在点击应用时重新解析，因此旧地图目标会安全失败。点击按钮时会重新检查身份、热身状态、原生可分配性、次数和冷却。角色、物品、传送与竞技场分别使用独立的每玩家单调时钟冷却，任一操作不会延迟其他类型。被拒绝的回调会记录精确结果代码和详情。玩家 SSS 不再注册装备预设控件。

玩家 SSS 永远不会收到调试、机器人诊断或导航编辑选项；这些功能只保留在 Remote Admin。StatsBots 在 Display 分类添加显示开关和“仅已解锁称号”选择器。玩家控制台备用命令为 `warmuptitle [list|none|<称号ID>]`。

机器人数量也跟随竞技场人数：轻收有人时至少维护 1 个 SCP 机器人；重收/入口有人时至少维护 2 个人类机器人（基础配置先各含一个基金会与混沌阵营）；地表继续使用经典人数倍率。空服仍保持配置的基础机器人数量。由人口控制器创建的机器人会持续校正角色和竞技场。`bot_add` 则创建独立 AI 机器人；RA 修改的角色会保持不变，除非管理员明确执行 `bot_manage` 将其纳入人口控制。

一个回合级全局服务每隔 `respawn_scan_interval_seconds` 扫描所有已就绪的真实玩家。只有角色精确为原生 `Spectator` 才符合条件：首次观察到的观察者使用 `spectator_respawn_delay_ms`，从可玩角色死亡进入观察者则使用 `human_respawn_delay_ms` 并恢复此前可玩角色。观察者按服务器保存的竞技场归属路由，而不是使用没有实体身体的观察镜头坐标，因此 CI 无法通过地表预设复活。`None`、`Destroyed`、`Overwatch`、存活角色、主机和假人全部忽略。原生分配失败时会保留计划、自动重试并写入明确日志。

## Remote Admin 命令

| 命令 | 用途 | 权限 |
|---|---|---|
| `bot_status` | 就绪状态、目标/跟踪/托管/独立/存活机器人、导航代次、故障、AI 心跳和资源 | `FacilityManagement` |
| `bot_add` | 创建独立 AI 机器人（最多 10 个独立机器人）；RA 修改的角色会保持 | `PlayersManagement` |
| `bot_manage <玩家ID>` | 将独立机器人纳入维护人口槽位；人口已满时替换一个托管机器人 | `PlayersManagement` |
| `bot_unmanage <玩家ID>` | 解除人口托管但不删除机器人；控制器会创建替补 | `PlayersManagement` |
| `bot_warmup [none|standard]` | 查询或修改持久化模式 | 查询无需权限；修改需 `PlayersManagement` |
| `bot_difficulty [easy|normal|hard|hardest]` | 查询或修改战斗难度 | 查询无需权限；修改需 `PlayersManagement` |
| `bot_path`、`botspike ...` | 路径和原生移动诊断 | 修改需 `PlayersManagement`；状态需 `GameplayData` |
| `botspike survey <clutter|doors|all|keycard> [both|forward]`、`botspike survey_status`、`botspike survey_stop` | 让测试机器人原生行走穿过生成地图的每个无门连接处（或普通门），并逐例记录 `[BotSurvey]` 结论；`keycard` 仅用路径查询断言门禁路径规划 | 巡检需 `PlayersManagement`；状态需 `GameplayData` |
| `nav status` | 当前/配置的后端、就绪状态、烘焙与对账诊断 | `GameplayData` |
| `nav rebuild` | 对当前地图重新烘焙（runtime）或重新加载（authored）导航 | `ServerConfigs` |
| `nav probe [x y z|房间名]` | 某点是否在导航表面、最近表面点、导航区域/门禁类别 | `GameplayData` |
| `nav path <起点> <终点> [perms <十六进制>|all]` | 按权限掩码在两点或房间锚点之间查询路径 | `GameplayData` |
| `nav edit|load|save|vertex ...` | 手工后端网格编辑器（保留一个版本周期） | 读取需 `GameplayData`；修改需 `ServerConfigs` |
| `statsbots status|grant|revoke <完整UserId> ...` | 查询或管理热身称号 | 可配置的 `statsbots.manage` |

StatsBots 管理命令必须使用完整已认证 UserId；模糊昵称和 `ID_Dummy` 会被拒绝。

## 配置

SCPSLBot 主要默认值：

```yaml
language: ""
default_warmup_mode: Standard
warmup_mode: Standard
human_respawn_delay_ms: 1200
bot_respawn_delay_ms: 2500
spectator_respawn_delay_ms: 5000
respawn_scan_interval_seconds: 0.5
warmup_bot_count: 3
warmup_bot_role: ChaosRifleman
warmup_human_role: NtfPrivate
default_warmup_arena: SurfacePve
surface_pve_bot_factor: 1.2
surface_pve_max_bot_count: 6
heavy_entrance_pvpve_bot_count: 2
light_containment_scp_bot_count: 1
disable_warhead_in_warmup: true
disable_lcz_decontamination_in_warmup: true
disable_disarming_in_warmup: true
disable_scp207_health_drain_in_warmup: true
enable_overflow_cleanup: true
cleanup_item_threshold: 80
cleanup_check_interval_seconds: 10
force_standard_door_connectors: false
navigation:
  backend: Runtime            # Runtime（地图加载时烘焙，需要已打补丁的服务器资源）或 Authored
  reconcile_interval_seconds: 5
  voxel_size: 0.09
  keycard_area_routing: true
  blocked_crossing_seconds: 45
panel:
  role_change_cooldown_seconds: 6
  item_grant_cooldown_seconds: 1
  teleport_cooldown_seconds: 1
  arena_switch_cooldown_seconds: 5
```

`enable_overflow_cleanup` 会按配置间隔检查散落物品。当数量相对本回合基线增加超过 `cleanup_item_threshold` 时，SCPSLBot 会调用游戏原生的物品、尸体、血迹和弹孔清理命令，然后重新记录基线。`controls` 包含物品策略、原生出生锚点覆盖、三个实体竞技场预设、共享冷却组、允许物品角色/区域和次数限制；其中旧版角色允许列表不再限制角色选择。`panel` 包含 SSS 显示设置，以及仅为配置兼容而保留的旧版装备预设数据；玩家界面不会显示装备预设控件。高影响物品共享 60 秒冷却、每条生命限 1 个，并为长期热身回合使用每回合 999 个的实用上限；调试项会被过滤。正式服部署前请检查这些玩法默认值。

每个产品都提供 `language`：`"en"` 强制英文，`"cn"` 强制中文，`""` 在服务器 API 可用时匹配客户端，否则回退中文。完整配置请查看 [WarmupSafezone/README.md](WarmupSafezone/README.md) 和 [StatsBots/README.md](StatsBots/README.md)。

建议保留以下原生热身配置：

```yaml
auto_warhead_start_minutes: 0
dms_enabled: false
stamina_balance_use: 0
spawn_protect_enabled: true
```

## 构建与验证

```powershell
$env:SL_REFERENCES = 'C:\Program Files (x86)\Steam\steamapps\common\SCP Secret Laboratory Dedicated Server\SCPSL_Data\Managed'
dotnet build SCPSLBotAddon.sln -c Release -p:Platform=x64 -p:DeployToLocalServer=false
dotnet test SCPSLBot.PolicyTests\SCPSLBot.PolicyTests.csproj -c Release
node ..\.tests\lint-scenarios.js
```

构建默认不会部署；生产解决方案不包含服务器内测试插件和重载插件。测试证据及仍需现场/手工验证的项目记录在各产品的 `tests` 目录中。导航验收会在隔离端口 8891 上针对已打补丁的服务器资源启动：`python tests/playtest/tools/check_connector_survey.py --scenario scpslbot-runtime-navmesh-gate`（可读网格、覆盖、链接、门禁类别、预算、长路径）、`--scenario scpslbot-keycard-routing-survey`（每扇门禁门的拒绝/允许路径）、`--rounds 2`（两个地图种子上原生行走穿过每个无门连接处）以及 `--scenario scpslbot-door-survey`；每个驱动脚本都会先校验资源补丁。详见 [tests/playtest/README.md](tests/playtest/README.md)。

本机专用机器人测试端口为 `8888`，部署运行时套件、HSM、PlaytestHarness 及机器人/安全区场景程序集，不安装 DummyRoleFiller。
请通过 `tools\Start-BotTestServer8888.ps1` 启动；该脚本会提供 StatsSystem 持久化所需的端口独立 `SCPSL_OPS_STATE_ROOT`。

## 已知冲突与限制

- 不要部署 `WarmupPlayerPanel`、旧版 `ScpslPluginStarter.dll`，也不要同时部署两份 ServerKeybinds。
- `force_standard_door_connectors: true` 会改写地图连接点，可能与地图布局插件冲突；默认关闭。
- 运行时导航后端需要已打补丁的专用服务器资源（见安装）；游戏更新或 Steam 文件校验会恢复原版文件，启动脚本和部署脚本会拒绝启动，直到重新执行 `NavMeshAssetPatcher patch`。机器人乘坐电梯和真实多人环境下的门禁路径规划仍需手工验证。对讲机房间内部不在烘焙表面内（其地面无法体素化），机器人会停在对讲机房门口。
- 本套件不会改写原生徽章或玩家名；StatsBots 称号只显示在其 HSM 资料卡中。
- HSM 使用稳定、独占的标签，绝不清空共享原版提示或广播队列。
- 最终仍建议进行多人/手工检查：客户端语言显示、真实枪械/SCP 能力，以及 10 机器人长时间性能压测。
