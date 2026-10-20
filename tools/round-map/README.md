# Round map

Records a round from the SCPSLBot plugin and renders it as one self-contained top-down HTML map
(per-zone panels, navmesh floor, player trails, deaths/revives/escapes, time slider).

## Record

- Every round: set `round_trace.enabled: true` in the plugin config (`sample_interval_seconds`, default
  1.0, is clamped to 0.25-30).
- On demand: RA command `bot_trace start|stop|status` (needs the FacilityManagement permission).
  `start` records the current round, or the next one if it has not begun; it stops at round end,
  restart or `bot_trace stop`.

Files go to `<LabAPI plugins>/<port>/SCPSLBot/traces/round-<yyyyMMdd-HHmmss>.jsonl` (the plugin's
data folder, next to `navmesh.slnmf`). One file per round; it is flushed every ~5 s, so a live file
can be rendered, and a truncated last line is ignored.

## Render

```
python tools/round-map/render_round_map.py round-20260101-120000.jsonl -o round.html
```

Python 3 standard library only; open the HTML in any browser (no network needed).
Mouse wheel zooms a panel, drag pans, double-click resets. Click a player in the legend to focus them.

`synthetic_trace.py <out.jsonl> [--no-nav]` writes a small fake trace for checking the renderer.

## Trace format (JSONL, one object per line, `t` is the line type)

| `t` | Fields |
| --- | --- |
| `header` | `version`, `utc`, `reason`, `port`, `seed`, `mapAge`, `roundTime`, `interval`, `roles` (`{roleId: [name, team]}`), `rooms` (`name`, `zone`, `shape`, `pos`, `rotY`, `bounds.center/size`) |
| `nav` | `scale` (10), `v` flat `x,y,z` vertices in 0.1 m units, `i` triangle indices; only when the runtime navmesh was ready |
| `player` | `time`, `id`, `nick`, `isBot` (any dummy), `managedBot` (SCPSLBot-owned), `role`, `roleId`, `team`; first sighting of an id |
| `s` | `time`, `p`: `[[id, x, y, z, roleId], ...]` for alive first-person-control players |
| `role` | `time`, `id`, `role`, `roleId`, `team`, `reason` (`Revived`, `Escaped`, ...), optional `pos` |
| `death` | `time`, `id`, `attackerId` (-1 if none), `pos`, `role`, `cause` |
| `left` | `time`, `id` |
| `round_end` | `time`, `leadingTeam` |

`time` is seconds on the native round clock. Positions are world meters with one decimal.
