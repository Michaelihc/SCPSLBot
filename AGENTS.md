# scpsl-bot-plugin

## Repository visibility and language policy

**OSS: yes** — public on GitHub. Checked 2026-09-18.
GitHub: [Michaelihc/scpsl-bot-plugin](https://github.com/Michaelihc/scpsl-bot-plugin) (public).

- OSS means public on GitHub for this policy. Record the owning repository's status here; recheck GitHub visibility when remotes or publication status change. A nested repository has its own status.
- Maintain English and Chinese user-facing documentation and player-facing text. Show one player language at a time; prefer the client language with Chinese fallback.
- Developer instructions, code identifiers, command syntax, and host/operator documentation remain English. Preserve proper names and native labels.

**Reliability first, performance first**
**Keep behavior predictable under load**
**Maintain clear per-player/per-client vs whole-server boundaries.**

## Terminology And Quick Reference

- RA: Remote Admin.
- RA Panel: In-game Remote Admin GUI for server management, item spawning, kicks, and related admin actions. It also has a CLI mode.
- Server-Specific Settings: SCP:SL's built-in player-facing settings panel. Prefer it over Player Console commands so it is more intuitive.
- Player Console: In-game CLI available to players. Use it for simple player-facing commands.

## Project Snapshot

- Runtime suite: `SCPSLBot` 1.0.0, `WarmupSafezone` 1.0.0, `StatsBots` 1.0.0, `XPSystem` 2.0.2, and the existing `LabAPI_InfiniteAmmo` 1.0.1 plugin; `RatingTags` remains rating/tier-only with its old XP progression disabled
- SSS dependency: `ServerKeybinds.Compat` API 4; deploy its single `ServerKeybinds.dll` under the target port only, never `dependencies/global`
- Player text: HSM stable-tag providers with EN/CN config and Chinese fallback
- Dedicated local bot test port: `8888`; production bot server uses remote port `7777`
- A single shared participation policy excludes Tutorial from SCPSLBot and WarmupSafezone player management; native spawn/effects remain untouched, and arena state, population, controls, bot targeting, protection and safezone enforcement ignore it
- Player SSS is stale-input hardened and role-permissive; Surface allows Facility Guard plus all four NTF ranks, while other humans evacuate to varied native RA-door targets in HCZ/EZ and SCPs evacuate to LCZ with a localized per-player broadcast
- Role/item/room-teleport/arena SSS dropdowns stage stable server-side selections; explicit Apply/Grant buttons execute them, and no loadout control is registered
- Room teleport lists every non-Surface native RA-resolvable named door with a room-first unique label for every role; Surface is uniformly hidden and rejected on Apply, while role/item/teleport/arena actions use independent configurable per-user cooldowns (role default 6 seconds)
- A round-owned global scanner retries exact-`Spectator` real-player respawns; `None`, `Destroyed`, `Overwatch`, hosts, and dummies remain ignored
- Surface PvE CI bots use exact native CI reinforcement spawnpoints; the NTF Surface anchor is player-only
- `bot_add` creates independent AI bots whose RA role changes persist; `bot_manage`/`bot_unmanage` transfer population ownership without changing the configured maintained count
- Player teardown never recreates LabAPI wrappers, and SSS arena changes retain the authenticated callback player instead of re-resolving a recyclable numeric ID
- Managed bots independently clear native `SpawnProtected` after every role assignment
- During Standard warmup, real players retain native `SpawnProtected` only for their first playable respawn after a confirmed death; other participating-player role/loadout changes clear it
- WarmupSafezone restores the configured Surface axis/threshold/minimum-X protection and visible boundary with native escape-zone fallback; the SCP-914 backing is 10x while its text remains normal-size
- Periodic native overflow cleanup is enabled by default
- Navigation load failures retry within the owning map generation with 1/2/4/8/15-second capped backoff; recovery unblocks managed bot population and clears `nav_error`
- Default `navigation.backend: runtime` bakes a Unity navmesh per map from live physics colliders (`Navigation/Runtime`): async build, elevator links, jump-tier passage links across sealed door-less clutter connectors (capsule-probed), one navmesh area per keycard-door permission class with per-bot inventory area masks, 5-second hashed reconciliation plus event triggers, carving obstacles for blocked crossings, a per-room sample/entry index (island-filtered, doorway-area-aware anchors), and `nav status|rebuild|probe|path`; sources use the player collision mask minus door leaves/glass, hubs, ragdolls, pickups, chambers, invisible doorway blockers and the known movers (helicopter, capybara); two bake failures fall back to `authored`; the Intercom room interior does not voxelize (known limitation)
- The runtime backend requires the server assets patched by `tools/NavMeshAssetPatcher` (every Mesh readable with streamed vertex data inlined, `.bak` originals, `navmesh-asset-patch.json`); launchers, the 8891 drivers and the production deploy script `verify` before starting and refuse an unpatched server; re-apply after every game update
- `navigation.backend: authored` keeps the cell mesh for one release: room fill from floor probes, capsule-probed door-less connectors, the `nav` cell editor; behaviors consume `INavigationBackend`/`IBotNavigator`, never cells directly
- Bot movement follows string-pulled corners nudged into turns, slides around the collider ahead each tick, keeps two goal-keyed plans per bot (combat target plus door), re-plans creeping goals at most once per second, and escalates stuck recovery (nudge, jump, back-off, replan, 45-second carve, goal abandon); roam targets are drawn from reachable points and unreachable goals yield partial paths
- Gates on isolated port 8891 through `tests/playtest/tools/check_connector_survey.py`: `scpslbot-runtime-navmesh-gate`, `scpslbot-keycard-routing-survey`, the connector survey over two seeds, `scpslbot-door-survey` and `scpslbot-lifecycle`
- Bot population, AI ownership, and arena lifetime collections use managed-reference identity so native dummy destruction cannot poison pruning; released arena state is cleared even for Unity-null hubs, and population faults use throttled LabAPI logging
- Plugin target: LabAPI `net48`; `SCPSLBot` references `UnityEngine.AIModule`; `tools/NavMeshAssetPatcher` is a .NET 8 console with `AssetsTools.NET` (tools only)

## SCP:SL Plugin-Specific Principles

### General
- For API lookup, follow [../.references/AGENTS.md](../.references/AGENTS.md).
- Treat each plugin folder as a separate product unless the user explicitly asks for shared code or a multi-plugin change.
- Prefer native game/LabAPI behavior over custom implementations unless custom behavior is specifically requested. Example: invoke the native RA ragdoll cleanup instead of manually recreating it.
- Ask before changing a request into a separate plugin/shared library, adding external dependencies, or using Harmony when a LabAPI event/wrapper might work.

### Testing
- Follow [../.tests/AGENTS.md](../.tests/AGENTS.md) for relevant tests; read-only and documentation work needs no game-server launch.
- Plugin-specific tests, transcripts, and screenshots should live in this plugin's `tests` folder.
- Plugin configs are under `%APPDATA%\SCP Secret Laboratory\LabAPI\configs\<active port>\<Plugin Name>\`.
- Call out when multiplayer/manual verification is needed. Keep detailed logs, especially for client/server behavior, so kicks, disconnects, jitter, and plugin glitches can be distinguished reliably.

### UI And Text
- SCP:SL does not support custom client-side UI. For admin-facing commands, prefer RA CLI commands. For player-facing options, prefer Server-Specific Settings; use Player Console commands as a fallback.
- For player text, always use the reusable `..\.templates\HintDisplayProvider` pattern instead of direct `Player.SendHint` calls from feature code, unless explicitly asked not to. See `..\.templates\HintDisplayProvider\README.md`
- Follow [../.tests/AGENTS.md](../.tests/AGENTS.md) for HSM positioning; use Center alignment with explicit X.
- Keep a clear hint/text system instead of improvising message behavior per feature.

## Best Practices
- Avoid God Classes. Do not add new feature logic to an existing class only because it is the lowest-diff place to put it.
- If a change makes one class own multiple unrelated domains, split the class before adding more behavior.
- Avoid per frame updates unless they are cheap and necessary.

### Documentation
- Create and maintain a localized (language toggle at top) user-facing `README.md` with a clear explanation of what the plugin does, how to use it, config files, player/RA commands, and known plugin conflicts.
- Do not read or maintain `implementation-notes.*`; report decisions in the response and keep lasting behavior in the owning docs.
- Keep `## Project Snapshot` in `AGENTS.md` updated and concise.
- Cite exact source paths when explaining SCP:SL, LabAPI, or native game behavior.

### Localization
- Provide user-facing docs, UI text, hints, broadcasts, and prompts in both English and Chinese.
- Show only one language at a time. Prefer matching each player's client game language when available.
- Add a `language` config setting where `""` means match client, `"cn"` forces Chinese, and `"en"` forces English.
- Default to `language: ""`; fall back to Chinese when the client language cannot be determined.
- Keep internal development notes, `AGENTS.md`, code comments, identifiers, and CLI commands in English.
