# Deployment Notes

These notes are for operating this fork locally and on the live warmup server.

## Repositories

Use this repository for SCPSLBot:

```text
C:\Users\Michael\Documents\repos\unity\nav-test\scpsl-bot-plugin
```

Do not deploy from old Halloween/legacy copies. The adjacent workspace is used only for companion plugins:

```text
C:\Users\Michael\Documents\repos\unity\scpsl plugin
```

## Build SCPSLBot

```powershell
$env:SL_REFERENCES = 'C:\Program Files (x86)\Steam\steamapps\common\SCP Secret Laboratory Dedicated Server\SCPSL_Data\Managed'
dotnet build .\SCPSLBot\SCPSLBot.csproj -c Release -p:Platform=x64
```

Deploy these outputs:

```text
SCPSLBot\bin\x64\Release\net48\SCPSLBot.dll
SCPSLBot.Components\bin\x64\Release\net48\SCPSLBot.Components.dll
```

Keep `0Harmony.dll` in the plugin folder.

## Runtime navmesh asset patch (required for `navigation.backend: runtime`)

The dedicated server ships its collider meshes unreadable with streamed vertex data; Unity's runtime
navmesh builder then sees almost nothing. Patch once per game update and verify before every start:

```powershell
dotnet build .\tools\NavMeshAssetPatcher\NavMeshAssetPatcher.csproj -c Release
dotnet .\tools\NavMeshAssetPatcher\bin\Release\net8.0\NavMeshAssetPatcher.dll patch  --server "<server root containing SCPSL_Data>"
dotnet .\tools\NavMeshAssetPatcher\bin\Release\net8.0\NavMeshAssetPatcher.dll verify --server "<server root containing SCPSL_Data>"
```

`patch` rewrites `SCPSL_Data/globalgamemanagers.assets`, `resources.assets` and `sharedassets*.assets`
(all 3167 meshes readable, 84 streamed meshes inlined, about 5 MB larger in total), keeps `.bak`
originals, and writes `SCPSL_Data/navmesh-asset-patch.json` (tool version, Unity version,
Assembly-CSharp hash, per-file hashes). `verify` exits 0 when the live files match the manifest, 2 when
a game update or Steam validation restored the originals (re-run `patch`), 1 on error. `restore`
puts the originals back. Client installs are never touched.

For the Linux production host publish the self-contained patcher with
`tools\Publish-NavMeshAssetPatcher.ps1` and ship `tools/NavMeshAssetPatcher/` inside the deployment
package; `tools/Deploy-BotProduction7777.sh` runs `verify` against `SCPSL_SERVER_ROOT`
(default `/home/scpsl/scpsl`) and refuses to activate an unpatched server. Apply the patch on the host
with the service stopped:

```bash
sudo -u scpsl /path/to/NavMeshAssetPatcher patch --server "$SCPSL_SERVER_ROOT"
```

## Local 7790 Paths

Local LabAPI plugin folder:

```text
%APPDATA%\SCP Secret Laboratory\LabAPI\plugins\7790
```

Local gameplay config:

```text
%APPDATA%\SCP Secret Laboratory\config\7790\config_gameplay.txt
```

Local SCPSLBot config:

```text
%APPDATA%\SCP Secret Laboratory\LabAPI\configs\7790\SCPSLBot\config.yml
```

Recommended local gameplay settings:

```yaml
auto_warhead_start_minutes: 0
dms_enabled: false
stamina_balance_use: 0
spawn_protect_enabled: true
```

Keep spawn protection enabled through server config.

## Live 7777 Paths

Live SSH:

```powershell
ssh -i "$env:USERPROFILE\.ssh\codex-scpsl-test-key" -o IdentitiesOnly=yes root@47.76.117.246
```

Live service:

```text
scpsl-warmup.service
```

Live LabAPI plugin folder:

```text
/home/scpsl/.config/SCP Secret Laboratory/LabAPI/plugins/7777
```

Live gameplay config:

```text
/home/scpsl/.config/SCP Secret Laboratory/config/7777/config_gameplay.txt
```

Live SCPSLBot config:

```text
/home/scpsl/.config/SCP Secret Laboratory/LabAPI/configs/7777/SCPSLBot/config.yml
```

Required live gameplay settings for warmup:

```yaml
auto_warhead_start_minutes: 0
dms_enabled: false
stamina_balance_use: 0
spawn_protect_enabled: true
```

## 30 Second Deployment Warning

Before restarting live, send a visible 30 second warning to players.

Do not rely on writing `bc ...` or `broadcast ...` to the LocalAdmin pipe. On live `7777`, LocalAdmin rejected both commands as unknown.

Do not rely on `bots updatewarning ...` through the LocalAdmin pipe either. That command existed in legacy plugin code, but was not executable from the live pipe during the 2026-05-17 deployment.

Known working implementation source:

```text
C:\Users\Michael\Documents\repos\unity\scpsl plugin\ScpslPluginStarter\WarmupSandboxPlugin.cs
BroadcastLiveUpdateWarning(...)
```

The important behavior is direct per-player broadcast:

```csharp
player.ClearBroadcasts();
player.SendBroadcast(broadcastText, broadcastDuration, Broadcast.BroadcastFlags.Normal, true);
```

If the legacy `ScpslPluginStarter.dll` is disabled, port or keep this direct broadcast implementation in a currently loaded plugin such as `AdminGlobalBroadcast` before relying on future restart warnings.

Minimum live restart flow:

1. Confirm a working direct broadcast warning path exists.
2. Send a 30 second warning.
3. Wait at least 30 seconds.
4. Copy DLLs.
5. Restart `scpsl-warmup.service`.
6. Check server logs for plugin load errors.

## Plugin Folder Rules

Do not deploy `ScpslPluginStarter.dll` with this fork unless it is intentionally being tested. It contains old warmup/player-panel behavior and causes duplicate Server Specific Settings menus alongside `WarmupPlayerPanel.dll`.

On live `7777`, the duplicate menu was fixed by renaming:

```text
ScpslPluginStarter.dll
ScpslPluginStarter.pdb
```

to disabled backup filenames and restarting `scpsl-warmup.service`.

Do not remove existing companion/audio plugins during SCPSLBot deploys. Current warmup setups may include:

```text
WarmupPlayerPanel.dll
WarmupSafezone.dll
AdminGlobalBroadcast.dll
user-uploaded audio plugin DLLs
```

Only replace the DLLs that are part of the deployment being performed.

## GitHub Release Package

The SCPSLBot release package should include:

```text
SCPSLBot.dll
SCPSLBot.Components.dll
0Harmony.dll
README.md
DEPLOYMENT_NOTES.md
```

The default authored navmesh is embedded in `SCPSLBot.dll` (used only with `navigation.backend: authored`). If an external navmesh is included for convenience, it should be placed as:

```text
SCPSLBot/navmesh.slnmf
```

The runtime backend (default) needs no navmesh file but needs the patched server assets; ship the
published `tools/NavMeshAssetPatcher/` folder with the package so operators can re-apply after game updates.

Companion plugins should be released separately unless the release is explicitly a full warmup-server bundle.
