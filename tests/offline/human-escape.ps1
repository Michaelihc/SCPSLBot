param($Context)
$ErrorActionPreference = 'Stop'
# Native RA arranges roles and door anchors; the bots choose all gameplay movement and attacks.
# The real client selects the human through its native spectator-list button.
$log = Join-Path $Context.Evidence 'human-escape.log'
$samples = Join-Path $Context.Evidence 'human-escape.jsonl'
$failures = [Collections.Generic.List[string]]::new()
function Note([string]$text) { "$(Get-Date -Format HH:mm:ss.fff) $text" | Add-Content $log }
function Command([string]$text) {
    $reply = (& "$($Context.Root)/LocalAdmin/LocalAdmin.exe" ctl --quiet-ms 100 --timeout-ms 3000 $Context.Port console $text) -join "`n"
    if ($LASTEXITCODE -ne 0) { throw "Console failed: $text => $reply" }
    Note "$text => $reply"
    return $reply
}
function Snapshot {
    $reply = Command 'labobserve'
    $line = @($reply -split "`n" | Where-Object { $_.StartsWith('OFFLINE_LAB_SNAPSHOT ') })[-1]
    if (!$line) { throw 'Missing native observer snapshot' }
    return ($line.Substring(21) | ConvertFrom-Json).players
}
function RecordingState { (Invoke-LabSetup 'record-status').setupResponse | ConvertFrom-Json }
function WaitRecording([string]$phase) {
    $end = (Get-Date).AddSeconds(180)
    do {
        $state = RecordingState
        if ($state.error -or $state.phase -eq 'failed') { throw "Recording failed: $($state.error)" }
        if ($state.phase -eq $phase) { return }
        Start-Sleep -Milliseconds 200
    } while ((Get-Date) -lt $end)
    throw "Recording never reached $phase"
}
function Distance($a, $b) { [Math]::Sqrt([Math]::Pow($a.x-$b.x,2)+[Math]::Pow($a.z-$b.z,2)) }
function Spectate([string]$nick) {
    $entry = @(Get-LabUi | Where-Object { $_.type -eq 'Button' -and "$($_.text)" -match ([regex]::Escape($nick)+'(\D|$)') })[0]
    if (!$entry) { throw "Missing spectator entry: $nick" }
    $null = Invoke-LabInput @{frames=30;inputFrames=4;keys=@(323);cursorX=($entry.rect.x0+$entry.rect.x1)/2;cursorY=($entry.rect.y0+$entry.rect.y1)/2}
}

$actors = @(Snapshot)
$client = @($actors | Where-Object { !$_.dummy })[0]
$bots = @($actors | Where-Object dummy | Sort-Object id)
if ($bots.Count -lt 5) { throw 'Need five managed bots' }
$humanId = $bots[0].id
$scpId = $bots[1].id
if ((Command '/roundtime') -notmatch 'Round time:') { $null = Command '/forcestart' }
$null = Command '/decontamination disable'
$null = Command '/wave forcepause NTF true'
$null = Command '/wave forcepause Chaos true'
foreach ($bot in $bots[2..4]) { $null = Command "/forcerole $($bot.id) Spectator" }
$debug = Command '/bot_debug'
$nick = if ($debug -match "(?<![\d.])$humanId (SCPSL Round Bot \d+) role=") { $Matches[1] } else { throw 'Missing human bot nickname' }
$null = Command "/forcerole $($client.id) Overwatch"
Start-Sleep -Seconds 10
$started = $false
try {
    $null = Invoke-LabSetup 'record-start human-escape'
    $started = $true
    WaitRecording 'recording'
    $take = Get-Date
    $cases = @(
        @{name='unarmed-classd';role='ClassD';armed=$false;seconds=30;anchor='GR18'},
        @{name='armed-scientist';role='Scientist';armed=$true;seconds=30;anchor='LCZ_CAFE'},
        @{name='armed-classd';role='ClassD';armed=$true;seconds=30;anchor='GR18'},
        @{name='militant';role='FacilityGuard';armed=$false;seconds=55;anchor='GR18'}
    )
    foreach ($case in $cases) {
        $null = Command "/forcerole $scpId Spectator"
        # Native death/respawn between cases also exercises release of flee movement state.
        $null = Command "/kill $humanId"
        $null = Command "/forcerole $humanId $($case.role)"
        $null = Command "/hp $humanId 10000"
        if ($case.armed) { $null = Command "/give $humanId 13" }
        if ((Command "/doortp $humanId $($case.anchor)") -notmatch 'affected 1 player') { throw 'Human door placement failed' }
        Spectate $nick
        $null = Command "/forcerole $scpId Scp0492"
        $null = Command "/hp $scpId 10000"
        if ((Command "/doortp $scpId $($case.anchor)") -notmatch 'affected 1 player') { throw 'SCP door placement failed' }
        $first = @(Snapshot | Where-Object id -eq $humanId)[0]
        if ($first.role -ne $case.role) { throw 'Fixture role mismatch' }
        if ($case.armed -and !(@($first.items) -contains 'GunCOM15')) { throw 'Fixture firearm missing' }
        $initialShots = $first.shots
        $initialReloads = $first.reloads
        $travel = 0.0
        $fleeSeen = $false
        $combatSeen = $false
        $last = $first.position
        $nextDebug = Get-Date
        $end = (Get-Date).AddSeconds($case.seconds)
        Note "CASE $($case.name) sourceOffset=$([int]((Get-Date)-$take).TotalSeconds)"
        while ((Get-Date) -lt $end) {
            $actors = @(Snapshot)
            $human = @($actors | Where-Object id -eq $humanId)[0]
            $scp = @($actors | Where-Object id -eq $scpId)[0]
            if (!$human -or $human.role -ne $case.role -or !$scp -or $scp.role -ne 'Scp0492') { throw 'Case actor changed unexpectedly' }
            $travel += Distance $human.position $last
            $last = $human.position
            @{utc=(Get-Date).ToUniversalTime().ToString('o');case=$case.name;human=$human;scp=$scp} | ConvertTo-Json -Depth 8 -Compress | Add-Content $samples
            if ((Get-Date) -ge $nextDebug) {
                $nextDebug = (Get-Date).AddSeconds(2)
                $debug = Command '/bot_debug'
                $row = @($debug -split "`n" | Where-Object { $_ -match "^$humanId " })[0]
                $fleeSeen = $fleeSeen -or $row -match 'behavior=flee'
                $combatSeen = $combatSeen -or $row -match 'combat=human_combat'
            }
            Start-Sleep -Milliseconds 250
        }
        $shots = $human.shots-$initialShots
        $reloads = $human.reloads-$initialReloads
        Note "RESULT $($case.name) travel=$([Math]::Round($travel,1)) shots=$shots reloads=$reloads flee=$fleeSeen combat=$combatSeen"
        if ($case.name -eq 'militant') {
            if (!$combatSeen -or $shots -lt 31 -or $reloads -lt 1) { $failures.Add('Militant did not sustain fire through a reload') }
        } else {
            if (!$fleeSeen -or $travel -lt 10 -or $shots -gt 0) { $failures.Add("$($case.name) failed escape acceptance: travel=$travel shots=$shots flee=$fleeSeen") }
        }
    }
}
finally {
    if ($started) {
        $null = Invoke-LabSetup 'record-stop'
        WaitRecording 'complete'
    }
}
if ($failures.Count) { throw ($failures -join '; ') }
