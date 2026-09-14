# WarmupSafezone implementation notes

## 2026-08-30 audit rework

### Architecture

The former 920-line plugin body was replaced by a composition-only `WarmupSafezonePlugin` and focused services:

- `SafezoneVolumeService`: authoritative surface and SCP-914 geometry.
- `SafezoneOccupancyService`: event-time membership and 100 ms recovery tracking.
- `SafezoneEnforcementService`: damage/effect/action matrix.
- `ExitProtectionService`: per-player monotonic exit expiries.
- `SurfaceHealthDrainService`: surface-only drain policy.
- `SurfaceBlockerService`: active-time penalty progression.
- `SafezoneVisualService`: surface bounds and SCP-914 gate signage.
- `SafezoneLifecycleService`: one owned resilient scheduler.
- `IHintDisplayProvider` implementations: stable HSM tags with a no-output provider when HSM is unavailable.

Enable is idempotent and transactional: partial construction/subscription is unwound through `Cleanup`. Disable is idempotent. Each recurring service has its own fault boundary inside the owned scheduler; the outer loop rearms in `finally`.

### SAFE-01 / SAFE-02: no native global ownership

No production code writes `SpawnProtected.IsProtectionEnabled`, `SpawnProtected.SpawnDuration`, `SpawnProtected.TryGiveProtection`, or `Player.IsGodModeEnabled`. Exit protection is a dictionary of player ID to monotonic deadline. A new grant can extend but never shorten an existing deadline.

`PlayerEvents.Hurting` cancellation is authoritative. Replacing `ev.DamageHandler` was intentionally rejected because current native `PlayerStats.DealDamage` continues with its original local handler after the event. In-place zeroing was also rejected because it is handler-specific and still emits the after-event path. Evidence:

- `../../.references/LabAPI/LabApi/Events/Arguments/PlayerEvents/PlayerHurtingEventArgs.cs`
- `../../.references/Decompiled/DedicatedServer/Assembly-CSharp/PlayerStatsSystem/PlayerStats.cs`

Both endpoints are resolved at event time. Incoming damage to protected victims and outgoing damage from protected attackers are canceled. Environment/self/indirect cases follow the documented matrix. Surface drain and blocker damage use a synchronous owned-damage marker so their `CustomReasonDamageHandler` can pass safely. `OwnedDamageRegistry.ApplyHealthDrain` then makes up only the configured health portion absorbed by AHP/Hume Shield; the native event/death pipeline still owns cancellation and lethal transitions.

### SAFE-03: blocker time semantics

`BlockerPenaltyTracker` records monotonic observations, active accumulated milliseconds, whether the prior observed interval was active, and a continuous outside-start time. Time observed outside never enters active progression. Re-entry discards the outside interval and resumes the previous accumulated value. Reset occurs only at the exact continuous outside deadline.

`BlockerDrainCalculator` integrates across one-second progression slices. A first post-grace 1000 ms slice uses exponent zero, so an initial setting of 1 HP/s produces exactly 1 HP, not 2 HP. Delayed ticks integrate every crossed slice rather than applying one late exponent to the entire delay.

The drain oracle is health rather than total barrier damage. This matters for SCP roles: their renewable Hume Shield previously absorbed each low initial blocker tick and could regenerate faster than the penalty. Live SCP-173 probing on port 8888 reproduced that bypass; the health-drain completion step closes it without disabling the native pre-damage event.

### SAFE-04: lifecycle fault survival

`ResilientSchedule` advances a work item's next deadline before invoking it, catches faults per item, and continues sibling items. `SafezoneLifecycleService` catches outer pass faults and rearms its 100 ms MEC callback in `finally`. Deterministic tests inject a failing recurring item and prove both sibling progress and later retry.

### SAFE-05: policy separation

Shared protection explicitly accepts the union of `SurfaceEscape` and `Scp914` membership. Surface health drain calls only `SafezoneVolumeService.ContainsSurface`; SCP-914 membership cannot reach that policy. The blocker is the configured band immediately outside the restored Surface threshold.

### SAFE-06: authoritative geometry

Surface membership restores the original configured axis/threshold/minimum-X volume after verifying the closest native room is Surface. It also iterates the live `LabApi.Features.Wrappers.Map.EscapeZones` list as an additional protected fallback. The visual service restores the original two-sided configured boundary wall and label column; it fingerprints the relevant config so runtime changes rebuild the owned toys.

Evidence:

- `../../.references/LabAPI/LabApi/Features/Wrappers/Facility/Map.cs`
- `../../.references/Decompiled/DedicatedServer/Assembly-CSharp/Escape.cs`

