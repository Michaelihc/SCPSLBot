# Production XP migration — 2026-09-02

## Outcome

- Migrated the live `RatingTags` progression store into `XPSystem` 2.0.2 on production port 7777.
- Preserved each authenticated player's level and fractional progress within that level while converting from the RatingTags `100 × 1.16` curve to XPSystem's `35 + 15√level` curve.
- Source records: 5,613; authenticated records migrated: 5,610; ignored dummy/generated records: 3.
- Source-level validation mismatches: 0; destination-level validation mismatches: 0.
- Destination range: levels 1–33, maximum banked XP 3,077.
- Disabled only RatingTags progression. Rating/tier tags and HUD remain enabled without the old XP level/progress fields.

## Backups

- Remote pre-migration archive: `/home/scpsl/.config/SCP Secret Laboratory/LabAPI/backups/7777/xpsystem-migration-20260902-011325.tar.gz`
- Local copy: `C:\Users\Michael\Documents\repos\unity\nav-test\live-backups\player-data-20260902-011325\xpsystem-migration-20260902-011325.tar.gz`
- Archive SHA-256: `59987dabff7a5c096bcecdcd1d6a6bb6c5218c9032b253f3bce010e079216a5f`
- The archive contains the final stopped-service RatingTags data/config, converted XPSystem data, migration report, and manifest.

## Deployed artifacts

- `XPSystem.dll` SHA-256: `149402e667b4bb7e6da75befc3bdf2bd1d3321ef8cae25c1e129bebf9f86ef6b`
- `Newtonsoft.Json.dll` is port-scoped under `dependencies/7777`; SHA-256: `0f89b82c6b76816b17286951ba83201c71b6611f3e75e907552247dfc72034f2`
- `ServerKeybinds.dll` API 4 was repaired port-locally after the restart exposed an older production dependency; SHA-256: `3230b9e579c5e236920d39a14942d0beb93f9f6a36d51a7d496d88b46379ae28`.
- `StatsBots.dll` was updated to accept both API-3 and API-4 personalized-dropdown signatures; SHA-256: `aaffaaa5150370c53f03fb97ddb96eae3a28ebc3f9a5658e07d67cf089f758aa`.

## Verification

- XPSystem offline tests passed and the Release build completed with 0 warnings/errors.
- ServerKeybinds compatibility tests passed 10/10 and the Release build completed with 0 warnings/errors.
- StatsBots tests passed 7/7 and the Release build completed with 0 warnings/errors using the dedicated-server managed references.
- A first guarded deployment attempt used obsolete converter option names; rollback restored the unchanged configuration and restarted production. The corrected transaction then completed.
- Final service state: active/running, PID 133399, `NRestarts=0`.
- XPSystem 2.0.2, SCPSLBot, StatsBots, ServerKeybinds API 4, Infinite Ammo, and WarmupSafezone all loaded/enabled.
- StatsBots logged successful registration of its personalized title selector and five display controls.
- SCPSLBot logged the global spectator scanner, bot spawn-protection clearing, and overflow-cleanup baseline.
- `xp flush` returned the typed durable receipt with 457,536 bytes and SHA-256 `d63cd3503632b31addaa647a5f91ee7223ee7db70453a9e2ef149ed544f8df2f`.
- Production contained only the three managed bots after the final restart.

Manual multiplayer verification is still appropriate for XP award broadcasts, level-up presentation, and persisted SSS interaction from a real client.
