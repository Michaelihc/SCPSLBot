# StatsBots implementation notes

## Product boundary

StatsBots is a separate LabAPI product. It does not reference or modify StatsSystem, HintServiceMeow, ServerKeybinds, CUIMeow, PlayerBadge, or AdditionalNameTags source. Runtime dependencies are late-bound so this assembly can be built in isolation. It never owns gameplay role/item grants and never writes display-name, badge, or permission-group state.

## Identity and scoring interpretation

The audit's scoring matrix says to increment score but supplies no amount. `score_per_bot_kill` defaults to 10 and is validated non-negative. A real player is `IsPlayer && IsReady && !IsDummy && !IsHost && !DoNotTrack` with exactly one non-edge `@` in UserId. This accepts future authenticated providers without hard-coding Steam. `ID_Dummy` fails before every provider mutation.

Managed bots are attributed only through the public `SCPSLBot.Api.ManagedBotIdentity.IsManaged(Player)` contract, and the alive/active HUD target count uses its public `LiveCount` property. StatsBots does not reflect `BotManager` or its mutable registry. Difficulty is a cosmetic late-bound public-property read. No `IsDummy` fallback exists. Same-faction deaths are treated as disallowed team kills. Duplicate suppression keys the victim network ID plus the native damage-handler object identity, which coalesces duplicate callbacks without suppressing a later legitimate death with a distinct handler.

## StatsSystem adapter

Every reflected call passes `file: null`, selecting the default `player_stats` store that StatsSystem hydrates on join. Reads first call `TryGetStats`; a missing record is Loading, not zero. The adapter rebinds when the public static provider instance changes after reload. Invocation failures invalidate the provider and surface Unavailable.

StatsSystem's concrete plugin/repository types are internal, hence reflection is necessary even though its provider interface is public. Offline RA operations invoke the concrete repository's internal `EnsureHydrated` on a worker task and return Loading until that task completes. This avoids blocking Unity's main thread and does not create a blank record while a shared-store read is pending. StatsSystem exposes neither an async public offline hydrate nor a health/write receipt.

Scoring during the join hydration grace is queued by event, bounded at 64 entries per user and 512 globally. After five seconds StatsBots starts/polls its own worker-thread `EnsureHydrated` call; only after that finishes and a fresh read still finds no row may it initialize a genuinely new online record. Success/deferred/dropped/partial-failure outcomes have distinct flash copy; a deferred event is never presented as committed. Score uses the provider's merge-safe increment operation with a snapshot-based saturating delta instead of overwriting the shared value. Provider mutations are serialized inside StatsBots but the provider exposes separate void operations for each counter, so true multi-key transactions and per-event durability cannot be promised. Once a multi-key attempt starts, a later failure is terminal and the original mutation is not replayed, avoiding duplicate earlier deltas. Clean disable calls provider `Save`.

## Title state

`Warmup.TagUnlocked.<id>` values mean `1` explicit grant, `-1` explicit revoke, and missing/`0` automatic score threshold. This makes revoke idempotent and prevents a high score from immediately undoing an admin revoke. `Warmup.SelectedTagCode=0` means none. A removed catalog code is shown as orphaned in RA status and omitted from HUD/SSS; it is never substituted.

Catalog validation rejects malformed IDs, duplicate IDs/codes, non-positive codes, missing localized labels, and negative thresholds. Tier catalogs gain a score-zero Recruit fallback when necessary.

## SSS compatibility fork

StatsBots uses the reserved base 1131000. The adapter accepts both the API-3 four-parameter and API-4 five-parameter `AddDropdownForPlayer` signatures and requires `RequestPlayerRefresh(Player,string)`. For API 4 it passes a null acquisition callback so loading a persisted client value cannot select a title. Expression trees create the fork-owned delegate/model types without a compile reference. The fork owns acquisition-baseline suppression, sent-generation validation, fingerprinting, debounce/coalescing, and rate limiting. StatsBots still regenerates the current unlocked model and validates the selected display value/title ID immediately before write.

Five fixed two-button labels follow forced language, or Chinese fallback when language is empty because LabAPI has no client-language property. The personalized title dropdown and HSM/broadcast text use per-player language if a future public wrapper property appears.

Initial SSS acquisition has no action. The compatibility fork treats each dropdown send generation's first value as baseline only, and StatsBots swallows the first acquisition callback for each two-button control before enabling its in-memory display/broadcast preference handler. These controls never grant roles, items, score, or titles. A missing/incompatible fork emits one warning and otherwise leaves the HUD, broadcasts, RA commands, and localized Player Console fallback operational.

## HSM and broadcasts

The local hint layer follows the metarepo HintDisplayProvider pattern: optional HSM reflection, stable IDs/group, explicit removal, and center alignment only. There is deliberately no native `SendHint` fallback because that shared channel could overwrite another plugin. The default X `-800` profile appends a transparent forty-five-cell 1em tail to every line, placing abbreviated visible ink in the left safe lane while preserving HSM Center alignment. Rendered hero/footer snapshots are cached only after the provider remains available; HSM updates otherwise retry. Provider state changes are visible as Loading/Unavailable.

Native notices use one state object per connected authenticated real player. It stores no unbounded message list. Priority is setup, then due community, then tip; an occupied StatsBots slot leaves lower-priority due state for a later tick. Every native call is per-player with `BroadcastFlags.Normal` and `shouldClearPrevious:false`; StatsBots never calls `ClearBroadcasts`.

## Manual integration still required

- Install the ServerKeybinds compatibility fork instead of upstream and verify the personalized title model/baseline behavior in a real client.
- Run a two-client EN/CN check. Current LabAPI cannot report client game language, so empty-language builds fall back to Chinese unless another future public wrapper property exists.
- Verify provider absent, late load, MySQL hydrate delay/failure, clean restart persistence, and reload rebinding on the dedicated bot-test port 8888.
- EN/CN left-lane fixtures pass static caret/wrap checks and all registered collision checks at 1920x1080 against inventory, announcements/stat bar, spectator, waiting, and spawn-flash backgrounds; see `tests/ui/20260830-results.md`.
- Confirm native RA/warhead/decontamination/plugin broadcasts coexist under realistic queue load. Static code guarantees `shouldClearPrevious:false`, but client FIFO behavior needs live observation.
