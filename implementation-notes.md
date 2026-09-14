# Implementation Notes

## 2026-08-30 audit rework

This implementation follows `SCPSLBot-Audit-2026-08-30.html`. The lifecycle changes selectively
port the transactional/disposal/nav-generation work from local commit `f06cc4b`; the larger branch
was not cherry-picked. Existing upstream StatsSystem, HintServiceMeow, ServerKeybinds,
CustomizableUIMeow, PlayerBadge, and AdditionalNameTags sources remain outside this product.

The runtime products are deliberately separate:

- `SCPSLBot` owns warmup mode, managed bot identity/lifecycle, combat, navigation, role/item policy,
  warmup SSS controls, and bot/nav diagnostics.
- `WarmupSafezone` owns safezone volumes, occupancy, event enforcement, exit protection, blocker
  penalties, health drain, visuals, and its text lifecycle.
- `StatsBots` owns persisted warmup scoring/title state, the three-band HUD, title/display settings,
  and onboarding/community broadcasts.
- `ServerKeybinds.Compat` is an alternative build of the process-local `ServerKeybinds.dll`, not a
  second registry. Packages must choose it instead of upstream when the personalized API is needed.
  Local lanes deploy it under `dependencies/<port>` and leave `dependencies/global` empty.

## Bot lifecycle and readiness

`BotPopulationController` is the only maintained-population reconciler. Wake-ups invalidate work;
they do not each create delayed spawn callbacks. A bot counts as live only when its native dummy is
network-registered, its managed graph is published and undisposed, and its exact configured FPC role
is initialized for the current navigation generation. Failed construction is rolled back in reverse
order and native dummy destruction is scoped to the plugin-owned hub.

Native spawn readiness follows Mirror/network, restart, host-hub, prefab, and map-generation state.
`SeedSynchronizer.MapGenerated` is the current native boolean; the plugin owns the monotonic map
generation counter because the game exposes no such counter. Relevant native evidence is in:

- `../.references/Decompiled/DedicatedServer/Assembly-CSharp/CustomNetworkManager.cs`
- `../.references/Decompiled/DedicatedServer/Assembly-CSharp/MapGeneration/SeedSynchronizer.cs`
- `../.references/Decompiled/DedicatedServer/Assembly-CSharp/NetworkManagerUtils/Dummies/DummyUtils.cs`
- `../.references/Decompiled/DedicatedServer/Assembly-CSharp/RoundRestarting/RoundRestart.cs`

Warmup exclusively owns desired maintained-bot roles. Native role changes return no success value and
may be canceled or replaced, so reconciliation verifies the exact FPC role afterward and retries with
bounded backoff. The old global SCP-049/SCP-173 relocation was removed.

The AI runner is supervised per frame: one bot fault does not stop siblings, repeated per-bot faults
park that bot briefly, and the runner records a heartbeat/fault for `bot_status`. Disposal is
idempotent through BotHub, FPC player, mind, beliefs, perception, jobs, pinned handles, native arrays,
global subscriptions, and collision-layer restoration.

## Navigation persistence and pathing

Navigation load work is generation-owned and canceled on lifecycle changes. A document is fully
read/validated before publication. Invalid live files are quarantined, backup recovery is attempted,
and saves use a replace/backup transaction rather than truncating the live file. Publication updates
topology and ready-generation state atomically.

A* retains a thread-local workspace and stable reusable binary min-heap instead of allocating several
dictionaries and performing an O(V) LINQ minimum each iteration. Combat uses one materialized 8 Hz
world snapshot and cached immutable difficulty profiles. The long live performance gate still requires
a 10-bot/30-player soak because allocation and Unity main-thread budgets cannot be proved by pure tests.

## WarmupSafezone rewrite

