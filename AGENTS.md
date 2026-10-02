# SCPSLBot

Read the [shared workspace instructions](../AGENTS.md) before working here, including
language, authorization and verification policy; independent repositories may not inherit them.
Use [plugin conventions](../docs/plugin-conventions.md) for shared settings, audio and UI.

LabAPI `net48` bot runtime and companion warmup plugins. Read [README](README.md) for
features, configuration and commands; source owns detailed tuning and inventories.

## Constraints

- Use one mainline `ServerKeybinds.dll` per port in the loader's plugin folder, never a second copy
  or `dependencies/global`. Build the dependency from its metarepo source project.
- Tutorial is excluded from bot/player management and safezone enforcement; preserve native spawn/effects.
- Keep bot population ownership separate from independent AI bots and player arena lifetime.
  Lifetime collections use managed-reference identity; cleanup must handle Unity-null hubs.
- Player teardown must not recreate LabAPI wrappers. Retain authenticated SSS callback players
  instead of resolving recyclable numeric IDs. Apply/Grant actions must revalidate staged selections.
- Native spawn protection and wave suppression belong to the warmup policy, not navigation.
- Behaviors consume `INavigationBackend`/`IBotNavigator`, never authored cells directly.
  Bound retries and callbacks to the current map generation.
- Runtime navigation requires `tools/NavMeshAssetPatcher` verification before server launch;
  reapply the patch after game updates. Keep the patcher's dependencies out of the runtime plugin.
- Static navigation may include static toy hierarchies; exclude non-static parents and children.

## Focused verification

Read the [Playtest contract](../.tests/Playtest/AGENTS.md) and
[scenario/driver guide](tests/playtest/README.md). Use an isolated instance selected under the
shared local-test policy; old driver port defaults do not reserve a port.

- Exercise native RA/game-console processors and observable dummy/world behavior; do not reflect into
  SCPSLBot under the movement-provider exception when SCPSLBot is the subject of the test.
- For affected SSS behavior, cover repeated Apply/Grant, transitions and death/respawn as relevant.
  Authenticated-player cases need a real client; dummy-excluded cases are not passes.
- Combat checks must include sustained fire through depletion/reload.
- Navigation changes use the existing connector/door/lifecycle drivers and asset-patcher verification.
  A successful `nav path` query proves planning only; connector acceptance requires native walks.
  Report seed-specific failures and skipped destinations separately.
