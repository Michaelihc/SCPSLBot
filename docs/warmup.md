# Warmup features

SCPSLBot's warmup layer, its companions and the `bots_only` master switch that controls them.
Settings live in `LabAPI/configs/<port>/SCPSLBot/config.yml`; see [configuration](configuration.md).

## The `bots_only` master switch

```yaml
bots_only: true   # default: plain AI bots; false turns on the warmup features
```

With `bots_only: true`, these stay off:

- the warmup layer, so the effective mode is `None`: no round lock, respawns, arenas, managed bot
  population, or hazard and wave overrides;
- overflow cleanup, so corpses (SCP-049 revives, SCP-3114 disguises) and broken doors remain;
- the warmup Server-Specific Settings menu;
- WarmupSafezone and StatsBots, which stay dormant even when installed;
- the humans-only rewrite of the native `players` header, so the native output stays unchanged.

Nothing registers Server-Specific Settings, so ServerKeybinds stays idle and other plugins' menus are
untouched. Bots never target dummies SCPSLBot did not create, and the runtime navmesh stops
re-scanning the map while no bot exists.

The saved `warmup_mode` is kept, so switching `bots_only` back to `false` restores it. While the
switch is on, `bot_warmup` reports it and refuses mode changes. `bot_status` shows `bots_only=`.
Restart the server after changing the switch.

In bots-only mode you add the bots:

- `bot_add` spawns an AI bot (at most 10 at a time). Use native RA force-class to make it any role,
  SCPs included, and the AI takes over on the new role.
- Bots are removed on round restart.
- Dead bots are native spectators, so native NTF/Chaos waves can bring them back.
- Other plugins can spawn and command bots through the [plugin API](plugin-api.md).

## Standard warmup

Requires `bots_only: false`.

**Arenas and population.** Arena occupancy drives the managed bot population:

- LCZ occupancy keeps at least one SCP bot.
- HCZ/EZ occupancy keeps at least two human bots: one Foundation and one Chaos before any higher
  configured count.
- Surface keeps its classic player-factor population.
- Empty servers keep the configured baseline population.

Population-created bots stay reconciled to their role and arena. `bot_add` instead creates an
independent AI bot: RA role changes persist, because the population controller only adopts it when an
admin runs `bot_manage`.

**Arena entry and Surface rules.**

- Surface and LCZ placement use the game's native NTF Private and Class-D spawnpoints.
- HCZ/EZ entry rotates across distinct generated rooms. It uses the native named-door registry with
  the same collision-safe resolver as RA `doortp`, and falls back to SCP-939's native spawn if no door
  target is available.
- Surface PvE managed CI bots use their exact native CI reinforcement spawn.
- Real players may stay on Surface as Facility Guard or any NTF rank. Other human roles are evacuated
  to HCZ/EZ and SCPs to LCZ, each with a localized per-player broadcast.
- Only the native Gate A/Gate B Surface elevator doors receive a plugin-owned lock. It is removed
  when warmup is disabled.

**Respawns.** A round-owned service scans participating ready players every
`respawn_scan_interval_seconds`:

- Only the exact native `Spectator` role is eligible.
- A first-observed spectator respawns after `spectator_respawn_delay_ms`.
- A death from a playable role respawns after `human_respawn_delay_ms` and restores the previous role.
- Spectators are routed by their server-owned arena membership, not the spectator camera position.
- Failed native assignments stay scheduled and are retried with explicit logs.

Native spawn protection for real players is kept only on the first playable respawn after a confirmed
death. SCPSLBot clears native spawn protection on every bot it drives.

**Native waves.** Native reinforcement waves (NTF/CI main waves, mini-waves and forced waves) are
disabled during Standard warmup by default (`disable_native_respawn_waves_in_warmup: true`). Individual
player and bot respawns continue. Setting the option to `false`, switching warmup off or unloading
SCPSLBot releases the restriction without overwriting native timers or token counts.

**Tutorial.** Admin-assigned Tutorial is outside all warmup management. It keeps native spawning and
effects, adds nothing to arena population, has no warmup controls and is not protected by safezones.

## Player controls

While Standard warmup is active, Server-Specific Settings (SSS) provide personalized controls. The
menu registers only during Standard warmup and follows `bot_warmup`.

- **`Respawn as` + `Apply`.** The dropdown lists every registered native gameplay role except `None`,
  `Spectator`, `Destroyed`, `Overwatch`, `Filmmaker`, `CustomRole` and `Tutorial`. `Apply` performs
  the revalidated exact-role change. Role changes inside the facility keep the player's position.
- **`Request item` + `Grant`.** The dropdown lists the complete safe native item list. `Grant`
  rechecks full-UserId cooldowns and per-life/per-round limits before one native grant.
- **`Teleport room` + `Apply`.**
  - The dropdown stages a generated room from the native RA named-door registry.
  - Apply re-resolves the door tag with the same collision-safe calculation as RA `doortp`.
  - Surface destinations are hidden for every role. Apply rechecks the resolved zone, so a stale or
    forged selection cannot bypass that.
- **`Arena preset` + `Apply`.** Moves only that player to Surface PvE, HCZ/EZ PvPvE or LCZ SCP and
  applies the arena's default role. Selecting the active arena is a no-op for an alive player and
  respawns a spectator. A real switch commits only after the exact role and destination are verified,
  and otherwise rolls back.

How staging and refreshes behave:

- Opening or refreshing SSS performs no action. Dropdowns only stage a server-side selection; the
  explicit button executes it.
- A retained selection stays staged after it runs, so pressing the button again revalidates and
  repeats it.
- Staged selections use stable IDs tied to PlayerId, full UserId and action type. Each action type
  has its own monotonic per-user cooldown, so one action never delays another.
- Personalized refreshes are fingerprinted, targeted, debounced by 500 ms, spaced at least 2 s
  apart, and capped at six per player per minute.

Debug, bot-diagnostic and navigation-authoring tools are never sent to player SSS; they stay in Remote
Admin. StatsBots adds Display toggles and an unlocked-only title selector, with
`warmuptitle [list|none|<titleId>]` as the Player Console fallback.

## Overflow cleanup

`enable_overflow_cleanup` (with `bots_only: false`) checks loose pickups on the configured interval.
When the count grows more than `cleanup_item_threshold` above the round baseline, it does the
following, then captures a new baseline:

- runs the game's native item, corpse, blood and bullet-hole cleanup;
- runs `repair **` for all repairable doors.

## Companions

- [WarmupSafezone](../WarmupSafezone/README.md): Surface, SCP-914 and Class-D cells safezones.
- [StatsBots](../StatsBots/README.md): bot-kill stats, skill rating, titles and HUD.

Recommended native settings for a warmup server:

```yaml
auto_warhead_start_minutes: 0
dms_enabled: false
stamina_balance_use: 0
spawn_protect_enabled: true
```
