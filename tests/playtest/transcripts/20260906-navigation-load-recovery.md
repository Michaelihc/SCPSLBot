# Navigation load recovery and production RNR

Date: 2026-09-06 (Asia/Shanghai).

## Change

`SCPSLBot/Navigation/NavigationSystem.cs` now retries failed navigation installation/loading/linking
in one generation-owned worker. Delays are 1, 2, 4, 8, then 15 seconds, capped indefinitely until
success or lifecycle cancellation. Partial topology is cleared on failure. Successful loading
publishes readiness and clears the diagnostic error, allowing the existing population reconciler
to resume. Native network/map readiness checks remain required. First failure includes its stack;
LabAPI console logs expose `NAV_LOAD_RETRY` and `NAV_LOAD_RECOVERED`.

## Verification

- Release x64 SCPSLBot build: passed, zero warnings/errors. Playtest Release build: same.
- Existing policy regression tests: 108/108 passed.
- Shared scenario lint: zero errors, 11 unrelated legacy entries. Its scanner covers shared
  harness directories; the new plugin-local scenario was separately checked to use RA commands,
  public world state/events and existing grounding probes, with no runtime reflection.
- `git diff --check`: passed; Python test/deploy scripts compile.
- Initial isolated run was intentionally stopped: Unity Debug messages were not visible in
  LocalAdmin output, preventing the external driver from observing retry progress. The final
  implementation uses LabAPI logging and the accepted run below uses the final DLL.

Automated isolated port 8891: `tests/playtest/tools/check_navigation_recovery.py` temporarily
deploys the final Release assembly and locks the existing mesh file with Windows exclusive sharing.
At 23:26:14, the scenario observed `network_ready=True`, `nav_ready=False`, `live=0`, `desired=4`,
`arenas=surface:4`, `nav_generation=2`, and a real file-sharing IOException in `nav_error`.

Retry logs at 23:26:09, :10, :12, :16, :24, :39 proved 1/2/4/8/15/15-second backoff. After the sixth
failure, the driver released the lock. At 23:26:54 navigation recovered in generation 2. Within
0.5 seconds the native/world population was 4/4 live and `nav_error=none`. Ground raycasts passed.
The same scenario then canceled one role repair, observed retry healing, killed a maintained bot
through native RA, observed its Death event and live desired-role recovery, and verified settling.

- Runtime: **1/1 passed**, zero failed/skipped/violations; 44.17-second scenario.
- Full log: `tests/playtest/artifacts/navigation-recovery-20260906-232601/la8891.log`.
- Summary: `tests/playtest/artifacts/navigation-recovery-20260906-232601/20260906-232614-standard.summary.json`.
- Event evidence: corresponding `20260906-232614-standard.jsonl` in the same directory.
- Driver stopped its exact LocalAdmin and child and restored the test port's plugin/config files.
- Lifecycle cancellation and mid-round initialization share the inspected generation-owned path;
  they were not separately fault-injected in this acceptance. Authenticated multiplayer/client
  behavior was not changed or tested by these dummy probes.

## Production deployment

User explicitly requested production deployment and arming native RNR. Before deployment the live
server was healthy (`nav_ready=True`, 3/3 live), so the original intermittent incident is not proven
to have been a navigation failure.

Used `tools/Deploy-NavigationRecovery.py` on `scpsl-warmup-hk` (`47.76.117.246`) to verify the uploaded
payload, create and read-verify a rollback ZIP, and atomically replace only
`/home/scpsl/.config/SCP Secret Laboratory/LabAPI/plugins/7777/SCPSLBot.dll`, retaining scpsl ownership
and mode 644. No companion DLL or production config was changed.

- Previous SHA-256: `7718bd0e454805c6a497f1f65e0fa58437876eadf725fdbbee9d8c0b988a220b`.
- Deployed SHA-256: `821d5978bd70ed3533b21a182c601aff5571628b5354a831e8c8f937251a3790`.
- Verified rollback archive:
  `/home/scpsl/.config/SCP Secret Laboratory/LabAPI/backups/7777/navigation-recovery-20260906-152752.zip.bak`.
- Read-only `scpsl-ctl ra nextround` first reported normal round restart. Called `scpsl-ctl ra rnr`
  once, receiving `RNR#Server WILL restart after next round.` Subsequent read-only `nextround`
  confirmed `NEXTROUND#Server will RESTART.` Do not toggle RNR again to check it.
- Final 15:28:24 UTC status: service active, LocalAdmin PID 154153 and game PID 157548 unchanged;
  network/navigation ready, 3/3 bots live, no navigation/reconcile/AI faults. Final on-disk hash
  matches the accepted test artifact.

Activation remains pending the scheduled game-process restart at round end. No immediate round or
service restart was forced. The final healthy status describes the still-running previous assembly;
production startup acceptance of the new assembly follows the scheduled restart.
