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

The default navmesh is embedded in `SCPSLBot.dll`. If an external navmesh is included for convenience, it should be placed as:

```text
SCPSLBot/navmesh.slnmf
```

Companion plugins should be released separately unless the release is explicitly a full warmup-server bundle.
