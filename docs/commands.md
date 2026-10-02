# Remote Admin commands

| Command | Purpose | Permission |
|---|---|---|
| `bot_status` | Readiness, `bots_only`, desired/tracked/owned/independent/live bots, nav generation, faults, runner heartbeat, resources | `FacilityManagement` |
| `bot_health` | Network registry recovery counters, last repair with object/component provenance, last scan fault; also available in the server console | `FacilityManagement` |
| `bot_add` | Spawn an independent AI bot (maximum 10); RA role changes persist | `PlayersManagement` |
| `bot_manage <player ID>` | Adopt an independent bot into a maintained population slot (Standard warmup); at full population it replaces one managed bot | `PlayersManagement` |
| `bot_unmanage <player ID>` | Release a maintained bot without despawning it; the controller creates a replacement | `PlayersManagement` |
| `bot_warmup [none\|standard]` | Query or change the persisted warmup mode; refused while `bots_only` is on | Query: none; change: `PlayersManagement` |
| `bot_difficulty [easy\|normal\|hard\|hardest]` | Query or change combat difficulty (default `hardest`, not persisted) | Query: none; change: `PlayersManagement` |
| `bot_path`, `botspike ...` | Pathing and native movement diagnostics | Mutation: `PlayersManagement`; spike status: `GameplayData` |
| `botspike survey <clutter\|doors\|all\|keycard> [both\|forward]`, `botspike survey_status`, `botspike survey_stop` | Walk the spike bot natively across every door-less connector (or plain door) and log per-case `[BotSurvey]` verdicts; `keycard` asserts keycard-aware routing with path queries only | Survey: `PlayersManagement`; status: `GameplayData` |
| `nav status` | Active/configured backend, readiness, bake and reconcile diagnostics | `GameplayData` |
| `nav rebuild` | Re-bake (runtime) or re-load (authored) navigation for the current map | `ServerConfigs` |
| `nav rebuild <center xyz> <size xyz>` / `nav rebuild clear` | Add or remove one custom-map region | `ServerConfigs` |
| `nav probe [x y z\|RoomName]` | Whether a point is on the navigation surface, the nearest surface point, navmesh area / door class | `GameplayData` |
| `nav path <from> <to> [perms <hex>\|all]` | Runtime path query between points or room anchors with a permission mask | `GameplayData` |
| `nav edit\|load\|save\|vertex ...` | Authored-backend cell editor | Read: `GameplayData`; mutation: `ServerConfigs` |
| `statsbots status\|grant\|revoke <fullUserId> ...` | Inspect or administer warmup titles | configurable `statsbots.manage` |

Developer tools: `bot_tp`, `bot_position_local`, `bot_test` (RA) and `bot_raycast` (client console,
`GameplayData`).

Notes:

- StatsBots admin commands require an exact full authenticated UserId.
- With `bots_only: false`, the native server-console `players` response counts humans only,
  including those still authenticating. It excludes the dedicated host and bots, so a LocalAdmin
  "restart when empty" policy treats a server with only bots as empty.
- Network registry monitoring checks for destroyed identities before network updates and when
  connections arrive.
  - With `enable_network_registry_recovery: true` (default), it removes only destroyed entries from
    Mirror's spawned, observing and ownership registries.
  - Repairs log `[BotHealth] DESTROYED_REGISTRY_ENTRY`.
  - Setting it to `false` keeps diagnostics without mutating the registries.
