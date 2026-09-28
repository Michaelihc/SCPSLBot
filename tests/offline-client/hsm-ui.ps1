param($Context)
$ErrorActionPreference = 'Stop'
$actor = @(Observe | Where-Object id -eq $Context.Actor.id)[0]
if (-not $actor.ready) { throw 'Connected client is not ready' }

$warmup = (Invoke-LabServer '/bot_warmup standard') -join "`n"
if ($warmup -match 'does not exist|Unknown command') { throw "Warmup command unavailable: $warmup" }
$role = (Invoke-LabServer "/forcerole $($actor.id) ClassD") -join "`n"
if ($role -match 'does not exist|Unknown command') { throw "Role setup failed: $role" }
Invoke-LabInput @{id='bot-warmup-hud';frames=180;capture=$true} | Out-Null
Invoke-LabScreenshot -Name bot_warmup_hud | Out-Null

$status = (Invoke-LabServer '/bot_status') -join "`n"
if ($status -match 'does not exist|Unknown command') { throw "Bot status unavailable: $status" }
Invoke-LabInput @{id='bot-hud-expiry';frames=300;capture=$true} | Out-Null
Invoke-LabScreenshot -Name bot_hud_after_expiry | Out-Null

# Exercise WarmupSafezone's actual blocked-action hint in native Class-D cells.
# End the arena phase so it no longer relocates the player's native spawn.
Invoke-LabServer '/bot_warmup none' | Out-Null
$mode = (Invoke-LabServer '/bot_warmup') -join "`n"
if ($mode -notmatch 'Current warmup mode is None') { throw "Warmup arena did not stop: $mode" }
Invoke-LabServer '/roundlock on' | Out-Null
Invoke-LabServer '/forcestart' | Out-Null
Start-Sleep -Seconds 4
Invoke-LabServer "/forcerole $($actor.id) ClassD" | Out-Null
Start-Sleep -Seconds 2
$safezone = (Invoke-LabServer "safezone status $($actor.id)") -join "`n"
if ($safezone -notmatch 'ClassDCells') { throw "Class-D cells fixture was not active: $safezone" }
Invoke-LabServer "/god $($actor.id) 1" | Out-Null
Invoke-LabServer "/strip $($actor.id)" | Out-Null
Invoke-LabServer "/give $($actor.id) 13" | Out-Null # Native GunCOM15.
Invoke-LabInput @{frames=40;inputFrames=2;keys=@(49)} | Out-Null
$held = @(Observe | Where-Object id -eq $actor.id)[0].held
if ($held -ne 'GunCOM15') { throw "Native pistol was not held: $held" }
Invoke-LabInput @{id='safezone-blocked-action';frames=120;inputFrames=2;keys=@(323);capture=$true;audio=$true} | Out-Null
Invoke-LabScreenshot -Name safezone_action_blocked | Out-Null
