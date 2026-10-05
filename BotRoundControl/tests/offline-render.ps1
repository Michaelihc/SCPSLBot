param($Context)
$ErrorActionPreference = 'Stop'
$duelActorId = $Context.Actor.id
$duelLog = "$($Context.Evidence)/duel-render.json"
$null = Invoke-LabServer '/forcestart'
Start-Sleep -Seconds 4
$null = Invoke-LabServer "/forcerole $duelActorId NtfPrivate"
Start-Sleep -Seconds 2
$before = @(Observe -ClientsOnly)[0]
$settings = Get-LabSettings
$settings | ConvertTo-Json -Depth 12 | Set-Content "$($Context.Evidence)/duel-settings.json" -Encoding utf8
if (-not ($settings.settings | Where-Object { $_.type -eq 'SSDropdownSetting' -and ($_.options -join ' ') -match '1v1' })) {
    throw 'Real client did not receive the fourth duel zone option'
}
$null = Invoke-LabSetup '.duel'
Start-Sleep -Seconds 2
$entered = @(Observe -ClientsOnly)[0]
if ($entered.position.y -lt $before.position.y + 100) { throw 'Duel command did not place the authenticated actor above Surface' }
$null = Set-LabLook -Yaw 0 -Pitch 0
$board = Invoke-LabScreenshot -Name duel-board
$null = Set-LabLook -Yaw 0 -Pitch -15
$arenaClip = Invoke-LabInput @{id='duel-arena-render';frames=180;capture=$true;audio=$true}
$arenaImage = Invoke-LabScreenshot -Name duel-arena
# Applying the same entry twice must not reset role/life/health or spawn another arena.
$null = Invoke-LabSetup '.duel'
$again = @(Observe -ClientsOnly)[0]
if ($again.life -ne $entered.life -or $again.position.y -lt $entered.position.y - 2) { throw 'Repeated duel entry reset the participant' }
$null = Invoke-LabSetup '.leave'
Start-Sleep -Seconds 2
$returned = @(Observe -ClientsOnly)[0]
if ($returned.position.y -gt $entered.position.y - 100 -or $returned.role -ne 'NtfPrivate') { throw 'Leaving duel did not return the player to Surface' }
@{before=$before;entered=$entered;repeated=$again;returned=$returned;board=$board;arena=$arenaImage;clip=$arenaClip;checks=3} |
    ConvertTo-Json -Depth 10 | Set-Content $duelLog -Encoding utf8
