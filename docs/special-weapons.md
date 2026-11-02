# Special weapons

Special weapons are bonus items that administrators grant with RA
[`special_weapon grant`](commands.md). They exist only during Standard warmup. Each one is a native
carrier item with a tracked serial, and the code lives in `BotRoundControl/Warmup/SpecialWeapons/`.

Set `special_weapons.allow_player_requests: true` to also list them in the warmup `Request item` menu.
The default is `false`.

| Weapon | Carrier | How it works |
| --- | --- | --- |
| Graviton Disruptor (`graviton-disruptor`) | `ParticleDisruptor` | Native firing, plus a gravity shockwave at the impact, recharging between shots. Real players are thrown back; bots and dummies take the damage without moving. |
| Guided Rocket Launcher (`guided-rocket`) | `GunShotgun` | Primary Use fires a slow rocket. A visible enemy near the crosshair is locked; otherwise it flies straight. Cover breaks a lock. Each launcher is consumed by its last rocket. |
| AK-12 (`ak-12`) | `GunAK` | A locked fit with the Muzzle Booster and a foregrip fires 571 rounds per minute against the AK's 510, for 0.88 times the damage per hit, so damage per second stays level. A grant also adds reserve ammo. |

The launcher needs **Primary Use** bound under Settings > Server-Specific Settings > Gameplay. The
server cannot detect an unbound key, so the equip hint explains it.

## Rules for every weapon

- Menu requests share the `high-impact` request group: 60 seconds, one per life. RA grants skip these
  limits.
- A weapon is bound to its player. It cannot be dropped, and it ends on death, on a role change and on
  leaving Standard warmup. Managed bots cannot receive or loot one.
- Attacks obey arena, duel and safezone rules. A weapon that fires without a native shot asks the same
  native pre-shot event those rules already use, through `ISpecialWeaponHost.CanFire`.
- Tutorial is outside arena management, so only native team rules decide its hits. With friendly fire
  off, two Tutorial players cannot hurt each other with these or any other weapon.

Settings live under `special_weapons` in the BotRoundControl config. A disabled weapon can't be granted
by either route. Request cooldowns and limits live in its `controls.items` entry, whose `item_id` is `special:<weapon id>`.

## Adding a weapon

1. Add a `SpecialWeaponKind` value, and a config class on `SpecialWeaponsConfig`.
2. Subclass `SpecialWeapon` in `Weapons/`. Override only the hooks the weapon needs. Keep trigger-driven
   behavior on the native firearm path where possible; set `UsesAbilityKeys` and override `OnPrimaryDown`
   only for a key-fired weapon, which must call `Host.CanFire` first and `Host.NotifyAttacked` after.
3. Register it in the `SpecialWeaponsRuntime` constructor.
4. Add a row to `SpecialWeaponCatalogDefaults.Weapons`. Existing configs gain the entry and arena
   permission on the next load.
5. Put models in `Assets/` as `<id>.mer.json`. They are embedded automatically; load them with
   `EmbeddedItemModels.Load`.

Damage must go through `Host.CanHit` and a native damage handler, so arena, duel and safezone protection
keep working. The runtime owns tracking and item lifetime; a weapon only clears its own state in
`OnItemRemoved` and `OnRoundReset`.

## Known limits

- The client rate-limits automatic fire from its own prefab, so a server cannot raise it past what a native
  attachment gives. The AK-12's 571 rounds per minute is the AK's limit.
- The launcher's one model is visible to everyone, so a player invisible through SCP-268 still shows it.
- A launcher hides the native item while out, so the player's hands read as empty to other systems and
  the character's arms stay at rest under the shouldered model.

## Testing

`BotRoundControl/tests/offline-special-weapons.ps1` is the real-client walkthrough, and
`offline-special-weapons-observer.ps1` covers the third-person view and hits on a player with
`--clients 2`. `package-special-weapons.py` builds their candidate bundle and prints the run command. It grants through
RA `special_weapon grant`; the offline client cannot press the panel's Grant button.
