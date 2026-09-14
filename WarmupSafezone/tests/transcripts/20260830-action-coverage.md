# Native action coverage record — 2026-08-30

## Executable coverage

`warmup-safezone-actions` drives SCP-173's real
Breakneck `ServerProcessCmd` path through a harness actor while the actor is inside SCP-914. The
harness requires both the cancellable pre-event and the completed native state transition.
Breakneck is intentionally allowed because it is movement/utility rather than an offensive action.

The isolated port-8015 live run passed on game version 14.2.7:

```text
ptest run warmup-safezone-actions standard
RUN_RESULT level=standard passed=1 failed=0 skipped=0
SCP-173 Breakneck ... expected=Succeeded allowed=True active=True
```

Duration was 40.638 seconds (the harness waits the native 40-second cooldown), with zero monitor
violations. The machine-readable result is
`20260830-052014-actions-standard.summary.json`.

The production action policy also has deterministic coverage in
`tests/WarmupSafezone.LogicTests.csproj`: firearm, dry fire, throwable, charged dangerous item,
targeted SCP offense, and area SCP offense cancel whenever the actor or target is protected;
utility/movement remains allowed. The passing `warmup-safezone-914` transcript separately proves
the final damage backstop with live actors.

## Concrete native-harness oracle gaps

These are live-test harness gaps, not known production-policy gaps. The scenarios stay inside the
public actor vocabulary required by `..\.tests\AGENTS.md`; they do not reproduce native mechanics
or invoke LabAPI event arguments directly.

| Action requested by the audit | Available actor verb | Why it cannot prove this plugin's pre-action cancellation |
|---|---|---|
| Firearm shot | `AttackExpectingBlocked` | The verb requires the shot to reach `PlayerEvents.Hurting`. WarmupSafezone intentionally cancels `PlayerEvents.ShootingWeapon` first, so a correct denial makes the verb fail with “never reached PlayerEvents.Hurting.” |
| Grenade/throwable release | `ThrowHeldItemAt` | The verb requires the exact serial to reach the successful `ThrewProjectile` confirmation. WarmupSafezone cancels `DroppingItem`/`ThrowingItem`/`ThrowingProjectile`; the harness has no `ThrowHeldItemExpectingBlocked` oracle for the absence of a spawned projectile. |
| Micro H.I.D. primary charge/fire | `UseHeldItem` / `UseHeldItemOn` | Both verbs require the complete wind-up/fire/wind-down cycle (and the targeted form requires attributable damage). WarmupSafezone cancels dangerous use and resets a phase that begins between events; no actor verb expects a primary cycle to be interrupted/reset. `AttemptDisabledMicroHidSecondary` only tests the separate Zoom/secondary input and would pass even without this policy, so it is not valid evidence. |
| Jailbird charge/attack | none | The current actor API exposes no Jailbird-specific native charge verb and no expected-cancellation oracle. Generic human `Attack` selects a firearm, so it cannot exercise `ProcessingJailbirdMessage`. |
| Offensive SCP ability | `Attack` plus utility-only `AttemptBreakneck` / `AttemptHuntersAtlas` | `Attack` requires an attributable successful hit and has no expected-cancellation form for SCP melee. The ability adapters with cancellation oracles cover Breakneck and Hunter's Atlas, both movement/utility actions that the policy intentionally permits; none drives SCP-049/096/106/173/3114/939 offensive pre-events with an expected-cancel result. |

Consequently, the firearm, grenade/throwable, Micro H.I.D., Jailbird, and offensive-SCP rows were
not claimed or executed as passing live native-action cases. Closing the negative-action gaps
requires new shared harness verbs that regard an observed
pre-event cancellation plus unchanged authoritative state (ammo/projectile/phase/cooldown/target
health, as appropriate) as success. That shared-harness change is outside this plugin-local rework.
