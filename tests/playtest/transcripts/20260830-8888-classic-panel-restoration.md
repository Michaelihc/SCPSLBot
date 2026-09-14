# Port 8888 classic panel restoration — 2026-08-30

## Scope

- Restore the previous per-player `surface`, `pvpve` (HCZ/EZ), and `lcz` physical arenas.
- Restore the complete safe native player catalogs: 19 regular roles and 69 items.
- Evacuate role changes made on Surface to HCZ/EZ for humans or LCZ for SCPs; preserve exact position for role changes already inside the facility.
- Remove debug/admin options from the player-facing SSS panel.
- Drive bot composition from occupied arenas: LCZ gets an SCP; HCZ/EZ gets at least two opposing humans.
- Install the StatsSystem provider so StatsBots can persist and apply scoring on port 8888.

## Automated verification

- `dotnet build SCPSLBotAddon.sln -c Release -p:DeployToLocalServer=false`: PASS, 0 warnings, 0 errors.
- `dotnet test SCPSLBot.PolicyTests/SCPSLBot.PolicyTests.csproj -c Release`: PASS, 34/34.
- `dotnet test StatsBots.Tests/StatsBots.Tests.csproj -c Release`: PASS, 7/7.
- `dotnet test tests/performance/SCPSLBot.PerformanceChecks.csproj -c Release`: PASS, 5/5.
- `dotnet test ../stats-system/StatsSystem.StatePath.Tests/StatsSystem.StatePath.Tests.csproj -c Release`: PASS, 11/11.
- `dotnet build tests/playtest/SCPSLBot.PlaytestScenarios.csproj -c Release`: PASS, 0 warnings, 0 errors.
- `node ../.tests/lint-scenarios.js`: PASS, 0 errors (11 unrelated legacy scenario notices).
- `git diff --check`: PASS; only existing CRLF conversion notices were reported.

The new pure population checks cover LCZ SCP demand, the HCZ/EZ two-human minimum with opposing
factions, empty-server baseline behavior, and the global ten-bot cap.

## Live port 8888 evidence

- Final visible boot log: `%APPDATA%\SCP Secret Laboratory\LocalAdminLogs\8888\LocalAdmin Log 2026-08-30 14.08.40.txt`.
- UDP 8888 is owned by the running SCPSL dedicated-server process.
- SCPSLBot, WarmupSafezone, StatsSystem, StatsBots, PlaytestHarness, and both playtest assemblies enabled.
- Only the `SCPSLBot.Warmup` player block registered; `SCPSLBot.Tools` did not register.
- With no arena occupants, Standard mode created the configured three-bot baseline.
- The saved config contains 19 role entries, 69 item entries, and exactly `surface`, `pvpve`, `lcz` presets.
- The default arena for joining players is `SurfacePve`.
- The arena dropdown always displays `surface`, `pvpve`, and `lcz`; it marks the active arena through
  the native dropdown default index instead of removing it from the options.
- Arena placement uses validated native game spawnpoints only: NTF Private (Surface), SCP-939 (HCZ/EZ),
  and Class-D (LCZ). No plugin-authored coordinates or room-center offsets are used.
- Missing StatsSystem records hydrate asynchronously, are created after hydration, and trigger HUD/SSS
  refresh when the provider changes from Loading to Ready.
- StatsSystem enabled against `%APPDATA%\SCP Secret Laboratory\LabAPI\state\8888\statssystem\player_stats.json`
  and the store contains an updated real-player record.
- After the final boot, an authenticated client reconnected and deliberate `surface`, `lcz`, and `pvpve`
  selections all reached the SSS callback. LCZ reassigned a managed bot to SCP-049; HCZ/EZ produced Chaos
  Rifleman plus NTF Private; Surface restored its human-bot composition. The persisted full-UserId record
  contains `Warmup.BotKills`, `Warmup.Score`, streak, best-streak, and bot-death counters after real combat.

## Remaining client/manual gates

These need a connected, authenticated SCP:SL client because the real SSS acquisition callback excludes
dummy players and the scenario rules prohibit reflecting into plugin internals:

- Inspect the SSS panel in English and Chinese and confirm that no debug/tools controls are visible.
- Select every role/item edge entry and confirm the displayed catalog and native application behavior.
- Change role on Surface and confirm native evacuation; then change to an SCP in HCZ and confirm the exact position is preserved.
- Confirm Gate A/Gate B elevator doors are locked while all non-Surface-elevator doors retain native state.
- Occupy LCZ and HCZ/EZ and perform spatial/raycast checks that the requested SCP/human bots are grounded
  in the correct physical rooms and remain cross-arena isolated.
- Confirm an actual threshold-crossing title unlock and title selection after enough persisted bot kills.

Live bot scoring persistence and all three connected-client arena callbacks are claimed above. Grounding/
raycast placement checks and threshold-crossing title selection remain manual gates.
