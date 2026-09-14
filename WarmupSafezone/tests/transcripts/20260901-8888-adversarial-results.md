# 2026-09-01 port 8888 adversarial results

- `warmup-safezone-914 standard`: PASS in 26.797 s; 1 passed, 0 failed, 0 skipped,
  zero monitor violations (`20260901-024307-standard.summary.json` in the port 8888 harness run store).
- `warmup-safezone-actions standard`: PASS in 43.316 s; 1 passed, 0 failed, 0 skipped,
  zero monitor violations (`20260901-024424-standard.summary.json` in the port 8888 harness run store).
- Deterministic logic: 46/46 passed.
- Production and playtest Release x64 builds: zero warnings and zero errors.
- Scenario lint: zero errors; 11 unrelated legacy migration notices.

The first blocker probe demonstrated a real SCP bypass: SCP-173's renewable Hume Shield absorbed the
configured low health-drain ticks. `OwnedDamageRegistry.ApplyHealthDrain` now completes the configured
health portion after the native damage event. The rerun proved an actual health decrease while the SCP
stood on raycast-validated native Surface ground inside the configured blocker shell.

The rerun also proves that a cancelled role request does not clear exit protection, SCP-244 native use
is cancelled with the exact item retained, the 10x SCP-914 sign remains visual-only/non-collidable, and
dummy teardown no longer produces the delayed destroyed-`ReferenceHub` HSM exception.

The combined deployment hashes, final boot log, panel findings, and remaining real-client gate are in
`../../../tests/playtest/transcripts/20260901-8888-production-adversarial-review.md`.

