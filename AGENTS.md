# SCP:SL Plugin Project Starter

**Reliability first, performance first** 
**Keep behavior predictable under load**
**Maintain clear per-player/per-client vs whole-server boundaries.**

## Terminology And Quick Reference
[Add entries only when useful.]

- RA: Remote Admin.
- RA Panel: In-game Remote Admin GUI for server management, item spawning, kicks, and related admin actions. It also has a CLI mode.
- Server-Specific Settings: SCP:SL's built-in player-facing settings panel. Prefer it over Player Console commands so it is more intuitive.
- Player Console: In-game CLI available to players. Use it for simple player-facing commands.

## Project Snapshot
- Stable release: `production-2026-09-08`, verified production SCPSLBot.dll; published source baseline `f036fb06d9b16adaa708363de2fd6bc7e5ed322f`, current source development in testing. The release tag is publication provenance, not an exact binary source snapshot.
[Concise technical overview for future agents. Overwrite stale notes instead of appending long histories.]

- Runtime suite: `SCPSLBot` 1.0.0, `WarmupSafezone` 1.0.0, `StatsBots` 1.0.0, `XPSystem` 2.0.2, and the existing `LabAPI_InfiniteAmmo` 1.0.1 plugin; `RatingTags` remains rating/tier-only with its old XP progression disabled
- SSS dependency: `ServerKeybinds.Compat` API 4; deploy its single `ServerKeybinds.dll` under the target port only, never `dependencies/global`
- Player text: HSM stable-tag providers with EN/CN config and Chinese fallback
- Dedicated local bot test port: `8888`; production bot server uses remote port `7777`
- Player SSS is stale-input hardened and role-permissive; Surface allows Facility Guard plus all four NTF ranks, while other humans evacuate to varied native RA-door targets in HCZ/EZ and SCPs evacuate to LCZ with a localized per-player broadcast
- Role/item/arena SSS dropdowns stage stable server-side selections; explicit Apply/Grant buttons execute them, and no loadout control is registered
- A round-owned global scanner retries exact-`Spectator` real-player respawns; `None`, `Destroyed`, `Overwatch`, hosts, and dummies remain ignored
- Surface PvE CI bots use exact native CI reinforcement spawnpoints; the NTF Surface anchor is player-only
- Managed bots independently clear native `SpawnProtected` after every role assignment
- During Standard warmup, real players retain native `SpawnProtected` only for their first playable respawn after a confirmed death; other player role/loadout changes clear it
- WarmupSafezone restores the configured Surface axis/threshold/minimum-X protection and visible boundary with native escape-zone fallback; the SCP-914 backing is 10x while its text remains normal-size
- Periodic native overflow cleanup is enabled by default
- Plugin target: LabAPI `net48`

## SCP:SL Plugin-Specific Principles

### General
- When starting a plugin or checking available APIs, read `..\.references\AGENTS.md` in parent folder and follow its LabAPI/decompiled lookup order.
- Treat each plugin folder as a separate product unless the user explicitly asks for shared code or a multi-plugin change.
- Prefer native game/LabAPI behavior over custom implementations unless custom behavior is specifically requested. Example: invoke the native RA ragdoll cleanup instead of manually recreating it.
- Ask before changing a request into a separate plugin/shared library, adding external dependencies, or using Harmony when a LabAPI event/wrapper might work.

### Testing
- Always follow test flow in `..\.tests` before calling work complete. If no checks exist (e.g. simple config tweaks or read only queries), say so and describe what was verified.
- Plugin-specific tests, transcripts, and screenshots should live in this plugin's `tests` folder. 
- Plugin configs are under `%APPDATA%\SCP Secret Laboratory\LabAPI\configs\<active port>\<Plugin Name>\`.
- Call out when multiplayer/manual verification is needed. Keep detailed logs, especially for client/server behavior, so kicks, disconnects, jitter, and plugin glitches can be distinguished reliably.

### UI And Text
- SCP:SL does not support custom client-side UI. For admin-facing commands, prefer RA CLI commands. For player-facing options, prefer Server-Specific Settings; use Player Console commands as a fallback.
- For player text, always use the reusable `..\.templates\HintDisplayProvider` pattern instead of direct `Player.SendHint` calls from feature code, unless explicitly asked not to. See `..\.templates\HintDisplayProvider\README.md`
- For HSM edge placement, keep hints center-anchored by default. Avoid combining HSM left/right alignment with far-edge X values; if left alignment is needed, tune an offset inside HSM's centered TMP text area.
- Keep a clear hint/text system instead of improvising message behavior per feature.

## Best Practices
- Avoid God Classes. Do not add new feature logic to an existing class only because it is the lowest-diff place to put it.
- If a change makes one class own multiple unrelated domains, split the class before adding more behavior.
- Avoid per frame updates unless they are cheap and necessary. 

### Documentation
- Create and maintain a localized (language toggle at top) user-facing `README.md` with a clear explanation of what the plugin does, how to use it, config files, player/RA commands, and known plugin conflicts.
- Keep `implementation-notes.md` current for non-trivial work: decisions, spec interpretations, intentional deviations, tradeoffs, plugin-conflict risks, and open questions. Use clear subheadings.
- Keep `## Project Snapshot` in `AGENTS.md` updated and concise.
- Cite exact source paths when explaining SCP:SL, LabAPI, or native game behavior.

### Localization
- Provide user-facing docs, UI text, hints, broadcasts, and prompts in both English and Chinese.
- Show only one language at a time. Prefer matching each player's client game language when available.
- Add a `language` config setting where `""` means match client, `"cn"` forces Chinese, and `"en"` forces English.
- Default to `language: ""`; fall back to Chinese when the client language cannot be determined.
- Keep internal development notes, `AGENTS.md`, code comments, identifiers, and CLI commands in English.
