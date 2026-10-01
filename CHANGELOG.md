# Changelog

## Unreleased

- Correct the native server-console player-count header to exclude dummies and the dedicated host, keeping LocalAdmin's human-count restart gates consistent with the roster.
- Detect and recover destroyed identities stranded in Mirror's spawned, observing and ownership registries; retain object/component provenance and expose recovery counters through `bot_health`.
