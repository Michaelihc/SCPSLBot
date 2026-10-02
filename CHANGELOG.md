# Changelog

## Unreleased

- Add the `bots_only` master switch, default `true`: plain AI bots with the warmup layer, overflow
  cleanup, the warmup SSS menu, WarmupSafezone and StatsBots off. Existing warmup servers must set
  `bots_only: false` to keep their behaviour. `bot_status` reports it and `bot_warmup` refuses mode
  changes while it is on.
- WarmupSafezone enables at `Low` load priority so it always follows SCPSLBot.
- The warmup SSS controls register only while Standard warmup runs, so ServerKeybinds leaves the native
  menu alone otherwise; `bot_warmup` switches them on and off.
- Under `bots_only`: the native `players` header is no longer rewritten, and the runtime navmesh skips
  its periodic re-scan while no bot exists.
- Bots no longer target dummies that SCPSLBot did not create.
- Restoring LCZ decontamination only undoes SCPSLBot's own override, never one set by the round,
  an admin, the warhead or another plugin.
- Commands renamed to avoid collisions: `teleport` is `bot_tp`, `raycast` is `bot_raycast` (now
  requires `GameplayData`), `plugin_test` is `bot_test`, `position_local` is `bot_position_local`;
  the `warmup` alias of `bot_warmup` is removed.
- README rewritten in English only: it now describes what the bots do, lists the optional warmup
  features with their switches, gives a bots-only configuration, and adds gameplay media.

## v2026.10.01-012cae8 (2026-10-01, production)

- Correct the native server-console player-count header to exclude dummies and the dedicated host, keeping LocalAdmin's human-count restart gates consistent with the roster.
- Detect and recover destroyed identities stranded in Mirror's spawned, observing and ownership registries; retain object/component provenance and expose recovery counters through `bot_health`.
