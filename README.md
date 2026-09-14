[English](#english) | [中文](#中文)

# English

**Stable release:** [Production 2026-09-08](https://github.com/Michaelihc/scpsl-warmup-sandbox/releases/tag/production-2026-09-08). The release DLL is the verified build deployed to production. Current source development is in testing.

This repository publishes source baseline `f036fb06d9b16adaa708363de2fd6bc7e5ed322f`. The stable binary includes subsequent production fixes; the tag identifies this publication, rather than the exact source used to build that binary. Use the release asset for the stable production version.

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

Declared process-wide companions are `HintServiceMeow.dll` for owned HSM text and the existing StatsSystem plugin/provider for persistence. StatsBots fails honestly as loading/unavailable when StatsSystem is missing; HSM text quietly disables when HSM is absent. Do not deploy upstream and compatibility-fork `ServerKeybinds.dll` files together.

Surface PvE managed CI bots use their exact native CI reinforcement spawn. Real players may remain on Surface as Facility Guard, NTF Private, Sergeant, Captain, or Specialist. Other human roles are evacuated to HCZ/EZ, while SCP roles are evacuated to LCZ, with a clear localized per-player broadcast that flushes stale queued broadcasts and displays immediately.

SCPSLBot installs its embedded `Assets/navmesh.slnmf` on a fresh configuration. Invalid live nav data is quarantined, backup recovery is attempted, and saves use replace/backup publication.

## Player controls

While Standard warmup is active, SSS provides personalized controls:

- `Respawn as` + `Apply`: the dropdown stages any currently registered native gameplay role, with only `None`, `Spectator`, `Destroyed`, `Overwatch`, `Filmmaker`, `CustomRole`, and `Tutorial` excluded; `Apply` performs the revalidated exact-role change. Configuration allowlists, arena presets, team capacity, current role, and spectator state do not shrink this list. Facility Guard and all four NTF ranks may remain on Surface. Any other native role change that begins there—including CI selection and item-driven transformations—evacuates a human to HCZ/EZ or an SCP to LCZ and displays a localized per-player broadcast. Role changes already inside the facility preserve the player's exact position.
- `Request item` + `Grant`: the dropdown stages one item from the complete safe native list (excluding `None` and `DebugRagdollMover`); `Grant` rechecks full-UserId cooldowns and life/round limits before one native grant.
- `Teleport to`: current authenticated real-player destinations in the requester's same physical arena only.
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

Opening or refreshing SSS performs no action. Role, item, and arena dropdowns only stage a server-side selection; their explicit `Apply`/`Grant` button executes it. A visibly retained selection remains staged after successful execution, so pressing the button again revalidates and applies/grants that same value rather than falsely requesting another selection. All button feedback clears that player's stale native broadcast queue and displays immediately. Teleport remains an immediate deliberate dropdown action. Personalized refreshes are fingerprinted, targeted, debounced by 500 ms, spaced by at least two seconds, and capped at six sends per player per minute.

Clients may submit buttons or dropdown indices from a stale SSS view. Pending role/item/arena selections use stable IDs tied to PlayerId, full UserId, and action type; departed teleport targets become unavailable tombstones instead of shifting another player into the old index. Button presses recheck identity, warmup state, native assignability, physical arena, limits, and cooldowns, and all mutation callbacks share a per-user monotonic one-second minimum interval. Rejected callbacks log their exact result code and detail. No player loadout control is registered.

Debug, bot-diagnostic, and navigation-authoring settings are never sent to player SSS; those workflows stay in Remote Admin. StatsBots adds Display toggles and an unlocked-only warmup-title selector. The Player Console fallback is `warmuptitle [list|none|<titleId>]`.

Arena occupancy also owns population: LCZ occupancy maintains at least one SCP bot, HCZ/EZ occupancy maintains at least two human bots (one Foundation and one Chaos before higher configured counts), and Surface retains its classic player-factor population. Empty servers keep the configured baseline population.

A round-owned service scans all ready real players every `respawn_scan_interval_seconds`. Only the exact native `Spectator` role is eligible: first-observed spectators use `spectator_respawn_delay_ms`, while a playable-role-to-Spectator death uses `human_respawn_delay_ms` and restores the previous playable role. Spectators route from their server-owned arena membership rather than the non-physical spectator camera position, so CI cannot respawn through the Surface preset. `None`, `Destroyed`, `Overwatch`, alive roles, hosts, and dummies are ignored. Failed native assignments remain scheduled and are retried with explicit server logs.

## Remote Admin commands

| Command | Purpose | Permission |
|---|---|---|
| `bot_status` | Readiness, desired/tracked/live bots, nav generation, faults, runner heartbeat, resources | `FacilityManagement` |
| `bot_add` | Increase the maintained Standard population, capped at 10 | `PlayersManagement` |
| `bot_warmup [none|standard]` | Query or change persisted mode | Query: none; change: `PlayersManagement` |
| `bot_difficulty [easy|normal|hard|hardest]` | Query or change combat difficulty | Query: none; change: `PlayersManagement` |
| `bot_path`, `botspike ...` | Pathing and native movement diagnostics | Mutation: `PlayersManagement`; spike status: `GameplayData` |
| `nav ...` | Navigation diagnostics/edit/load/save | Read: `GameplayData`; mutation: `ServerConfigs` |
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
warmup_arena_switch_cooldown_seconds: 5
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
```

`enable_overflow_cleanup` checks loose pickups on the configured interval. When the count grows by more than `cleanup_item_threshold` above the current round baseline, SCPSLBot invokes the game's native item, corpse, blood, and bullet-hole cleanup commands, then captures a new baseline. `controls` contains item policy, native spawn-anchor overrides, the three physical arena presets, cooldown groups, allowed item roles/zones, and limits; its legacy role allowlists no longer gate role selection. `panel` contains SSS presentation plus legacy loadout data retained only for config compatibility; no loadout control is shown. High-impact items share a cooldown; debug entries are filtered. Review these gameplay defaults before production deployment.

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

Build deployment is opt-in; the production solution excludes the in-server test and reload plugins. Test evidence and remaining live/manual gates are recorded in [implementation-notes.md](implementation-notes.md) and each product's `tests` directory.

The dedicated local bot-testing deployment is port `8888`. It carries the runtime suite, HSM,
PlaytestHarness, and the bot/safezone scenario assemblies without DummyRoleFiller.
Start it with `tools\Start-BotTestServer8888.ps1`; the launcher supplies the lane-specific
`SCPSL_OPS_STATE_ROOT` required by StatsSystem persistence and restores the compatibility fork under
`dependencies/8888`. Local deployments keep `dependencies/global` empty so one lane cannot replace
another lane's SSS ABI.

## Known conflicts and limits

- Do not deploy `WarmupPlayerPanel`, legacy `ScpslPluginStarter.dll`, or both upstream/fork ServerKeybinds assemblies.
- `force_standard_door_connectors: true` rewrites map connectors and can conflict with map-layout plugins; it is off by default.
- This suite never owns native badges/player names. StatsBots titles stay in its HSM profile.
- HSM uses stable owned tags and never clears the shared vanilla hint/broadcast channels.
- Final multiplayer/manual checks remain appropriate for client-language presentation, real weapon/SCP ability behavior, and long 10-bot performance soak.

# 中文

**稳定版本：**[生产版本 2026-09-08](https://github.com/Michaelihc/scpsl-warmup-sandbox/releases/tag/production-2026-09-08)。发布的 DLL 已核对，正是生产环境部署的版本。当前源码开发仍在测试中。

本仓库发布源码基线 `f036fb06d9b16adaa708363de2fd6bc7e5ed322f`。稳定二进制包含后续生产修复；标签标识本次发布记录，并非该二进制构建时的完整源码快照。需要稳定生产版本时，请使用发布附件。

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

进程级依赖包括用于独占 HSM 文本的 `HintServiceMeow.dll`，以及用于持久化的现有 StatsSystem 插件/提供器。StatsSystem 缺失时，StatsBots 会明确显示“加载中/不可用”；HSM 缺失时只会安静停用文字层。严禁同时部署上游版和兼容分支版 `ServerKeybinds.dll`。

地表 PvE 的托管 CI 机器人使用其精确 CI 角色的原生增援出生点。真实玩家可作为设施警卫、九尾狐列兵、中士、指挥官或收容专家留在地表；其他人类角色会被疏散至重收/入口，SCP 会被疏散至轻收。个人本地化广播会先清空该玩家的旧广播队列并立即显示。

首次启动时，SCPSLBot 会安装内嵌的 `Assets/navmesh.slnmf`。损坏的实时导航文件会被隔离，然后尝试备份恢复；保存时使用替换/备份事务。

## 玩家控制

Standard 热身模式启用时，SSS 会显示个性化控件：

- `复活为` + `应用`：下拉框暂存当前注册的任一原生游戏角色，仅排除 `None`、`Spectator`、`Destroyed`、`Overwatch`、`Filmmaker`、`CustomRole` 和 `Tutorial`；点击应用后才重新校验并切换到精确角色。配置允许列表、竞技场预设、阵营容量、当前角色及观察者状态都不会缩减此列表。设施警卫和全部四种九尾狐军衔可留在地表；其他从地表开始的原生角色变化（包括选择混沌角色和物品触发的变身）会将人类送入重收/入口、将 SCP 送入轻收，并显示本地化个人广播。玩家已在设施内时保持原地切换。
- `请求物品` + `发放`：下拉框从完整安全原生物品列表（排除 `None` 和 `DebugRagdollMover`）暂存一个物品；点击发放后才按完整 UserId 重新校验冷却、每条生命和每回合次数，并执行一次原生添加。
- `传送到`：只显示与请求者处于同一实体竞技场的当前已认证真实玩家目标。
- `竞技场预设` + `应用`：Standard 热身中始终是个人选项。下拉框暂存地表 PvE、重收/入口 PvPvE 或轻收 SCP；点击应用后才移动该玩家、应用区域默认角色并刷新该玩家的菜单。

真实玩家仅在确认死亡后的第一次可玩角色复活时保留原生出生保护；Standard 热身期间的其他角色/配装切换会清除该效果。托管机器人继续使用原有的独立规则。

竞技场下拉框始终包含全部三个选项并标出当前区域。地表和轻收分别使用九尾狐列兵与 D 级的原生出生点；重收/入口会遍历原生具名门列表，使用 RA `doortp` 相同的碰撞安全位置算法，在生成地图的不同房间间轮换；若没有可用门目标才回退至 SCP-939 原生出生点。系统不会自定义坐标或把角色放到房间中心。存活玩家重复选择当前区域不会重置角色、生命或装备；精确处于 `Spectator` 的玩家会在当前区域复活。真正的区域切换只有在默认角色和原生目标都验证成功后才提交，否则回滚。

Standard 热身期间，仅原生 Gate A/Gate B 地表电梯门会加上插件自有锁；其他门保持原生状态，关闭热身时会移除该锁。

打开或刷新 SSS 不会执行操作。角色、物品与竞技场下拉框只在服务器暂存选择，必须点击对应的 `应用`/`发放` 才会执行。成功后若界面仍显示该选项，服务器也会继续保留它；再次点击按钮会重新校验并执行同一选项，不会错误要求重新选择。所有按钮反馈都会清空该玩家的旧原生广播队列并立即显示；传送仍会在用户主动改变下拉选项后立即执行。个性化刷新包含指纹去重、定向路由、500 毫秒防抖、至少两秒间隔，以及每名玩家每分钟最多六次的限制。

客户端可能提交旧版 SSS 页面中的按钮或下拉索引。待处理的角色、物品与竞技场选择使用 PlayerId、完整 UserId、操作类型和稳定 ID 绑定；离线的传送目标会变成“不可用”占位，不会把旧索引映射到另一名玩家。点击按钮时会重新检查身份、热身状态、原生可分配性、实体区域、次数和冷却；所有修改型回调还共用每名玩家至少一秒的单调时钟限速。被拒绝的回调会记录精确结果代码和详情。玩家 SSS 不再注册装备预设控件。

玩家 SSS 永远不会收到调试、机器人诊断或导航编辑选项；这些功能只保留在 Remote Admin。StatsBots 在 Display 分类添加显示开关和“仅已解锁称号”选择器。玩家控制台备用命令为 `warmuptitle [list|none|<称号ID>]`。

机器人数量也跟随竞技场人数：轻收有人时至少维护 1 个 SCP 机器人；重收/入口有人时至少维护 2 个人类机器人（基础配置先各含一个基金会与混沌阵营）；地表继续使用经典人数倍率。空服仍保持配置的基础机器人数量。

一个回合级全局服务每隔 `respawn_scan_interval_seconds` 扫描所有已就绪的真实玩家。只有角色精确为原生 `Spectator` 才符合条件：首次观察到的观察者使用 `spectator_respawn_delay_ms`，从可玩角色死亡进入观察者则使用 `human_respawn_delay_ms` 并恢复此前可玩角色。观察者按服务器保存的竞技场归属路由，而不是使用没有实体身体的观察镜头坐标，因此 CI 无法通过地表预设复活。`None`、`Destroyed`、`Overwatch`、存活角色、主机和假人全部忽略。原生分配失败时会保留计划、自动重试并写入明确日志。

## Remote Admin 命令

| 命令 | 用途 | 权限 |
|---|---|---|
| `bot_status` | 就绪状态、目标/跟踪/存活机器人、导航代次、故障、AI 心跳和资源 | `FacilityManagement` |
| `bot_add` | 增加 Standard 模式维护数量，上限 10 | `PlayersManagement` |
| `bot_warmup [none|standard]` | 查询或修改持久化模式 | 查询无需权限；修改需 `PlayersManagement` |
| `bot_difficulty [easy|normal|hard|hardest]` | 查询或修改战斗难度 | 查询无需权限；修改需 `PlayersManagement` |
| `bot_path`、`botspike ...` | 路径和原生移动诊断 | 修改需 `PlayersManagement`；状态需 `GameplayData` |
| `nav ...` | 导航诊断、编辑、加载和保存 | 读取需 `GameplayData`；修改需 `ServerConfigs` |
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
warmup_arena_switch_cooldown_seconds: 5
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
```

`enable_overflow_cleanup` 会按配置间隔检查散落物品。当数量相对本回合基线增加超过 `cleanup_item_threshold` 时，SCPSLBot 会调用游戏原生的物品、尸体、血迹和弹孔清理命令，然后重新记录基线。`controls` 包含物品策略、原生出生锚点覆盖、三个实体竞技场预设、共享冷却组、允许物品角色/区域和次数限制；其中旧版角色允许列表不再限制角色选择。`panel` 包含 SSS 显示设置，以及仅为配置兼容而保留的旧版装备预设数据；玩家界面不会显示装备预设控件。高影响物品共享冷却，调试项会被过滤。正式服部署前请检查这些玩法默认值。

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

构建默认不会部署；生产解决方案不包含服务器内测试插件和重载插件。测试证据及仍需现场/手工验证的项目记录在 [implementation-notes.md](implementation-notes.md) 和各产品的 `tests` 目录中。

本机专用机器人测试端口为 `8888`，部署运行时套件、HSM、PlaytestHarness 及机器人/安全区场景程序集，不安装 DummyRoleFiller。
请通过 `tools\Start-BotTestServer8888.ps1` 启动；该脚本会提供 StatsSystem 持久化所需的端口独立 `SCPSL_OPS_STATE_ROOT`。

## 已知冲突与限制

- 不要部署 `WarmupPlayerPanel`、旧版 `ScpslPluginStarter.dll`，也不要同时部署两份 ServerKeybinds。
- `force_standard_door_connectors: true` 会改写地图连接点，可能与地图布局插件冲突；默认关闭。
- 本套件不会改写原生徽章或玩家名；StatsBots 称号只显示在其 HSM 资料卡中。
- HSM 使用稳定、独占的标签，绝不清空共享原版提示或广播队列。
- 最终仍建议进行多人/手工检查：客户端语言显示、真实枪械/SCP 能力，以及 10 机器人长时间性能压测。
