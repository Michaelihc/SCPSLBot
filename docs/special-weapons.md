# Special weapons

Special weapons are bonus items players request from the warmup `Request item` menu. They exist only
during Standard warmup. Each one is a native carrier item with a tracked serial, and the code lives in
`BotRoundControl/Warmup/SpecialWeapons/`.

| Weapon | Carrier | How it works |
| --- | --- | --- |
| Graviton Disruptor (`graviton-disruptor`) | `ParticleDisruptor` | Native firing, plus a gravity shockwave at the impact, recharging between shots. Real players are thrown back; bots and dummies take the damage without moving. |
| Guided Rocket Launcher (`guided-rocket`) | `GunShotgun` | Primary Use fires a slow rocket. A visible enemy near the crosshair is locked; otherwise it flies straight. Cover breaks a lock. Each launcher is consumed by its last rocket. |

The launcher needs **Primary Use** bound under Settings > Server-Specific Settings > Gameplay. The
server cannot detect an unbound key, so the equip hint explains it.

## Rules for every weapon

- Both defaults share the `high-impact` request group: 60 seconds, one per life.
- A weapon is bound to its player. It cannot be dropped, and it ends on death, on a role change and on
  leaving Standard warmup. Managed bots cannot receive or loot one.
- Attacks obey arena, duel and safezone rules. A weapon that fires without a native shot asks the same
  native pre-shot event those rules already use, through `ISpecialWeaponHost.CanFire`.

Settings live under `special_weapons` in the BotRoundControl config. A disabled weapon leaves the menu.
Request cooldowns and limits live in its `controls.items` entry, whose `item_id` is `special:<weapon id>`.

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

- The launcher's model is visible to everyone, so a player invisible through SCP-268 still shows it.
- A launcher hides the native item while out, so the player's hands read as empty to other systems.

## Testing

`BotRoundControl/tests/offline-special-weapons.ps1` is the real-client walkthrough;
`package-special-weapons.py` builds its candidate bundle and prints the run command. RA
`special_weapon grant` stands in for the panel's Grant button, which the offline client cannot press.
