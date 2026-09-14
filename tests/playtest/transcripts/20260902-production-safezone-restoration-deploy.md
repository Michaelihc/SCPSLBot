# Production Surface safezone restoration deployment — 2026-09-02

## Outcome

- Atomically installed the port-8888-verified `WarmupSafezone.dll` into production port 7777.
- Production artifact SHA-256: `839bc62c1fe35765b64b4d4d91efdc3af7e7303f7486d420d858df15788adc1f`.
- Preserved the previous production DLL as `/home/scpsl/.config/SCP Secret Laboratory/LabAPI/backups/7777/WarmupSafezone.pre-surface-restore-20260902-022115.dll.bak`.
- Rollback DLL SHA-256: `d8e90b230048c17828b2c8a7967e6c97ab51b57c5fdfe88ddfefd3d8b6694fd0`.

## Safety and activation

Production initially had one real player connected alongside the three managed bots. The artifact was first replaced on disk without restarting the service. After explicit restart authorization, Remote Admin command `serveraudio say` sent a bilingual 35-second broadcast to all four visible players and the service restart began after the required 30-second warning period.

The restart changed the service PID from `133399` to `134572`. Production returned `active/running`, UDP 7777 rebound on IPv4 and IPv6, and `NRestarts=0`. The post-restart player list contains only the three managed bots.

LabAPI loaded and enabled `WarmupSafezone` from the deployed hash and logged:

```text
[WarmupSafezone] Enabled enabled=True; configured Surface safezone restored with native Map.EscapeZones fallback.
```

SCPSLBot, StatsSystem, StatsBots, XPSystem, ServerKeybinds API 4, HintServiceMeow, and Infinite Ammo also enabled. No post-start WarmupSafezone exception was logged.

## Production configuration check

The existing production configuration already matches the restored default geometry:

```yaml
enabled: true
scp914_safezone_enabled: true
safezone_visuals_enabled: true
surface_escape_safezone_max_z: -17
surface_escape_safezone_axis: z
surface_escape_safezone_less_than: false
surface_escape_safezone_min_x: 91
```

The exact deployed build previously passed 54/54 deterministic tests and the live `warmup-safezone-914` scenario on isolated port 8888.

## Separate observation

Before the restart, the production console emitted a repeating pre-existing `StatsBotsRuntime.RefreshHud` stale-`ReferenceHub` `NullReferenceException`. It stopped after the restart. The only post-restart error was an unrelated existing duplicate `getleaderboard` command registration between loaded stats plugins.
