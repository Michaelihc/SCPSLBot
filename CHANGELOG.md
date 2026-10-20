# Changelog

## Unreleased

- Drive every non-combat bot through a utility brain (explore, objective, loot, heal, flee and SCP
  corpse abilities) and route all movement through one locomotion layer. Every role and every
  movement source now opens doors with a matching keycard and rides elevators, so bots cross zones
  (Surface to Entrance through the Gate elevators, LCZ and HCZ through the checkpoint elevators).
- Replace zone roaming with facility-wide exploration toward room interiors with visited-room memory.
  Bots no longer turn back at doorways in corridors; SCPs roam the whole facility.
- Stop treating walkable slopes and stair risers as walls in obstacle avoidance.
- Use native SCP abilities: SCP-049 Doctor's Call, native attack reach and corpse revival; SCP-049-2
  corpse consumption; SCP-096 charge and gate prying; SCP-106 stalk and Hunter's Atlas; SCP-939
  focus, lunge and amnestic cloud; SCP-3114 disguise, reveal and strangle.
- Swing SCP attacks at the native reach read from the role prefabs instead of fixed ranges, so
  SCP-106, SCP-939, SCP-3114, SCP-049-2 and SCP-096 strike as soon as a hit can land.
- Let SCP-106 walk through doors in a chase instead of stopping to open them.
- Make SCP-939 lunge only from door ambushes: on entering a room it sometimes waits, focused, behind
  a closed door and lunges at whoever opens it. In a chase it claws instead of focusing.
- Human bots pick up useful items with the native search time, heal with medical items when safe,
  flee when unarmed, and equip keycards for keycard lockers. Only Standard warmup issues a COM-15 to
  unarmed humans.
- Make the GOAP escape objective recover from failed actions and rejected pickups, target the nearest
  untried position, replan periodically and route straight to the exit once every border is passable.
- Add `bot_debug` and `bot_trace`, the `round_trace` setting and the `tools/round-map` renderer for
  top-down round maps.
- Pause the native Halloween moon during Standard warmup to prevent its ending sequence from
  repeatedly exploding respawned players and bots; restore it when warmup ends.
- Enforce warmup warhead suppression for administrator and server-triggered starts and detonations.
  Block DMS activation and cancel already-running countdowns, including their final ten seconds.
- Queue accepted duel pairs in arrival order while unpaired viewers continue inviting.
  Show individual selection, invitation and queue status on the HUD. Both fighters return to
  viewing by default; `duel.keep_winner_in_arena` enables king-of-the-hill with individual
  FIFO challengers and automatic winner retention.
- Accept an arena invitation with the inviter's ID as well as without an ID. Distinguish
  invitation rejection reasons in player responses and server logs. Pending viewer challenges
  no longer block accepting an invitation to the current arena without an ID.
- Keep duel fighters on the protected viewing deck after a match, alongside other viewers.
  Allow viewing between matches and invitations/rematches from the deck while keeping combat 1v1.
- Add viewer-to-fighter challenge requests alongside the arena player's existing invitations.
  Incoming challenges wait for the active 1v1 to finish and require the recipient's acceptance.
- Load the duel model with the game's shipped JSON reader so Linux Mono configuration does not
  prevent the arena and fourth zone option from becoming available.
- Split bot-server management into `BotRoundControl.dll`; SCPSLBot owns AI and navigation.
  Management configuration and commands live with BotRoundControl.
- Add the fourth Duel / 1v1 warmup zone using the supplied enclosed arena above Surface.
  World-space names support shooting or interaction, multiple/all invitations and first acceptance.
  Add `.accept` / `.yes`, protected `.spectate` / `.view` / `.watch`, and lifecycle cleanup that
  returns players to Surface and releases the single match slot.
- Add `normal_round_bot_count`: independent spectator bots enter native normal-round role allocation
  and reinforcement waves without warmup population management.
- Restrict Surface idle to Standard warmup so normal-round escape planning continues on Surface.

- Arena placement skips a position write when the spawned role's movement module is not ready yet
  (another role change in the same event); the scheduled placement retries instead of throwing.

## v2026.10.02-dce1c17

- Add the `bots_only` master switch, default `true`: plain AI bots with the warmup layer, overflow
  cleanup, the warmup SSS menu, WarmupSafezone and StatsBots off. Existing warmup servers must set
  `bots_only: false` to keep their behaviour. `bot_status` reports it and `bot_warmup` refuses mode
  changes while it is on.
- WarmupSafezone enables at `Low` load priority so it always follows SCPSLBot.
- The warmup SSS controls register only while Standard warmup runs, so ServerKeybinds leaves the native
  menu alone otherwise; `bot_warmup` switches them on and off.
- Under `bots_only`: the native `players` header is no longer rewritten, and the runtime navmesh skips
  its periodic re-scan while no bot exists.
- Bots no longer target dummies that SCPSLBot did not create.
- Restoring LCZ decontamination only undoes SCPSLBot's own override, never one set by the round,
  an admin, the warhead or another plugin.
- Commands renamed to avoid collisions: `teleport` is `bot_tp`, `raycast` is `bot_raycast` (now
  requires `GameplayData`), `plugin_test` is `bot_test`, `position_local` is `bot_position_local`;
  the `warmup` alias of `bot_warmup` is removed.
- README rewritten in English only: it now describes what the bots do, lists the optional warmup
  features with their switches, gives a bots-only configuration, and adds gameplay media.

## v2026.10.01-012cae8 (2026-10-01, production)

- Correct the native server-console player-count header to exclude dummies and the dedicated host, keeping LocalAdmin's human-count restart gates consistent with the roster.
- Detect and recover destroyed identities stranded in Mirror's spawned, observing and ownership registries; retain object/component provenance and expose recovery counters through `bot_health`.
