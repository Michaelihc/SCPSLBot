# Port 8888 Surface safezone restoration — 2026-09-02

## Scope

- Restore the original configured Surface safezone (`axis=z`, threshold `-17`, `less_than=false`, minimum X `91`) and its two-sided visible boundary wall.
- Retain current native `Map.EscapeZones` as an additional protected fallback.
- Keep the SCP-914 panel backing at 10x while restoring its text to the original `0.12` scale.

## Static verification

- `dotnet build WarmupSafezone/WarmupSafezone.csproj -c Release -p:Platform=x64 -p:DeployToLocalServer=false`: passed, 0 warnings, 0 errors.
- `dotnet run --project WarmupSafezone/tests/WarmupSafezone.LogicTests.csproj -c Release`: passed, 54/54.
- `dotnet build WarmupSafezone/tests/WarmupSafezone.Playtests.csproj -c Release`: passed, 0 warnings, 0 errors.
- `git diff --check -- WarmupSafezone implementation-notes.md`: passed.

Release plugin SHA-256: `839bc62c1fe35765b64b4d4d91efdc3af7e7303f7486d420d858df15788adc1f`

Playtest plugin SHA-256: `5dca3b44d0270e5c5a96ece0ba824025f1095f8b222adeefb23d4f95a9fcab24`

## Live port 8888 verification

Commands:

```text
ptest reload
ptest run warmup-safezone-914 standard
```

Result:

```text
RESULT scenario=warmup-safezone-914 outcome=PASS duration=26.42s
RUN_RESULT level=standard passed=1 failed=0 skipped=0
```

The live scenario verified:

- two non-collidable Surface wall faces at the restored default threshold around `z=-17`;
- three normal-scale Surface labels at the restored wall;
- an SCP blocker actor settled on native ground inside the restored approach band;
- two SCP-914 backing faces at scale `(11.5, 5.5, 0.25)`;
- both SCP-914 text faces remained at normal scale `(0.12, 0.12, 0.12)`;
- protected damage, immediate exit protection, native spawn-protection isolation, and cleanup still passed.

Harness summary:

`%APPDATA%\SCP Secret Laboratory\LabAPI\configs\8888\PlaytestHarness\runs\20260902-014145-standard.summary.json`

## Local dependency correction

The stale global `ServerKeybinds.dll` was recoverably moved to `dependencies/global/ServerKeybinds.dll.disabled-20260902`. Port 8888 now loads the required API-4 build only from `dependencies/8888`, matching the repository deployment contract.

Port 8888 was left running after the successful scenario. Production was not changed by this verification.
