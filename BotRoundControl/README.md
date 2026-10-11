# BotRoundControl

The bot server's management plugin. Install `BotRoundControl.dll` beside `SCPSLBot.dll`,
`SCPSLBot.Components.dll` and the mainline `ServerKeybinds.dll` in the port's plugin folder.
SCPSLBot owns AI and navigation. BotRoundControl owns round policy, player respawns,
warmup arenas and populations, SSS controls, overflow cleanup and duels.

Management settings live in `LabAPI/configs/<port>/BotRoundControl/config.yml`.
The `bots_only` setting in this file disables management when true; its default is false.
AI/navigation settings remain in SCPSLBot's config. See [warmup](../docs/warmup.md).

During Standard warmup, the zone menu lists Surface, HCZ/EZ, LCZ and **Duel / 1v1**.
Select a zone and press Apply. `.duel` / `.1v1` also enter the duel lobby.
The bundled arena uses native toys from the supplied model and sits 180 meters above Surface;
`duel.surface_height_offset` controls the offset (100–400 meters).

Only one challenger reserves the arena at a time. Shoot or interact with names to toggle
opponents, choose everyone if desired, then select **Send invitations**. Pages cover the
full eligible player list. Invitations are sent as broadcasts. The first invited player
to type `.accept`, `.yes`, `.y` or `.duelaccept` claims the match; later accepts are rejected.
An optional sender ID selects the invitation explicitly: `.accept <inviter ID>`.
Without an ID, an invitation to the current arena takes priority over an incoming viewer request.
Tutorial, Overwatch, SCP-079, NPCs and unauthenticated clients are excluded.

Players fight as NTF and Chaos with matching E11 rifles, 100 health and 180 reserve rounds.
Damage starts after a three-second countdown. Role and inventory changes are locked during
duels and viewing; entering resets the player's loadout. Death, departure, leaving the arena
or timeout ends the duel and moves the remaining fighters to the protected viewing deck.
The winner and respawned loser stay there with the other viewers between matches.
`.leave` exits to Surface with its normal loadout and forfeits an active duel.

`.spectate`, `.view` and `.watch` toggle the protected viewing deck, including between matches.
Multiple players can view together. `.duel` joins viewing while the match slot is reserved;
when it is free, a viewer can use `.duel` to challenge and invite other viewers or players.
Observers cannot shoot or affect the match. Repeat any viewing alias to return to Surface.
Viewers can also send `.challenge [arena player ID]` to a living player on the arena.
With one player on the arena the ID is optional; during a fight choose either fighter's ID.
The recipient uses `.accept [viewer ID]` to accept a request (without an ID, the oldest request).
Requests made during a fight wait until it ends and then expire after `duel.invitation_seconds`.
Death, disconnect or leaving cancels that player's requests. Accepting either invitation path
claims the single match slot and clears competing invitations and requests.
`duel.invitation_seconds`, `duel.lobby_seconds` and `duel.match_seconds` bound each phase.
RA `duel_status` reports readiness and ownership; `bot_server_status` reports managed population.
SCPSLBot's `bot_status` reports AI/navigation without requiring management.

Build locally with `dotnet build BotRoundControl.csproj -c Release -p:DeployToLocalServer=false`.
Task worktrees can pass `ServerKeybindsProject`, `HsmAdapterProject` and `SL_REFERENCES`.
Committed offline walkthroughs live in [tests](tests/); use the maintained offline host runner.
