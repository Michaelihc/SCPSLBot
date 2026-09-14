# Local test deployment — 2026-09-15

Target: local port 8888. Runtime source commit: `8260863`.

- Committed all pending files in the active SCPSLBot repository before deployment.
- Release x64 solution build: success, zero errors, eight obsolete Unity NavMesh API warnings.
- Policy tests: 160 passed, zero failed/skipped. Scenario lint: zero errors; 11 pre-existing legacy entries in the shared test suite.
- Asset patch verification: passed, five patched server asset files, tool 1.1.0.
- Isolated 8891 runtime navmesh gate: 1 passed, zero failed/skipped.
- Isolated 8891 lifecycle suite: 5 passed, zero failed, 1 real-client scenario skipped.
- Backed up and hash-verified six port-local DLL replacements: SCPSLBot, SCPSLBot.Components, WarmupSafezone, StatsBots, ServerKeybinds.Compat, and SCPSLBot.PlaytestScenarios.
- SCPSLBot.dll SHA-256: `8d2ba9568a3c8c8dab1cecbdfd1ef937c7582c71e00d7033a5bb93b4fa1cf8a9`.
- Started maintained LocalAdmin PID 55992; game PID 56864 bound UDP 8888 on IPv4 and IPv6.
- Native `bot_status`: 4 desired/live/owned bots in Surface, runtime navigation ready, zero bake failures, no spawn/reconciliation/AI/navigation fault.
- Native `nav status`: 98 indexed rooms, zero uncovered rooms, 11 elevator links, one passage link, eight door permission classes, 794 ms bake.
- Startup log inspection found no `[ERROR]` or `Exception` records. Existing owner entry and configs retained; automatic playtest execution on 8888 remains disabled.
- Real-client UI, elevator travel, and multiplayer keycard behavior still need manual verification. Server remains running for that check.

Evidence under `tests/playtest/artifacts/`:

- `local-deploy-20260915-build.log`
- `local-deploy-runtime-gate-20260915-20260915-000819/`
- `local-deploy-lifecycle-20260915-20260915-000852/`
- `local-deploy-20260915-8888/` (backups, deployment hashes, startup logs and LocalAdmin PID)

No production deployment or Git push was performed. Older separate repositories/worktrees were not changed.