The recovered 920-line lifecycle/godmode plugin was replaced by focused services. Surface membership
restores the configured axis/threshold/minimum-X safezone and retains every current
`LabApi.Features.Wrappers.Map.EscapeZones` bound as an additional protected fallback. Native bounds match
`Escape.EscapeZones` membership in
`../.references/Decompiled/DedicatedServer/Assembly-CSharp/Escape.cs`.

SCP-914 membership requires both the live room's verified world-space bounds and
`Room.GetRoomAtPosition` identity. Surface drain applies only to the surface membership, not the union
with SCP-914.

Damage and offensive-action policy resolves both endpoints at event time. Protection cancels
`PlayerHurtingEventArgs.IsAllowed`; replacing the handler would not work because native
`PlayerStats.DealDamage` continues with its original local handler
(`../.references/Decompiled/DedicatedServer/Assembly-CSharp/PlayerStatsSystem/PlayerStats.cs`).
Plugin-owned drain/blocker damage has a synchronous scoped bypass. Exit protection is plugin state
driven by a monotonic clock. The plugin no longer writes `SpawnProtected` statics and never grants or
removes native godmode.

One resilient scheduler isolates each recurring service, advances deadlines even after a fault, and
does not grow recursive callback chains. Visual recovery recreates exact missing toys without
duplicating existing ones. HSM owns stable text tags. If HSM is unavailable, the runtime uses a null
provider and logs the missing integration; it never falls back to the process-wide native hint slot.

## Player text and language

SCPSLBot, WarmupSafezone, and StatsBots each own a copied HintDisplayProvider boundary because they are
separate products. Feature code does not clear the native broadcast queue or send raw hints. The main
plugin's old full planner graph was replaced with a compact spectator diagnostic card and nav authoring
uses a separate stable HSM tag. Both are gated by admin tools.

HSM output is always center-aligned with an explicit X. The warmup notice moved from Y=930 to Y=1040
after the native-background renderer found an inventory-wheel collision. WarmupSafezone occupies the
upper-left Y=150/235/325 bands and StatsBots the lower-left Y=735/780/850 bands, both using centered
HSM text with a calibrated transparent tail. Reproducible EN/CN fixtures containing all three products
passed waiting, inventory, announcements/stat bars, spawn-flash, and spectator backgrounds with zero
non-OK validations or collisions at 1920x1080.

LabAPI 1.1.7 has no authenticated per-player client-language property. Therefore `language: ""`
keeps the per-player resolution seam but falls back to Chinese; `cn` and `en` force a language. This is
preferable to reading the server process's local PlayerPrefs, which is not the remote client's state.

## Server-Specific Settings compatibility fork

`ServerKeybinds.Compat` is pinned to upstream commit
`6c2229e0b707347604f441e51c4790bca6ad3a07`. It preserves the upstream assembly name, namespace, and
public API. API 4 adds personalized regular dropdowns and native buttons, per-send acquisition
baselines, final-view fingerprints, interest routing, and one refresh coordinator with:

- a trailing 500 ms debounce and latest-snapshot replacement;
- reason coalescing and identical-view suppression;
- a two-second per-player minimum send interval;
- at most six sends per rolling minute;
- targeted one-to-two/two-to-one real-player population invalidation.

Opening, acquiring, or refreshing a personalized action dropdown establishes a baseline and performs
no action. Personalized button callbacks carry no client choice; consumers execute only a staged,
server-owned selection and still revalidate it against current authority. Reserved
blocks are 1130000 (SCPSLBot gameplay), 1131000 (StatsBots), and 1132000 (SCPSLBot tools).

For explicit button workflows, an opt-in acquisition callback stages the client's persisted visible
dropdown value without executing it. Without that callback, a non-placeholder role could be visibly
selected while Apply correctly found no server-owned pending value. Immediate-action dropdowns such as
Teleport do not opt in and retain acquisition swallowing.

## Role and item authority

