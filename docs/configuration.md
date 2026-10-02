# Configuration

SCPSLBot defaults (`LabAPI/configs/<port>/SCPSLBot/config.yml`):

```yaml
bots_only: true                                # master switch; false turns on the warmup features
language: ""                                   # "en", "cn", or "" (client language, Chinese fallback)
warmup_mode: Standard                          # Standard or None
default_warmup_mode: Standard                  # fallback when warmup_mode is invalid
disable_native_respawn_waves_in_warmup: true
human_respawn_delay_ms: 1200
bot_respawn_delay_ms: 2500
spectator_respawn_delay_ms: 5000
respawn_scan_interval_seconds: 0.5
warmup_bot_count: 3                            # 0-10
warmup_bot_role: ChaosRifleman
warmup_human_role: NtfPrivate
default_warmup_arena: SurfacePve
surface_pve_bot_factor: 1.2                    # 1-2
surface_pve_max_bot_count: 6                   # 2-6
heavy_entrance_pvpve_bot_count: 2              # 2-5
light_containment_scp_bot_count: 1             # fixed at 1
disable_warhead_in_warmup: true
disable_lcz_decontamination_in_warmup: true
disable_disarming_in_warmup: true
disable_scp207_health_drain_in_warmup: true
enable_overflow_cleanup: true                  # with bots_only: false; independent of warmup_mode
cleanup_item_threshold: 80
cleanup_check_interval_seconds: 10
enable_network_registry_recovery: true
force_standard_door_connectors: false
navigation:
  backend: Runtime                             # Runtime (needs the patched server assets) or Authored
  reconcile_interval_seconds: 5
  voxel_size: 0.09
  keycard_area_routing: true
  blocked_crossing_seconds: 45
panel:
  enabled: true                                # register the warmup SSS menu (read at plugin enable)
  show_arena_preset: true
  role_change_cooldown_seconds: 6
  item_grant_cooldown_seconds: 1
  teleport_cooldown_seconds: 1
  arena_switch_cooldown_seconds: 5
```

- `bots_only` and everything it gates are described in [warmup features](warmup.md).
- `navigation` is described in [navigation](navigation.md).
- `controls` holds the item policy, native spawn-anchor overrides, the three arena presets, cooldown
  groups, allowed item roles and zones, and limits. High-impact items share a 60-second cooldown and
  are limited to one per life.
- `panel` holds the SSS presentation.
- Each product exposes `language`. See [WarmupSafezone](../WarmupSafezone/README.md) and
  [StatsBots](../StatsBots/README.md) for their configuration.

## ServerKeybinds

ServerKeybinds keeps other plugins' Server-Specific Settings beside ours by default. A server that
wants exclusive control of the menu creates `LabAPI/configs/<port>/ServerKeybinds/config.yml`:

```yaml
foreign_settings: Block
```

See the [ServerKeybinds README](https://github.com/Michaelihc/serverkeybinds#foreign-settings).
