param($Context)
# StatsBots hero/footer HUD before and after a native round restart (HsmAdapter clears on WaitingForPlayers).
$ErrorActionPreference = 'Stop'
$viewer = @(Observe)[0]
if (-not $viewer.ready) { throw 'Viewer is not ready' }
$null = Invoke-LabInput @{ id='hud-before-restart'; frames=240; capture=$true }
$null = Invoke-LabScreenshot -Name 'statsbots-hud-before'

$restart = (Invoke-LabServer '/roundrestart') -join "`n"
if ($restart -notmatch 'Round restart forced') { throw "Round restart was not accepted: $restart" }
$deadline = (Get-Date).AddSeconds(90)
$rejoined = $null
do {
    Start-Sleep -Milliseconds 500
    try { $rejoined = @(Observe | Where-Object { $_.ready -and $_.userId -eq $viewer.userId -and $_.id -ne $viewer.id })[0] }
    catch { $rejoined = $null }
} until ($rejoined -or (Get-Date) -gt $deadline)
if (-not $rejoined) { throw 'Viewer did not rejoin after the round restart' }
$null = Invoke-LabInput @{ id='hud-after-restart'; frames=300; capture=$true }
$null = Invoke-LabScreenshot -Name 'statsbots-hud-after'
