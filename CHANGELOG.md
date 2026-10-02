# Changelog

## Unreleased

- README rewritten in English only: it now describes what the bots do, lists the optional warmup
  features with their switches, gives a bots-only configuration, and adds gameplay media.

## v2026.10.01-012cae8 (2026-10-01, production)

- Correct the native server-console player-count header to exclude dummies and the dedicated host, keeping LocalAdmin's human-count restart gates consistent with the roster.
- Detect and recover destroyed identities stranded in Mirror's spawned, observing and ownership registries; retain object/component provenance and expose recovery counters through `bot_health`.