Role and item policy is independent of SSS presentation. Regular role selection is intentionally
permissive: it enumerates the runtime's registered native `RoleTypeId` values and excludes only `None`,
`Spectator`, `Destroyed`, `Overwatch`, `Filmmaker`, `CustomRole`, and `Tutorial`. Legacy configuration
allowlists, arena role lists, team capacity, current role, and spectator state do not gate presentation
or execution. Spawn anchors are resolved before a native role change, the exact result is verified, and
a substitution is rolled back instead of accepted as a fallback. Admin Force retains its permission
boundary but uses the same intrinsic role exclusions.

Item catalog/cooldown identity uses the full authenticated UserId and stable catalog IDs. A shared
per-user in-flight guard plus a monotonic minimum action interval protects every mutation callback. Item and group cooldowns, per-life limits,
and per-round limits are checked with a monotonic clock. One exact native `AddItem` call is made and
ledger state commits only after the returned item matches. Death, Spectator, role changes, SSS refresh,
and reconnect do not reset round state.

Role, item, and arena dropdown callbacks are presentation-only staging operations. Pending choices are
keyed by numeric PlayerId, full UserId, action type, and stable value. Disconnect, a visible placeholder,
or a value that fails current catalog/authority validation clears them; successful execution, death, and
role refresh do not silently consume a choice the client still visibly displays. `Apply`/`Grant` consumes
the mutation rate limit only when pressed and revalidates current authority before executing. Successful
arena Apply invalidates only that player's role/item/zone view so the menu refreshes without global fanout.
All deliberate SSS feedback uses a per-player native broadcast with `shouldClearPrevious: true`; evacuation
is scheduled one tick after a role result so it remains the final visible notice. Legacy loadout config
remains readable for compatibility, but no player SSS loadout control is registered.

During Standard warmup, native `PlayerRoleManager` grants `SpawnProtected` on every playable role
assignment regardless of whether it followed a death. `WarmupPlayerSpawnProtectionService` tracks real
players through LabAPI `Death`, then consumes that per-hub marker on the first later playable
`ChangedRole`. That confirmed death respawn retains the native effect; any other real-player playable
role/loadout assignment in Standard mode removes it synchronously after native role completion. Outside
Standard mode the service leaves native protection unchanged. Dummies are ignored and retain
`BotManager`'s existing unconditional post-role clear. Markers are discarded on disconnect, round
restart, and plugin shutdown. A plugin that separately uses native `SpawnProtected` during Standard
warmup may conflict with this policy.

## Round-owned spectator recovery

Automatic real-player recovery no longer subscribes to join, death, spawn, or role-change events.
`WarmupRoundRespawnService` owns one periodic round scan of `Player.ReadyList`, keyed by numeric PlayerId
plus exact `ReferenceHub` identity so reconnect/ID reuse cannot inherit a schedule. Only exact native
`Spectator` is eligible. A playable-to-Spectator transition restores that playable role after the human
death delay; a first-observed Spectator uses the configured warmup human role after the spectator delay.
Because a Spectator has no authoritative body, role routing uses its server-owned arena membership and
never its camera coordinates. A logical Surface spectator requesting/restoring CI is therefore committed
to HCZ/EZ before native role assignment; playable roles still route from their generated-map room.
`None`, `Destroyed`, `Overwatch`, alive roles, hosts, and all dummies are ignored. Native rejection keeps
the request scheduled; the first and every twentieth retry are logged to avoid silent failure or flooding.

The manual recovery surfaces use the same intent: regular role selection is available to Spectators,
and selecting an already-active arena is an accepted no-op for an alive player but performs the arena's
native default-role transition for an exact Spectator. Arena-switch cooldown does not block this same-arena
recovery. SSS action rejections now log the stable result code and detail.

## Surface bot spawn and sustained ammunition