SCP-914 membership requires both `Scp914.Instance.Base.WorldspaceBounds.Contains(position)` and `Room.GetRoomAtPosition(position)?.Base == room.Base`. Native `RoomIdentifier.WorldspaceBounds` is calculated from child mesh renderer bounds; native `RoomUtils` supplies the second room-local verification.

The legacy axis/threshold/minimum-X fields are authoritative again for the configured Surface volume and visible boundary. `SurfaceEscapeBlockerDepth` defines the adjacent band outside that configured threshold; `Map.EscapeZones` are an additional protection fallback and do not replace or resize the configured visuals.

### SAFE-07: event-time recovery and action policy

Damage and dangerous-action handlers synchronously call `ResolveAtEvent`. This detects an exit before applying the current event and grants exit protection immediately. A 100 ms recovery tick maintains transitions without actions. A cancellable `ChangingRole` event clears stale occupancy/hints but deliberately retains the exit lease; death and disconnect clear the lease and all per-player state.

The action matrix is documented in the localized README. Explicit LabAPI pre-events cover firearms, throwing, HID/Jailbird, and the available offensive SCP events. SCP-244 is denied at `UsingItem`, again at `ItemUsageEffectsApplying`, and by the dangerous-item recovery tick if an active use crosses the boundary. The final hurting event remains the backstop for SCP attacks or indirect hazards without a suitable pre-event.

### Localization and hints

The provider follows `../../.templates/HintDisplayProvider`: feature services depend on `IHintDisplayProvider`; HSM is loaded through reflection; stable group/tag IDs replace/remove messages. If HSM is unavailable, a no-output provider keeps gameplay operational. WarmupSafezone contains no direct `SendHint` calls.

The production HSM profile retains `Center` alignment and the `Middle` anchor. All feature prompts use explicit X `-800`; `action-blocked`, `blocker`, and `surface-drain` use Y `150`, `235`, and `325` respectively. A transparent 49-column `1em` tail is appended to every physical row, which shifts the visible ink into the top-left safe lane while preserving HSM's centred layout contract. Base prompt size is 22, blocker title size is 32, and minimum line height is 12.

Active-hint keys use stable player IDs rather than `ReferenceHub.GetHashCode`. Delayed removal also captures the exact `ActiveHint` identity, so a disconnected/destroyed hub cannot fault a later callback and a reused numeric player ID cannot remove a replacement player's hint.

Localized composite fixtures in `tests/ui/warmup-safezone-{en,cn}.json` render the three stable tags together, which is stricter than normal blocker/drain coexistence. Ten 1920x1080 renders cover waiting, inventory, announcements/stat bar, spawn flash, and spectator backgrounds with `--fail-on-collision`; all static validation and layout issue arrays are empty. The first lower-left candidate exposed that the highlighted announcements image's visible CASSIE band is not fully represented by its registered rectangle, so visual inspection remains part of the gate. The final top-left placement avoids both the registered zones and that visible band. Exact ink bounds and output files are recorded in `tests/ui/20260830-results.md`.

LabAPI currently has no server-visible per-player client language. `language: ""` retains a per-player resolver boundary but presently falls back to Chinese. `cn` and `en` force their respective languages. Shared world panels necessarily use one server-selected language.

### Tests and remaining manual verification

`WarmupSafezone.LogicTests` deterministically covers blocker pause/resume/reset, exact initial damage, expiry extension/boundary, incoming/outgoing/self/environment/plugin-owned damage, every explicit action-policy category, and injected lifecycle faults.

`Scp914SafezoneScenario` replaces the obsolete godmode test. It probes live damage behavior, immediate exit protection through a cancelled role request, native static isolation, externally owned godmode preservation, panel collision/text, the visual-only 10× panel backing, normal-size panel text, surface-bound face alignment, ground raycasts, settling dummies, and SCP-class blocker drain. SCP-914 membership remains derived solely from native room bounds.

`SafezoneActionScenario` adds a real harness-driven SCP-173 Breakneck case and native SCP-244 use inside SCP-914. It
proves the intentional movement/utility allowance through the native command, cancellable event,
and completed state transition, then proves SCP-244 start is cancelled and the exact item remains.
The plugin-local action transcript lists remaining native negative-assertion gaps for firearm,
throwable, charged-item, or offensive-SCP pre-event cancellations; deterministic policy tests cover
those categories until shared expected-cancellation verbs exist.

Still requires an isolated server plus real client for:

- native shot/throw/SCP input feel while canceled at the pre-event;
- rapid boundary movement around the 100 ms recovery interval;
- panel readability through SCP-914 gate motion;
- multiplayer observation of simultaneous independent exit expiries;
- surface dummy settling/walking at every map/plugin-provided custom escape bound.
- final HintServiceMeow/TextMeshPro placement on a real 1920x1080 client, including readability during every native overlay state.
