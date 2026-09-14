# Bot 7777 restart scheduling and Cement permissions

Date: 2026-09-06 (Asia/Shanghai).

User authorized native `rnr` and full permissions for Cement on the live bot host.

- Native player query identified Cement as `76561199167860719@steam`, already assigned owner.
- `scpsl-ctl ra rnr` returned `RNR#Server WILL restart after next round.` Called once because
  the native command toggles the scheduled restart.
- Owner had every native permission except `ServerConsoleCommands`; that missing flag is checked
  by `..\.references\Decompiled\DedicatedServer\Assembly-CSharp\CommandSystem\Commands\Shared\RestartNextRoundCommand.cs`.
- Backed up the exact RA config to
  `/home/scpsl/.config/SCP Secret Laboratory/config/7777/config_remoteadmin.txt.cement-full-perms-20260905-171851.bak`.
- Changed only `ServerConsoleCommands: []` to `ServerConsoleCommands: [owner]` in the port's
  `config_remoteadmin.txt`. Verified backup bytes and replacement bytes; retained ownership/mode.
- New config SHA-256: `CAE7B0DD6FECAAD00B8806F498680CB391AFC2AC623AC3A9586038DA1A61E22F`.
- Native `pm reload` confirmed permissions reloaded. `pm group info owner` reported permissions
  `4294967295` (all 32 native flags), kick power 255 and required kick power 255.
- Native `setgroup Cement owner` confirmed one player updated, refreshing the connected session.
- Service remained active; LocalAdmin PID 154153 and game PID 155091 were unchanged at the final
  check. No immediate restart was forced. Plugin activation/startup checks remain pending the
  scheduled round-end restart. Cement can verify permission from the RA client; do not rerun `rnr`
  just to test access because it would cancel the pending restart.
