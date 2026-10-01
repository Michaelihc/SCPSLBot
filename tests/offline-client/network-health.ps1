param($Context)
$ErrorActionPreference = 'Stop'

function Check-Reply([string]$Command, [string]$Expected) {
    $reply = (Invoke-LabServer $Command) -join "`n"
    if ($reply -notmatch $Expected) { throw "Unexpected $Command response: $reply" }
    Write-Output $reply
}

$viewer = @(Observe -ClientsOnly)[0]
if (-not $viewer.ready) { throw 'Real viewer is not ready' }
$dummy = (Invoke-LabServer '/bot_add') -join "`n"
if (@(Observe | Where-Object dummy).Count -lt 1) { throw "No dummy was created: $dummy" }
Check-Reply 'players' 'List of players \(1\):'
Check-Reply 'bot_health_fixture spawn' 'HEALTH_FIXTURE spawned'
Start-Sleep -Seconds 2
Check-Reply 'bot_health_fixture corrupt' 'HEALTH_FIXTURE corrupted'
Start-Sleep -Milliseconds 500
Check-Reply 'bot_health_fixture assert' 'HEALTH_FIXTURE PASS'
$health = (Invoke-LabServer 'bot_health') -join "`n"
if ($health -notmatch 'registry_repairs=[1-9]\d*' -or $health -notmatch 'BotHealthFixture-Victim') {
    throw "Recovery/provenance not observed: $health"
}

# Drive reconnect through the real client's native console and verify its new hub.
$null = Invoke-LabSetup 'disconnect'
$deadline = (Get-Date).AddSeconds(20)
do {
    Start-Sleep -Milliseconds 250
    $roster = (Invoke-LabServer 'players') -join "`n"
} until ($roster -match 'List of players \(0\):' -or (Get-Date) -gt $deadline)
if ($roster -notmatch 'List of players \(0\):') { throw "Bot-only roster did not report zero humans: $roster" }
if (@(Observe | Where-Object dummy).Count -lt 1) { throw 'No dummy remains for the empty-human count check' }
$null = Invoke-LabSetup "connect 127.0.0.1:$($Context.Port)"
$deadline = (Get-Date).AddSeconds(60)
$joined = $null
do {
    Start-Sleep -Milliseconds 500
    try { $joined = @(Observe -ClientsOnly | Where-Object { $_.ready -and $_.userId -eq $viewer.userId -and $_.id -ne $viewer.id })[0] }
    catch { $joined = $null }
} until ($joined -or (Get-Date) -gt $deadline)
if (-not $joined) { throw 'Real client did not reconnect after registry corruption' }
Check-Reply 'players' 'List of players \(1\):'
Check-Reply 'bot_health_fixture assert' 'HEALTH_FIXTURE PASS'
Check-Reply 'bot_health_fixture cleanup' 'HEALTH_FIXTURE cleaned'

# Verify LocalAdmin's actual human-count gate: occupied stays up, dummy-only restarts.
$sessionReply = (Invoke-LabServer 'bot_health_fixture session') -join "`n"
if ($sessionReply -notmatch 'HEALTH_FIXTURE session=(\d+)') { throw "Missing game session ID: $sessionReply" }
$originalSession = $Matches[1]
$localAdminExe = Join-Path $Context.Root 'LocalAdmin/LocalAdmin.exe'
$presetList = (& $localAdminExe ctl $Context.Port localadmin 'restartwhen list') -join "`n"
if ($presetList -notmatch 'Custom presets are JSON files in (.+) \(name = filename\)\.') {
    throw "Missing native preset directory: $presetList"
}
$presetFolder = [IO.Path]::GetFullPath($Matches[1])
$slotPrefix = [IO.Path]::GetFullPath($Context.Root).TrimEnd('\') + '\'
if (-not $presetFolder.StartsWith($slotPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Preset directory is outside this run's slot: $presetFolder"
}
$null = New-Item -ItemType Directory -Path $presetFolder -Force
@{ edge='low-activity'; maxPlayers=0; confirmations=2; tickSeconds=1; summary='Local-only human-count gate check' } |
    ConvertTo-Json | Set-Content (Join-Path $presetFolder 'health-empty.json') -Encoding utf8
$armed = (& $localAdminExe ctl $Context.Port localadmin 'restartwhen health-empty') -join "`n"
if ($armed -notmatch 'armed|Armed') { throw "Empty-human restart gate was not armed: $armed" }
Start-Sleep -Seconds 8
$sessionReply = (Invoke-LabServer 'bot_health_fixture session') -join "`n"
if ($sessionReply -notmatch "HEALTH_FIXTURE session=$originalSession\b") { throw 'Restart gate fired with a human connected' }
Check-Reply 'players' 'List of players \(1\):'
$null = Invoke-LabSetup 'disconnect'
$deadline = (Get-Date).AddSeconds(75)
$newSession = $null
do {
    Start-Sleep -Milliseconds 500
    $sessionReply = (Invoke-LabServer 'bot_health_fixture session') -join "`n"
    if ($sessionReply -match 'HEALTH_FIXTURE session=(\d+)' -and $Matches[1] -ne $originalSession) {
        $newSession = $Matches[1]
    }
} until ($newSession -or (Get-Date) -gt $deadline)
if (-not $newSession) { throw 'Bot-only empty-human restart gate did not restart the game' }
Write-Output "HEALTH_GATE PASS occupied=$originalSession dummy_only_restart=$newSession"
$null = Invoke-LabSetup "connect 127.0.0.1:$($Context.Port)"
$deadline = (Get-Date).AddSeconds(60)
$joined = $null
do {
    Start-Sleep -Milliseconds 500
    try { $joined = @(Observe -ClientsOnly | Where-Object { $_.ready -and $_.userId -eq $viewer.userId })[0] }
    catch { $joined = $null }
} until ($joined -or (Get-Date) -gt $deadline)
if (-not $joined) { throw 'Real client did not reconnect after the empty-human restart' }
Check-Reply 'players' 'List of players \(1\):'
$intentStatus = (& $localAdminExe ctl $Context.Port localadmin 'restartwhen status') -join "`n"
if ($intentStatus -notmatch 'No restart intent is armed') { throw "One-shot intent was not consumed: $intentStatus" }
Remove-Item -LiteralPath (Join-Path $presetFolder 'health-empty.json')
$null = Invoke-LabInput @{ id='network-health-rejoined'; frames=120; dx=0.08; capture=$true }
$null = Invoke-LabScreenshot -Name 'network-health-rejoined'
