[English](#english) | [中文](#中文)

# English

## WarmupSafezone

`WarmupSafezone` 1.0.0 provides two independent safezone policies for the SCPSLBot warmup server:

- The configured Surface escape safezone uses `surface_escape_safezone_axis`, its threshold, and `surface_escape_safezone_min_x`, restoring the original large Surface zone and boundary wall. Current LabAPI `Map.EscapeZones` remain protected as an additional fallback.
- The SCP-914 safezone uses SCP-914's native calculated room bounds, verified again through native room resolution.

The plugin never enables native godmode and never writes the process-wide `SpawnProtected` settings. Protection is decided synchronously in LabAPI damage/action events. After a player leaves either safezone, a private monotonic per-player expiry temporarily blocks both incoming and outgoing damage.

## Protection and action policy

| Case | Result |
|---|---|
| Outside attacker → outside victim | Allowed |
| Protected attacker → any victim | Blocked |
| Any attacker/environment → protected victim | Blocked |
| Self-damage while protected | Blocked |
| Plugin-owned surface drain/blocker damage | Allowed through the protection layer |

`Protected` means currently inside either safezone or covered by exit protection. The current position is resolved inside each damage or action event; a 100 ms recovery tick catches ordinary movement transitions when no action occurs.
An attempted role change does not erase an existing exit-protection lease; cancellation or later role substitution therefore cannot open a damage window.

The explicit action matrix is:

- Firearms and dry-fire: denied while protected.
- Thrown items, grenades, and projectiles: denied while protected.
- SCP-244 use is denied both at native use start and again at the completion boundary; the 250 ms dangerous-item recovery pass also stops an active use carried across a safezone boundary.
- Micro H.I.D. and Jailbird charge/fire: denied and an active charge is stopped.
- SCP-049 attacks, SCP-096 target/charge, SCP-106 player teleport, SCP-173 snap/tantrum, SCP-3114 strangle, and SCP-939 attack/lunge/cloud: denied when the actor or explicit target is protected.
- Other SCP damage and indirect hazards: the final damage event still enforces both endpoints. Utility/movement abilities without a harmful action are allowed.
- Flash, blindness, deafness, and concussion effects are suppressed inside a safezone. SCP-096 is calmed inside a safezone.

## Surface-only policies

Surface health drain is evaluated only against the configured/native Surface safezone union; SCP-914 occupants never enter that policy. The optional anti-blocker volume is the configured band immediately outside the restored threshold. Its progression:

- accumulates only during active time in the blocker shell;
- pauses when the player leaves the shell;
- resumes on re-entry until the player stays outside continuously for the configured reset period;
- applies exactly `surface_escape_blocker_initial_drain_hp_per_second` for the first punishable second after grace, then starts multiplying.

Configured Surface drain is a health drain, not a renewable-shield drain. It still enters the native damage/event pipeline, but any portion absorbed by AHP or Hume Shield is made up against health so SCP Hume regeneration cannot hold the blocker indefinitely.

The transparent Surface boundary wall follows the configured axis/threshold/minimum-X geometry. Native escape bounds extend gameplay protection as a fallback but do not replace or resize that wall. All boundary and sign toys are non-collidable, and missing toys are recreated by the resilient lifecycle loop.

## Player text and language

Player notices use the repository's stable-tag HintDisplayProvider pattern through HintServiceMeow. If HintServiceMeow is unavailable, notices are disabled while gameplay protection continues. WarmupSafezone contains no direct hint calls.

`language` values:

- `"en"`: force English.
- `"cn"`: force Chinese.
- `""`: match the client when a supported server API becomes available; LabAPI 1.1.6/1.1.7 exposes no synchronized client-language property, so the current fallback is Chinese.

The SCP-914 door panel is one shared network object and therefore uses the configured/fallback server language for everyone. Its two non-collidable backing faces render at 10× their original scale while the text retains its normal scale; this visual-only backing scale does not alter the native SCP-914 safezone bounds.

## Configuration

Key settings (existing configuration names remain load-compatible):

```yaml
language: ""
enabled: true
scp914_safezone_enabled: true
scp914_safezone_panel_text_english: "SAFE ZONE\nDAMAGE BLOCKED"
scp914_safezone_panel_text_chinese: "安全区\n禁止造成或受到伤害"
safezone_visuals_enabled: true

surface_escape_safezone_health_drain_enabled: false
surface_escape_safezone_health_drain_percent_per_second: 0.5
surface_escape_blocker_enabled: true
surface_escape_blocker_depth: 9
surface_escape_blocker_grace_seconds: 3
surface_escape_blocker_reset_seconds: 60
surface_escape_blocker_initial_drain_hp_per_second: 1
surface_escape_blocker_drain_multiplier_per_second: 2
surface_escape_blocker_max_drain_percent_per_second: 35

safezone_exit_spawn_protection_enabled: true
safezone_exit_spawn_protection_duration_ms: 10000

hint_display:
  group_name: "warmupsafezone.hints"
  tag_prefix: "warmupsafezone."
  default_x: -800
  notice_y: 150
  action_prompt_y: 150
  blocker_prompt_y: 235
  surface_drain_prompt_y: 325
  ghost_tail_columns: 49
  prompt_text_size: 22
  line_height: 12
```

The axis/threshold/minimum-X fields control the restored Surface gameplay volume and visible boundary. Native `Map.EscapeZones` remain an additional protected fallback. `surface_escape_blocker_depth` controls the approach band outside the configured threshold.

The default hint layout keeps HSM's centre alignment and middle anchor while using explicit X/Y values and a transparent 49-column tail on every row. This places visible text in a compact top-left lane without changing HSM's centred text-area model. The localized fixtures, ten 1920x1080 collision-gated renders, and exact measured bounds are under `tests/ui`.

There are no player or RA commands.

## Build and test

```powershell
dotnet build WarmupSafezone\WarmupSafezone.csproj -c Release -p:Platform=x64 -p:SL_REFERENCES="C:\Program Files (x86)\Steam\steamapps\common\SCP Secret Laboratory Dedicated Server\SCPSL_Data\Managed"
dotnet run --project WarmupSafezone\tests\WarmupSafezone.LogicTests.csproj -c Release
dotnet build WarmupSafezone\tests\WarmupSafezone.Playtests.csproj -c Release
node ..\.tests\lint-scenarios.js
```

The HSM fixture gate uses `../.tests/UI/render-image.js` with `--viewport 1920x1080 --fail-on-collision`. See `tests/ui/20260830-results.md` for the complete EN/CN matrix and output names.

With `PlaytestHarness.dll`, `WarmupSafezone.Playtests.dll`, and the production plugin loaded on an isolated server:

```text
ptest reload
ptest run warmup-safezone-914 standard
ptest run warmup-safezone-actions standard
```

The playtests check the four-way SCP-914 damage policy, immediate-egress protection that survives a cancelled native role request, unchanged native spawn-protection statics, untouched externally owned godmode, non-collidable 10× panels, native surface-bound alignment, a downward ground raycast at every escape-zone centre, SCP-class blocker drain through Hume Shield, a real SCP-173 utility ability, and native SCP-244 start cancellation with the exact item retained. A real client is still required to verify sign readability and input feel.

## Known conflicts

- Plugins that mutate `Map.EscapeZones` change the additional native fallback portion of this plugin's Surface policy. They do not change the configured boundary wall.
- Map replacements that do not provide a valid SCP-914 native room bound/gate disable the relevant room membership/panel until those objects exist.
- Toy cleanup plugins may delete signs or boundaries; the lifecycle service recreates the exact missing set.
- HintServiceMeow API incompatibility disables player notices; gameplay protection continues.

## Source evidence

- LabAPI escape-zone wrapper: `../../.references/LabAPI/LabApi/Features/Wrappers/Facility/Map.cs` (`DefaultEscapeZone`, `EscapeZones`).
- Native escape membership: `../../.references/Decompiled/DedicatedServer/Assembly-CSharp/Escape.cs` (`Escape.CanEscape`).
- Native room bounds: `../../.references/Decompiled/DedicatedServer/Assembly-CSharp/MapGeneration/RoomIdentifier.cs` (`WorldspaceBounds`).
- Native room resolution: `../../.references/Decompiled/DedicatedServer/Assembly-CSharp/MapGeneration/RoomUtils.cs`.
- LabAPI damage cancellation: `../../.references/LabAPI/LabApi/Events/Arguments/PlayerEvents/PlayerHurtingEventArgs.cs`.

# 中文

## WarmupSafezone

`WarmupSafezone` 1.0.0 为 SCPSLBot 热身服务器提供两套互相独立的安全区规则：

- 地表逃生安全区重新使用 `surface_escape_safezone_axis`、阈值与 `surface_escape_safezone_min_x`，恢复原来的大范围地表安全区和边界墙；LabAPI `Map.EscapeZones` 当前登记的边界仍作为额外保护回退。
- SCP-914 安全区使用 SCP-914 原生计算的房间边界，并再次通过原生房间解析确认玩家确实位于该房间。

插件不会开启原生无敌，也不会写入进程级 `SpawnProtected` 设置。伤害与操作事件会同步判断保护状态。玩家离开任一安全区后，插件使用私有的、基于单调时钟的玩家到期时间，暂时同时阻止其造成和受到伤害。

## 保护与操作规则

| 情况 | 结果 |
|---|---|
| 区外攻击者 → 区外目标 | 允许 |
| 受保护攻击者 → 任意目标 | 阻止 |
| 任意攻击者或环境 → 受保护目标 | 阻止 |
| 受保护期间自伤 | 阻止 |
| 插件自身的地表流血或堵门惩罚伤害 | 允许穿过保护层 |

“受保护”指当前位于任一安全区内，或仍处于离区保护时间。每次伤害或操作事件都会立即读取当前位置；另外使用 100 毫秒恢复检查处理没有发生操作的普通移动。
尝试切换角色不会清除已有的离区保护，因此取消请求或后续角色替换都不能制造伤害空窗。

明确的操作矩阵如下：

- 枪械射击与空仓击发：受保护时禁止。
- 投掷物、手雷与抛射物：受保护时禁止。
- SCP-244 在原生使用开始和完成边界都会被阻止；250 毫秒危险物品恢复检查还会停止跨越安全区边界后仍处于使用状态的 SCP-244。
- Micro H.I.D. 与 Jailbird 蓄力/攻击：禁止并停止当前蓄力。
- SCP-049 攻击、SCP-096 添加目标/冲锋、SCP-106 传送玩家、SCP-173 扭颈/污秽、SCP-3114 勒杀、SCP-939 攻击/扑击/迷雾：行为方或明确目标受保护时禁止。
- 其他 SCP 伤害与间接危险仍由最终伤害事件检查双方；没有伤害用途的移动/功能能力允许使用。
- 安全区内会清除闪光、致盲、耳聋和脑震荡效果，并让 SCP-096 平静。

## 仅地表生效的规则

地表生命流失只检查配置区域与原生区域的并集，不会误伤 SCP-914 内的玩家。防堵区域是恢复后的配置阈值外侧带状区域。惩罚进度：

- 只累计实际位于防堵壳层内的时间；
- 离开后暂停；
- 在重置前重新进入会继续原进度；
- 只有连续离开达到配置时长才重置；
- 宽限期后的第一次惩罚严格等于初始每秒伤害，之后才开始倍增。

配置的地表流失针对生命值，而不是可再生护盾。它仍先经过原生伤害和事件流程，但被 AHP 或 Hume Shield 吸收的部分会补扣生命值，避免 SCP 依靠 Hume 护盾回复无限堵门。

透明地表边界墙使用配置的轴、阈值和最小 X 几何；原生逃生边界只额外扩展玩法保护，不会替换或缩放这面墙。所有边界与标牌均无碰撞，生命周期检查会重新生成缺失的玩具。

## 玩家文字与语言

玩家提示使用仓库统一的稳定标签 HintDisplayProvider 模式，并通过 HintServiceMeow 显示。若 HintServiceMeow 不可用，提示会停用，但安全区玩法继续工作。WarmupSafezone 中没有直接发送提示的调用。

`language`：

- `"en"`：强制英文。
- `"cn"`：强制中文。
- `""`：在服务端 API 可用时匹配客户端；LabAPI 1.1.6/1.1.7 暂无同步的客户端语言属性，因此当前回退中文。

SCP-914 门牌是所有客户端共享的网络物体，只能统一使用配置语言或回退语言。两面的无碰撞背景按原尺寸的 10 倍渲染，文字保持正常尺寸；背景调整只影响视觉，不会改变 SCP-914 原生安全区边界。

## 配置

关键设置见上方英文 YAML。轴、坐标阈值和最小 X 字段重新控制地表安全区玩法与可视边界；原生 `Map.EscapeZones` 继续作为额外保护回退。`surface_escape_blocker_depth` 控制配置阈值外侧的防堵带宽度。

默认提示保持 HSM 居中对齐和中部锚点，通过明确的 X/Y 坐标，并在每一行末尾加入 49 列透明占位，把可见文字放入紧凑的左上安全区域，同时不破坏 HSM 的居中文本区模型。中英文测试夹具、十张 1920x1080 碰撞检查截图及精确测量结果位于 `tests/ui`。

本插件没有玩家命令或 RA 命令。

## 构建与测试

构建、逻辑测试、场景编译及场景 lint 命令见英文部分。隔离测试服执行：

```text
ptest reload
ptest run warmup-safezone-914 standard
ptest run warmup-safezone-actions standard
```

场景检查 SCP-914 四向伤害矩阵、取消原生角色请求后仍有效的立即离区保护、原生出生保护静态值未改变、外部无敌未被改写、10 倍门牌无碰撞、地表视觉与原生边界一致、每个逃生区中心的向下地面射线、SCP 穿过 Hume 护盾的防堵生命流失、SCP-914 内真实的 SCP-173 工具型能力，以及 SCP-244 原生使用开始被取消且原物品仍保留。标牌可读性和真实输入手感仍需真实客户端确认。

## 已知冲突

- 其他插件修改 `Map.EscapeZones` 时，只会改变本插件额外的原生回退保护范围，不会改变配置的边界墙。
- 若地图替换没有提供有效的 SCP-914 原生房间边界或大门，对应安全区或门牌会暂停，直到对象可用。
- 玩具清理插件可能删除边界或标牌；生命周期服务会恢复缺失集合。
- HintServiceMeow API 不兼容时，玩家提示会停用，但安全区玩法继续工作。
