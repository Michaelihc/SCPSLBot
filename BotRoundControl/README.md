# BotRoundControl

The bot server's management plugin. Install `BotRoundControl.dll` beside `SCPSLBot.dll`,
`SCPSLBot.Components.dll` and the mainline `ServerKeybinds.dll` in the port's plugin folder.
SCPSLBot owns AI and navigation. BotRoundControl owns round policy, player respawns,
warmup arenas and populations, SSS controls, overflow cleanup and duels.

Management settings live in `LabAPI/configs/<port>/BotRoundControl/config.yml`.
The `bots_only` setting in this file disables management when true; its default is false.
AI/navigation settings remain in SCPSLBot's config. See [warmup](../docs/warmup.md).

Standard warmup pauses the native Halloween moon controller. With
`disable_warhead_in_warmup: true` (the default), warhead and DMS activation are blocked for
players, administrators and automatic triggers. Entering warmup cancels an existing countdown,
including its final ten seconds. Leaving warmup restores native moon, warhead and DMS behavior.

During Standard warmup, the zone menu lists Surface, HCZ/EZ, LCZ and **Duel / 1v1**.
Select a zone and press Apply. `.duel` / `.1v1` also enter the protected viewing deck.
The bundled arena uses native toys from the supplied model and sits 180 meters above Surface;
`duel.surface_height_offset` controls the offset (100–400 meters).

Every unpaired viewer can press E on names to select opponents, choose everyone if desired,
then select **Send invitations**. Selections belong to each viewer; the HUD shows selected IDs.
Pages cover the eligible player list. `.duel <player IDs>` sends invitations directly.
Invitations are sent as broadcasts. The first invited player to type `.accept`, `.yes`,
`.y` or `.duelaccept` forms a pair; later accepts of that offer are rejected.
An optional sender ID selects the invitation explicitly: `.accept <inviter ID>`.
Without an ID, the oldest ordinary invitation takes priority over an incoming viewer request.
Tutorial, Overwatch, SCP-079, NPCs and unauthenticated clients are excluded.

Accepted pairs enter a first-come-first-served queue. One pair fights at a time; the next
pair enters automatically when that match ends. Waiting players remain on the deck, where
their HUD shows their opponent, groups ahead, the current match and upcoming pairs.
Unpaired players can continue inviting during a match. Accepting an offer cancels both
players' other invitations and requests. Leaving, disconnecting, death or an external role
change cancels a queued pair and frees its remaining player to invite again.

Players fight as NTF and Chaos with matching E11 rifles, 100 health and 180 reserve rounds.
Damage starts after a three-second countdown. Role and inventory changes are locked during
duels and viewing; entering resets the player's loadout. Death, departure, leaving the arena
or timeout ends the duel. By default, both fighters return to the protected viewing deck;
the loser respawns there. They can invite again to join a new pair.
`.leave` exits to Surface with its normal loadout and forfeits an active duel.

Set `duel.keep_winner_in_arena: true` for king-of-the-hill. Viewers use `.challenge` to join
an individual FIFO queue; the first two entrants open an empty arena. The winner stays and
automatically fights the next challenger, while the loser returns to viewing. A defeated winner
is replaced by the surviving challenger. Both fighters receive fresh health and loadouts each match.
With no survivor (such as a timeout), the next two queued players open another match.
Accepted invitations enroll the inviter and first acceptor individually in that order; a resident
winner keeps their place. Leaving cancels only that player's queue entry. The default is `false`,
which keeps agreed pairs intact and returns both players after each match.

`.spectate`, `.view` and `.watch` toggle the protected viewing deck, including between matches.
Multiple players can view together, whether the arena is occupied or idle.
Observers cannot shoot or affect the match. Repeat any viewing alias to return to Surface.
In paired mode, viewers can also send `.challenge [arena player ID]` to a living player on the arena.
With one player on the arena the ID is optional; during a fight choose either fighter's ID.
The recipient uses `.accept [viewer ID]` to accept a request (without an ID, the oldest request).
Requests made during a fight wait until it ends and then expire after `duel.invitation_seconds`.
Death, disconnect or leaving cancels that player's requests. Accepting either invitation path
forms a pair in the same queue and clears competing invitations and requests.
`duel.invitation_seconds` bounds offers, `duel.lobby_seconds` bounds unsent opponent
selections, and `duel.match_seconds` bounds combat.
RA `duel_status` reports readiness, active players, resident winner and the paired or individual queue;
`bot_server_status` reports managed population.
SCPSLBot's `bot_status` reports AI/navigation without requiring management.

Build locally with `dotnet build BotRoundControl.csproj -c Release -p:DeployToLocalServer=false`.
Task worktrees can pass `ServerKeybindsProject`, `HsmAdapterProject` and `SL_REFERENCES`.
Committed offline walkthroughs live in [tests](tests/); use the maintained offline host runner.
