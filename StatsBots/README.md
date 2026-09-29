# StatsBots

[English](#english) · [中文](#中文)

## English

StatsBots is the player-facing companion for SCPSLBot warmup. It records managed-bot kills/deaths in the existing StatsSystem `player_stats` store, renders a localized three-band HSM card, supplies an unlocked-title selector through ServerKeybinds, and schedules short native onboarding/community broadcasts without clearing anyone else's queue.

### Install

Build `StatsBots.csproj` for `Release|x64` and install `StatsBots.dll` as a LabAPI plugin. The HsmAdapter project is a source build reference (`-p:HsmAdapterProject=<path>` from another checkout); the other integrations are late-bound. The intended server package contains:

- `SCPSLBot.dll` exposing `SCPSLBot.Api.ManagedBotIdentity`;
- `StatsSystem.dll` 2.2-compatible, using its default `player_stats` store;
- `HintServiceMeow.dll` for the HUD (missing HSM fails quiet and never falls back to the shared native hint channel);
- one matching `HsmAdapter.dll` for hint ownership and expiry;
- the mainline `ServerKeybinds.dll` built from the metarepo `ServerKeybinds` project (provides `AddDropdownForPlayer`); exactly one copy per port.

StatsSystem must have its lane/state environment configured normally. StatsBots never opens or copies StatsSystem's files.

### Scoring and identity

- Real authenticated player → managed SCPSLBot: `Warmup.BotKills +1`, `Warmup.Score + score_per_bot_kill` (default 10), and current streak +1; best streak advances when appropriate.
- Managed SCPSLBot → real authenticated player: `Warmup.BotDeaths +1` and current streak resets.
- Bot → bot, real → real, world, self, and same-faction/team kills: no warmup score.

Only a full authenticated UserId (`account@provider`) can be persisted. `ID_Dummy` is always rejected. A dummy counts as a bot only when the public SCPSLBot identity contract says SCPSLBot currently owns it; `Player.IsDummy` alone is never attribution. Duplicate callbacks sharing the same native damage-handler identity are suppressed for the configured 1.5-second window.

Reserved default-store keys are:

`Warmup.BotKills`, `Warmup.BotDeaths`, `Warmup.Score`, `Warmup.CurrentStreak`, `Warmup.BestStreak`, `Warmup.TagUnlocked.<id>`, and `Warmup.SelectedTagCode`.

### Skill rating

Alongside the score tier, StatsBots keeps a combat skill rating that only samples engagements with managed bots:

- Inputs are firearm accuracy (hitting shots / shots), kill share (kills / (kills + deaths)) and firearm kills per combat minute, weighted by `skill_rating` config. Particle Disruptor, MicroHID, grenades and other non-firearm kills are not rating kills.
- Combat time accrues only between bot interactions (a firearm hit on a bot, being hurt by a bot, a kill or a death) at most `combat_window_seconds` apart. A missed shot counts only when a bot interaction happens within that window, so idle time, safe-zone time and stray shots never lower the rating. Any player-versus-player damage suspends sampling for one window; SCP roles are not sampled.
- Totals are stored as `Warmup.Skill.*` counters that decay with a configurable half-life (default 7 days). Rating = 1000 × skill × confidence, where confidence grows with the decayed number of kills plus deaths, so returning players rebuild quickly and long-inactive players fade to unranked.
- Until a player reaches the decayed placement minimums (default 150 shots and 20 kills plus deaths) the HUD shows placement progress; afterwards it shows the configured rank and rating. `statsbots status` reports every component, the decayed sample and unsaved shots.

Samples are persisted every `flush_seconds` (default 30), on leave and on disable. Rank thresholds are initial estimates; calibrate them from `statsbots status` data after a week of play.

### HUD, titles, and settings

The HSM profile owns only these stable entries. Every entry still uses HSM `Center` alignment, but X `-800` plus a transparent tail places visible ink in the left safe lane without the banned HSM left-alignment mode:

- `statsbots.warmup.flash`: about Y 735, size 26;
- `statsbots.warmup.hero`: about Y 780, size 23, at most two abbreviated lines;
- `statsbots.warmup.footer`: about Y 850, size 22.

Provider loading or failure is rendered as `LOADING`/`UNAVAILABLE`; StatsBots never substitutes an unverified zero. Configured title/tier labels are rich-text escaped. StatsBots does not change display names, native badges, groups, PlayerBadge, or AdditionalNameTags.

The Server-Specific Settings `Display` block uses base `1131000`. It contains one personalized regular/non-scrollable unlocked-title dropdown and native two-button settings for Warmup HUD, Warmup title, combat notices, beginner tips, and the QQ community line. ServerKeybinds suppresses acquisition callbacks, validates the exact sent model/generation, and rate-limits per-player refresh. StatsBots rechecks unlock state on execution. If ServerKeybinds is absent, use `warmuptitle list` and `warmuptitle <id|none>` in Player Console.

Player Console title responses are localized per player. RA command names and responses intentionally remain stable English for scripts and staff runbooks.

### Native broadcasts

- Community: 12 seconds on join, round start, and every 300 seconds.
- Beginner setup: once per session, due 20 seconds after join, only while verified `TotalPlayTime + current session < 3600 seconds`.
- Beginner tips: one 8-second localized tip every 120 seconds while still below the verified boundary; rotation has no immediate repeat.

Every send uses `shouldClearPrevious: false`. One bounded per-player state machine holds only due flags/timestamps, moves lower-priority work to the next free slot, and clears its own state on leave/disable. Unknown playtime never triggers beginner messaging. The exact default English setup text and QQ number/copy are the audit values; matching Chinese copy and the tip catalog are configurable/localized.

### Admin commands

RA commands require `statsbots.manage` by default and accept exact full UserIds, including offline cached/hydratable records:

```text
statsbots status <fullUserId>
statsbots grant <fullUserId> <titleId>
statsbots revoke <fullUserId> <titleId>
```

Grant stores `1`; revoke stores `-1`, so revoke remains effective even if score otherwise unlocks the title and is idempotent. A missing key (`0`) uses the configured minimum-score rule. Removed selected codes are reported as `removed-code:<n>` and are not silently mapped to another title.

An uncached offline MySQL record is hydrated on a worker thread so an RA lookup cannot stall the Unity game thread. The first command reports `loading`; retry it after hydration completes. No blank record is created while that read is pending.

### Important defaults and limitations

The audit specified behavior but not score amount, tier thresholds, or title catalog. Defaults are therefore explicit config, not hidden constants: 10 points per bot kill; tiers at 0/100/500/1500/5000/15000/40000; titles at 0/100/500/1500/5000/15000/40000. Edit them in the generated StatsBots config.

LabAPI currently exposes no authenticated client-language value. With `language: ""`, StatsBots attempts a future public `ClientLanguage`/`Language` wrapper property and otherwise falls back to Chinese; `cn` and `en` force one language. The static two-button SSS captions use the forced language or Chinese fallback for the same reason.

The abbreviated left-lane profile passes the registered EN/CN inventory, announcements/stat-bar, spectator, waiting, and spawn-flash collision checks. Static renders are recorded in `tests/ui/20260830-results.md`; live deployment still needs to confirm TextMeshPro rendering.

StatsSystem exposes no public hydration-complete/health receipt and mutation calls return `void`. StatsBots keeps early scoring events in a queue bounded at 64 per user and 512 globally. After the five-second grace it completes a worker-thread hydrate check before initializing a genuinely missing record, but cannot turn the upstream contract into a per-write durability guarantee. Provider errors remain visible; deploy database/file snapshots and monitor StatsSystem logs.

## 中文

StatsBots 是 SCPSLBot 热身模式的玩家端配套插件。它复用 StatsSystem 默认的 `player_stats` 存储记录托管机器人战绩，通过 HSM 显示三段式中文/英文状态卡，通过 ServerKeybinds 提供“仅显示已解锁称号”的下拉菜单，并以不会清除其他公告的方式发送新手与 QQ 社区提示。

### 安装

以 `Release|x64` 构建 `StatsBots.csproj`，将 `StatsBots.dll` 安装为 LabAPI 插件。StatsBots 使用延迟绑定，因此编译时不硬依赖其他插件；完整运行环境应安装：带公开 `ManagedBotIdentity` 契约的 `SCPSLBot.dll`、兼容 StatsSystem 2.2 的 `StatsSystem.dll`、用于 HUD 的 `HintServiceMeow.dll`，以及由 metarepo 的 `ServerKeybinds` 项目构建的主线 `ServerKeybinds.dll`。每个端口只安装一份。

### 计分与身份

- 真实已认证玩家击杀 SCPSLBot 托管机器人：机器人击杀 +1、积分默认 +10、当前连杀 +1，并更新最佳连杀。
- 托管机器人击杀真实已认证玩家：机器人死亡 +1，当前连杀归零。
- 机器人互杀、真人互杀、世界伤害、自杀及同阵营击杀：不记录热身积分。

只有完整 `账号@平台` UserId 可以写入；`ID_Dummy` 永远被拒绝。机器人身份只接受 SCPSLBot 公开所有权契约，绝不只凭 `IsDummy` 判断。所有持久键均使用 `Warmup.*` 命名空间。

### 技术评分

在积分段位之外，StatsBots 还会计算只针对托管机器人交战的技术评分：

- 评分由枪械命中率（命中射击 / 射击）、击杀占比（击杀 /（击杀 + 死亡））和每分钟交战枪械击杀数组成，权重在 `skill_rating` 配置中调整。粒子干扰器、MicroHID、手雷等非枪械击杀不计入评分。
- 只有相邻两次机器人交互（枪械命中机器人、被机器人伤害、击杀或死亡）间隔不超过 `combat_window_seconds` 时才累计交战时间；未命中的射击只有在该窗口内发生机器人交互时才计入。因此挂机、待在安全区和乱射都不会拉低评分。任何玩家之间的伤害会暂停采样一个窗口；SCP 角色不采样。
- 数据以 `Warmup.Skill.*` 计数器保存，并按可配置的半衰期（默认 7 天）衰减。评分 = 1000 × 技术 × 置信度，置信度随衰减后的击杀加死亡次数增长：回归玩家很快恢复，长期不玩的玩家会逐渐变为未定级。
- 达到衰减后的定级门槛（默认 150 次射击和 20 次击杀加死亡）前，HUD 显示定级进度；之后显示配置的段位与评分。`statsbots status` 会列出所有分项、衰减样本和未保存的射击数。

采样每 `flush_seconds`（默认 30 秒）、玩家离开及插件停用时保存。段位阈值为初始估计，请在运行一周后根据 `statsbots status` 数据校准。

### HUD、称号与设置

HSM 三段分别使用稳定 ID `flash`（约 Y 735/26号）、`hero`（约 Y 780/23号，最多两行）和 `footer`（约 Y 850/22号）。它们都保持 HSM 居中对齐，通过 X `-800` 与每行透明尾部把可见文字放进左侧安全区，不使用被禁用的 HSM 左对齐。数据加载或失败时显示“加载中/不可用”，不会伪造零值。StatsBots 不修改玩家名称、原生徽章、权限组、PlayerBadge 或 AdditionalNameTags；HSM 缺失时也不会回退到共享原生提示通道。

服务器专属设置使用 `1131000` 区块：一个个性化常规称号下拉菜单，以及 HUD、称号、战斗提示、新手提示、QQ 社区通知五个原生双按钮开关。兼容分支缺失时，可在玩家控制台使用 `warmuptitle list` 与 `warmuptitle <称号ID|none>`。

玩家控制台称号回复会按玩家语言本地化；为了便于脚本和管理员手册保持稳定，RA 命令名与回复有意固定为英文。

### 公告节奏

QQ 社区公告在加入、回合开始及每 300 秒发送 12 秒；已验证总游玩时间加本次在线时间低于 1 小时的玩家，会在加入约 20 秒后收到一次 8 秒设置说明，并每 120 秒收到一条 8 秒新手提示。到达 60:00 后立即停止；游玩时间未知时绝不发送新手内容。所有公告均使用 `shouldClearPrevious: false`，每名玩家只有一个有界调度状态，离开或停用时清理。

### 管理命令与配置

默认需要 `statsbots.manage` 权限：

```text
statsbots status <完整UserId>
statsbots grant <完整UserId> <称号ID>
statsbots revoke <完整UserId> <称号ID>
```

未缓存的离线 MySQL 记录会在工作线程水合，避免 RA 查询阻塞 Unity 游戏线程。第一次命令会回复“加载中”，水合完成后重试即可；等待期间不会创建空记录。

审计没有规定每次击杀分数、段位阈值和称号目录，因此这些值均明确放入配置：默认每次 10 分，段位阈值 0/100/500/1500/5000/15000/40000，称号阈值 0/100/500/1500/5000/15000/40000。`language: ""` 在可获得客户端语言时匹配；当前 LabAPI 不提供该值，因此回退中文；`cn`/`en` 可强制语言。

缩写后的左侧安全区布局已通过物品轮盘、公告/状态条、观察者、等待玩家和出生闪屏五种原生背景的 EN/CN 碰撞检查。静态结果记录于 `tests/ui/20260830-results.md`，上线后仍需确认真实 TextMeshPro 显示。

StatsSystem 没有公开的“水合完成”回执，写操作也没有成功返回值。StatsBots 会将加入初期的计分事件放入每用户最多 64 条、全局最多 512 条的有界队列；默认 5 秒宽限后，也必须等工作线程完成水合检查，才会创建确实不存在的新记录。它仍无法替上游提供逐事件持久化保证，请监控 StatsSystem 日志并保留数据库/文件快照。
