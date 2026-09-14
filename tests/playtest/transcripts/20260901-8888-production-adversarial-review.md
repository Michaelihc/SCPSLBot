# Port 8888 production adversarial review — 2026-09-01

## Scope

Two independent adversarial passes reviewed the player SSS panel and WarmupSafezone/Surface boundaries.
The threat model assumed that clients may retain and submit stale SSS buttons/dropdown indices. Ordinary
players were in scope; trusted RA or another plugin deliberately mutating doors/toys is listed separately.

## Confirmed issues fixed

- Cross-arena `Teleport to` could carry a facility CI/SCP to a Surface player while retaining the role.
- Re-selecting the active arena performed a full role reset before cooldown, allowing stale-index health,
  inventory, and loadout churn.
- Dynamic option lists could remap stale numeric indices to a neighboring role/item/player.
- Sequential callbacks had no monotonic per-user mutation interval.
- Arena membership committed before exact role/spawn success and did not verify cancellation/substitution.
- Native item-driven roles originating on Surface were not covered by the SSS-only routing path.
- A cancellable role request cleared an existing safezone exit-protection lease.
- SCP-244 could complete via its native usable-item path because only ordinary dangerous-item events were
  covered.
- SCP roles were filtered out of Surface blocker/drain iteration; after removing that filter, renewable
  Hume Shield could still absorb/regenerate the low blocker ticks without losing health.
- A delayed HSM hint removal hashed a destroyed `ReferenceHub` after dummy/player teardown.

The fixes use stable role/item/loadout slots, per-player teleport tombstones, live callback revalidation,
same-physical-arena teleport, a one-second full-UserId mutation limiter, no-op current-arena selection,
transactional arena switching, generic Surface-origin native-role routing, retained exit leases, SCP-244
start/completion/recovery guards, all-role health drain, and stable player-ID hint keys.

## Static and deterministic gates

```text
SCPSLBotAddon.sln Release x64: 0 warnings, 0 errors
SCPSLBot.PolicyTests: 37/37 passed
WarmupSafezone deterministic logic: 46/46 passed
ServerKeybinds.Compat pure scenarios: 7/7 passed
SCPSLBot.PlaytestScenarios: 0 warnings, 0 errors
WarmupSafezone.Playtests: 0 warnings, 0 errors
Scenario lint: errors=0; 11 unrelated legacy migration notices
```

## Live port 8888 results

- `warmup-safezone-914 standard`: PASS, 26.797 s, zero monitor violations. It covered the four-way damage
  matrix, cancelled-role exit protection, visual-only 10x SCP-914 panel scale/non-collision, native escape
  bounds, downward raycasts, settling/soak, SCP-173 blocker health drain, and foreign godmode preservation.
  Artifact: `20260901-024307-standard.summary.json`.
- `warmup-safezone-actions standard`: PASS, 43.316 s, zero monitor violations. Native SCP-173 Breakneck
  remained usable, while native SCP-244 start was cancelled and the exact item stayed in inventory.
  Artifact: `20260901-024424-standard.summary.json`.
- `surface-role-routing standard`: native Surface spawn, raycast, and settling setup passed, then the scenario
  SKIPPED explicitly because production routing is authenticated-player-only and PlaytestHarness actors are
  server dummies. Artifact: `20260901-024907-standard.summary.json`.

The final visible boot log is
`%APPDATA%\SCP Secret Laboratory\LocalAdminLogs\8888\LocalAdmin Log 2026-09-01 02.50.03.txt`.
It contains zero error/fatal/exception matches, loads only the regular `SCPSLBot.Warmup` SSS block (no Tools
block), starts the round, locks the six door components across the three Surface lifts, and creates three
warmup bots. Harness autorun was restored to empty.

## Final deployed SHA-256

```text
SCPSLBot.dll                   24F1062D8D15A2CA3A5CD83A4822332131F248B271F5095DD3CE937EFBEFFD15
SCPSLBot.Components.dll        97AF97428487F485FD0BF9FF573049CBF1B5475B220286BD234D3A0DAFA2A0F0
StatsBots.dll                  411BA8F58671CB23D2892B0C501EEF37D13861586B143F3596B8D2C47709569C
WarmupSafezone.dll             D8E90B230048C17828B2C8A7967E6C97AB51B57C5FDFE88DDFEFD3D8B6694FD0
SCPSLBot.PlaytestScenarios.dll F0529756883C573A949B3633AF70708E0FBD2CBDEC59BC75E57E8B08FB8AB26D
WarmupSafezone.Playtests.dll   4252D12CCDD913C5B9C33FF6C5AFE0CEED35945DB21391285923C4C04F103F74
ServerKeybinds.dll             9FF98C0E2E0CFBAC319C40528C4BB6FAB5A0B05D7B53A9AFC21A25832A015F78
```

Both `plugins/global` and `dependencies/global` are empty. The deployment target was port 8888 only;
port 7777 was not changed.

## Remaining release gates and compatibility risks

- One authenticated client must execute CI and SCP/item-driven role changes while physically on Surface,
  then repeat inside HCZ/LCZ to confirm Surface evacuation and facility position preservation through the
  real-player-only branch. A second client should replay stale SSS indices against changing teleport lists.
- The 8888 StatsSystem 2.2.0 assembly identifies itself as a development/pre-release build. Production must
  pin an approved StatsSystem build before promotion.
- A trusted plugin/RA that later clears/replaces the shared native elevator lock bit can override the warmup
  lock; a trusted admin-toy plugin can mutate a still-existing visual toy until it is deleted/recreated.
  Neither path is reachable by an ordinary player on the reviewed stack.

