param($Context)
$ErrorActionPreference = 'Stop'
$actor = @(Observe)[0]
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