Arena membership and spawn faction are separate contracts. A managed CI bot assigned to `surface`
uses the exact CI role's native reinforcement spawnpoint; it must not inherit the NTF Private anchor
used for real-player Surface placement. Real-player CI selection on Surface still evacuates to HCZ/EZ.
`BotPopulationController` publishes the desired bot spec before synchronous `ServerSetRole` execution.
`WarmupArenaService` then captures the exact native, spawn-reason-aware CI position during `Spawning`
and carries that position through one bounded spawn transaction with two delayed corrections. Assignment
changes receive one placement repair, while the 250 ms health reconciler never continuously teleports a
healthy bot. The pure `WarmupBotSpawnAnchorPolicy` tests all four native CI roles so a zone-only assertion
cannot mistake the MTF and CI sides of the same Surface zone again.

Native `PlayerRoleManager` grants `SpawnProtected` after its role-changed callback and does not exclude
dummies. `BotManager` therefore schedules a post-callback effect clear for every managed bot role change;
human spawn protection and safezone policy remain untouched. The Surface managed-CI scenario asserts the
effect is absent on initial creation, death/respawn repair, and a fresh off/on population.

Sustained ammunition is provided by the existing `LabAPI_InfiniteAmmo` 1.0.1 plugin, installed per
port. SCPSLBot does not implement a competing general infinite-ammo system. The plugin intentionally
does not replenish SCP-127 or Particle Disruptor ammunition. LabAPI role/item/reload events include
managed dummies, but native reload validation happens before the plugin refills reserve ammo. A fully
dry bot therefore keeps issuing the rate-limited native reload input: the first request primes reserve
through the plugin and the following request completes the reload. Without that plugin, the last loaded
round is preserved rather than wasted on an impossible reload.

## StatsBots interpretation

StatsBots late-binds existing providers and uses the default StatsSystem `player_stats` store with only
`Warmup.*` keys. Persisted identity must be a full real UserId. `ID_Dummy` is always rejected, and a
dummy is attributed only through `SCPSLBot.Api.ManagedBotIdentity`, never `Player.IsDummy` alone.
That public boundary also exposes the live managed-bot count while excluding disposed, parked, dead,
or incomplete bot graphs; StatsBots does not reflect into BotManager internals.

Allowed real-player-to-managed-bot kills increment bot kills, score, and streak/best streak. A managed
bot killing a real player increments bot deaths and resets that player's current streak. Bot-to-bot,
real-to-real, self, world, and team kills are ignored. Provider loading/failure remains visible as
loading/unavailable rather than a fake zero.

The audit did not specify a score amount, tier thresholds, or title catalog. These are explicit config
defaults (10 points; tier/title thresholds documented in `StatsBots/README.md`) rather than hidden
assumptions. Title grants/revokes are exact and idempotent; the personalized selector lists only titles
that are currently unlocked.

StatsSystem's counter mutators expose no commit receipt or multi-key transaction. Score deltas use its
merge-safe increment operation. If any step of a multi-key event throws, StatsBots reports the write as
uncertain and does not replay it, because replaying could duplicate steps that already committed.
Loading events are queued with per-user and global caps; overflow is reported rather than shown as a
successful score change. Offline hydration runs away from Unity's main thread and a missing record is
created only after that hydration completes and a fresh read still confirms absence.

Onboarding uses one bounded per-player scheduler. Beginner decisions require verified total playtime
plus the current session; unknown playtime sends no beginner content, and eligibility ends at 60:00
without requiring reconnect. Native broadcasts use `shouldClearPrevious: false`.

## Build and permission decisions

Unconditional post-build deployment was removed from the test/reload projects and those projects were
removed from the production solution. Runtime project deployment remains opt-in. Release assemblies
carry semantic/file metadata, and the compatibility fork never auto-deploys.

Mutating RA surfaces use native-aligned permissions: PlayersManagement for bot/player mutations,
ServerConfigs for nav authoring/load/save, GameplayData for read diagnostics, and FacilityManagement
for `bot_status` (matching native dummy commands). Query-only difficulty/warmup reads stay available.
Permission behavior is evidenced in:

- `../.references/Decompiled/DedicatedServer/Assembly-CSharp/Misc.cs`
- `../.references/Decompiled/DedicatedServer/Assembly-CSharp/CommandSystem/Commands/RemoteAdmin/Dummies/SpawnDummyCommand.cs`
- `../.references/LabAPI/LabApi/Loader/Features/Commands/Reload/ConfigsCommand.cs`

## Verification completed

The production solution builds with zero warnings and errors with deployment disabled. Deterministic
results are SCPSLBot policy 70/70, ServerKeybinds personalization 10/10, performance structures 5/5,
WarmupSafezone logic 46/46, and StatsBots logic 7/7. The shared scenario linter reports zero errors;
its 11 legacy entries are unrelated repository migration backlog.

On isolated port 7911, exact maintained-population recovery and canceled-role/death recovery passed.
The first native-walk run exposed a spawn-facing deadlock: a valid waypoint behind the dummy caused
the steering layer to submit zero input forever. Steering now always converts the intended world
direction through the motor's local-input boundary. The fresh walk-only rerun completed both legs in
31.39 seconds with four door transitions, zero stalls, zero teleports, zero ground misses, and 13/13
ground probes. The server, config, and temporarily substituted ServerKeybinds dependency were restored.

On isolated port 8015, the SCP-914 membership/protection scenario and an SCP-173 utility-action scenario
passed. Both test servers are stopped. Exact run transcripts, UI PNGs, and final release hashes are in
the product `tests` directories and `tests/release`.

Port 8888 is the dedicated local bot-test port for subsequent work. It is provisioned with the runtime
suite, HSM, PlaytestHarness, bot/safezone scenario assemblies, StatsSystem, and ServerKeybinds.Compat
API 4; it has no DummyRoleFiller. `tools/Start-BotTestServer8888.ps1` starts visible LocalAdmin with a
lane-specific StatsSystem state root so test data cannot mix with port 7777. The 7911 references above
are retained as historical evidence rather than as the active deployment target.

## Classic player-panel restoration (2026-08-30)

The first audit deployment regressed the useful player contract in four ways: StatsSystem was omitted;
the replacement treated an arena as a global role/item filter rather than a physical per-player area;
arena selection disappeared with more than one real player; and the defaults exposed only 11 roles and
three items while enabling an administrator Tools SSS block.

The player SSS surface now contains no debug/diagnostic/nav-authoring controls. Its regular role catalog
enumerates all currently registered native gameplay roles, and its item catalog restores all 69 safe native items while filtering
`None` and `DebugRagdollMover`. Exact server-side revalidation, identity checks, cooldowns, and acquisition
suppression remain in place. The three classic physical arenas are `surface`, `pvpve`, and `lcz`; arena
selection is per player. Facility Guard and all four NTF ranks may remain on Surface. An exact role change
relocates any other player who is physically there: humans route to HCZ/EZ and SCPs to LCZ. Role changes inside the facility restore
the exact pre-change position after native assignment. Failed assignments restore the prior arena state.

`WarmupArenaService` owns player/bot arena membership, spawn overrides, placement, and cross-arena combat
filtering. `WarmupArenaPopulationPlanner` is pure and deterministic: LCZ occupancy creates at least one
SCP bot, HCZ/EZ occupancy creates at least two opposing human-faction bots, Surface uses the classic
player-factor rule, the empty-server baseline remains configured, and the total stays capped at 10.

The arena dropdown includes the active arena, so the client always receives exactly the three stable
contract IDs. Role and item authority resolve the active preset per player. Physical placement is
restricted to native placement sources: NTF Private and Class-D use validated LabAPI
`RoleExtensions.TryGetRandomSpawnPoint` results for Surface and LCZ. HCZ/EZ entries round-robin across
distinct generated rooms from `DoorNametagExtension.NamedDoors` and use
`DoorTPCommand.EnsurePositionSafety`, the exact collision walk used by native RA `doortp`; SCP-939 remains
the fail-safe only when no valid HCZ/EZ named-door target exists. The room-center placement path was removed.
Only Gate A/Gate B Surface elevator doors receive the plugin-owned `AdminCommand` lock bit; every other
door is untouched, and cleanup removes only lock bits the plugin added.

