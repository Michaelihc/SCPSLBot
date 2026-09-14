# Tutorial Surface exemption

Date: 2026-09-05 (Asia/Shanghai)

## Change

The shared Surface role policy permits admin-assigned Tutorial. Tutorial remains excluded by the
player role-selection policy. All other Surface role restrictions retain their existing behavior.
English and Chinese user documentation and the project snapshot reflect this exemption.

## Automated verification

- Release policy tests: 101/101 passed, including Tutorial Surface permission and selection exclusion.
- SCPSLBot x64 Release build against the installed dedicated-server managed references: passed,
  zero warnings and zero errors; automatic deployment disabled.
- `git diff --check`: passed (only repository line-ending conversion notices).

## Pending live acceptance

This change has been deployed on disk as recorded below, but not activated or exercised in a running server. The existing Surface routing
scenario requires an authenticated real player for the production routing path; server dummies
are deliberately excluded by that path and cannot establish this exemption end to end.

After loading the build on the local test server, assign Tutorial through native RA while on
Surface and verify it remains there without an evacuation broadcast, including after delayed
placement callbacks. Also enter Surface as Tutorial from the facility. Confirm Tutorial is absent
from the player role dropdown and that CI/SCP Surface evacuation still works.

## Production deployment without restart

Date: 2026-09-06 (Asia/Shanghai). User explicitly authorized deployment to bot port 7777 without restart.

- Re-ran Release policy tests: 101/101 passed. Reference-aware x64 Release build: zero warnings/errors.
- Replaced only `/home/scpsl/.config/SCP Secret Laboratory/LabAPI/plugins/7777/SCPSLBot.dll`
  on `47.76.117.246`, using a verified upload and atomic file replacement.
- Local and deployed SHA-256: `4CB6B3548218B91E6F7855FF553EDF011C082AB83C89093E846CF3944D8B22AC`.
- Ownership/mode verified: `scpsl:scpsl:644`.
- Verified rollback copy:
  `/home/scpsl/.config/SCP Secret Laboratory/LabAPI/backups/7777/SCPSLBot-before-tutorial-20260905-163905.dll.bak`.
- Previous DLL SHA-256: `D12C707DD1F82DFF70D85630497B7F35635F851E3B789600A71C515C067825DF`.
- Pre-deployment service active/running; LocalAdmin PID 154153, game PID 155091.
  Native player query reported five players.
- Post-deployment service remained active/running with both PIDs unchanged, UDP 7777 bound on
  IPv4 and IPv6, and the native player query still reporting five players.
- No restart, reload, restart scheduling, or player broadcasts were requested or sent.
  The new behavior requires a future game-process restart; live acceptance remains pending.
