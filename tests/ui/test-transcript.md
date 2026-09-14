# SCPSLBot HSM UI transcript

Date: 2026-08-30  
Viewport: 1920x1080  
Renderer: `../.tests/UI/render-image.js`

## Static harness

`node smoke-test.js` exited 0 and parsed 132 shared fixtures. The harness reported its known
fixture-specific calibration warnings; the SCPSLBot audit models were then rendered separately with
collision failures enabled.

## Audit models

| Model | Native background | Static validation | Layout collisions |
|---|---|---:|---:|
| Bot diagnostics (English) | `spectator-highlighted` | 0 | 0 |
| Bot diagnostics (Chinese) | `spectator-highlighted` | 0 | 0 |
| Warmup notice (English) | waiting, inventory, announcements, spawn-flash | 0 | 0 (4/4) |
| Warmup notice (Chinese) | waiting, inventory, announcements, spawn-flash | 0 | 0 (4/4) |

Generated evidence:

- `tests/ui/output/diagnostics-en-spectator-highlighted.png`
- `tests/ui/output/diagnostics-cn-spectator-highlighted.png`
- `tests/ui/output/notice-en-<background>.png`
- `tests/ui/output/notice-cn-<background>.png`

The five native backgrounds covered are waiting-for-players, inventory, announcements/stat bars,
spawn flash, and spectator. The first inventory render exposed a collision at Y=930; the notice was
moved to Y=1040 and all eight bilingual notice/background combinations then passed. The HSM models
all use center alignment with an explicit X coordinate. This is renderer validation; a visible
in-game client still needs to confirm the final TextMeshPro appearance.