StatsBots proactively hydrates an authenticated player's record without blocking Unity. Once hydration
completes, it creates a missing record and refreshes both HSM and the personalized SSS selector on the
provider-state transition; a new player no longer remains in the loading presentation until a death.

## Production adversarial hardening (2026-09-01)

Two independent adversarial reviews attacked the player panel and safezone/Surface boundaries with the
explicit assumption that old SSS buttons and dropdown indices remain callable. Confirmed issues were
fixed; speculative findings that require a trusted administrator or another plugin to mutate world state
remain documented below rather than being disguised as player exploits.

Panel mutations now share a full-UserId monotonic rate limiter in addition to the synchronous in-flight
guard. Role, item, and arena dropdowns stage stable server-owned selections for explicit Apply/Grant
buttons, while per-requester teleport lists retain unavailable tombstones for departed or cross-arena targets. Execution revalidates the live wrapper, identity, physical
arena, native role/item authority, limits, and cooldown after resolving the stable slot. Teleport is
same-physical-arena only. Re-selecting the current arena is a no-op, so alternating stale indices cannot
reset health/inventory. Arena switches are transactions: logical membership commits
only after exact-role and native-destination verification, with rollback on cancellation or substitution.

`WarmupArenaService` captures every real player's physical origin for native role changes, not only SSS
role requests. Facility Guard and the four NTF ranks are valid on Surface. Every other role originating
there is evacuated through a native facility target (SCP to LCZ, other roles to HCZ/EZ) and receives a
localized per-player native broadcast that flushes that player's stale broadcast queue before display;
native item/revive/resurrection changes originating inside LCZ/HCZ/EZ retain
their exact position. `Spawned` also enforces the final invariant: if any native or foreign path still
finishes a real player on Surface as any disallowed role, the player is synchronously evacuated and the
correction is logged with role, target arena, final zone, and position. This closes direct CI/SCP-on-Surface
requests and item-driven arbitrary-role paths even when origin capture is unavailable.

Port 8888 live verification passed `warmup-safezone-914` and `warmup-safezone-actions`. Those runs cover
cancelled-role exit protection, Surface/SCP-914 geometry, the visual-only 10x panel backing with normal-size text, all-role blocker drain,
SCP-244 native start cancellation, settling dummies, raycasts, and cleanup. The authenticated-player-only
Surface role-routing path cannot be represented by a server dummy without weakening the production
player/dummy boundary, so it remains a required connected-client release check; deterministic policy tests
cover its routing matrix and transaction decisions.

## Remaining live/manual verification

The completed isolated scenarios do not replace the audit's repetition and multiplayer gates. Still
required are 20 cold-boot/restart and disable/enable cycles, construction-stage fault injection, the
100-cycle churn/resource baseline, two-client SSS forgery/language checks, StatsSystem scoring and
restart persistence against the real backend, and the 30-minute 10-bot/30-player performance soak.
The safezone firearm, throwable, Micro H.I.D., Jailbird, and offensive-SCP cancellation matrix remains
manual because the harness lacks native completion oracles for those verbs. A visible client must also
confirm final TextMeshPro readability and safezone toy appearance/animation.

Non-player residual compatibility risks: the elevator-lock service does not continuously arbitrate a
trusted plugin or administrator that later clears/replaces the same native lock bit, and the visual
recovery loop does not overwrite properties of a still-existing panel toy mutated by another trusted
admin-toy plugin. Neither path is reachable through ordinary player SSS or safezone actions on the current
8888 stack.
