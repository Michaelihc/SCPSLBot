param($Context)
$ErrorActionPreference = 'Stop'
# One SCPSLBot bot plays each SCP in turn against four human bots placed at native door anchors
# (doortp). The real client spectates the SCP bot in Overwatch through a native click on its
# spectator-list entry, and one uncut take records every segment. Native ability events are
# logged server-side by SCPSLBot.PlaytestScenarios ([ScpAbilityEvent]).
$log = Join-Path $Context.Evidence 'scp-abilities.log'
function Note([string]$text) { "$(Get-Date -Format HH:mm:ss) $text" | Add-Content $log -Encoding utf8 }
function Reply([string]$command) { $reply = "$(Invoke-LabServer $command)"; Note "$command => $reply"; $reply }
function RecordingState { (Invoke-LabSetup 'record-status').setupResponse | ConvertFrom-Json }
function WaitRecording([string]$phase, [int]$seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    do {
        $state = RecordingState
        if ($state.error -or $state.phase -eq 'failed') { throw "Continuous recording failed: $($state.error)" }
        if ($state.phase -eq $phase) { return }
        Start-Sleep -Milliseconds 200
    } while ((Get-Date) -lt $deadline)
    throw "Continuous recording did not reach $phase"
}

$client = @(Observe -ClientsOnly)[0]
function Bots { @(Observe | Where-Object { $_.id -ne $client.id } | Sort-Object id) }
$deadline = (Get-Date).AddSeconds(60)
while (@(Bots).Count -lt 5 -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 1 }
$bots = @(Bots)
if ($bots.Count -lt 5) { throw "Expected 5 SCPSLBot dummies, found $($bots.Count)" }
$scp = $bots[0]
$humans = @($bots[1..4])

# Idempotent so a recovery continuation can rerun the script on a started round.
if ((Reply '/roundtime') -notmatch 'Round time:' -and (Reply '/forcestart') -notmatch 'Forced round start') { throw 'Round did not start' }
$deadline = (Get-Date).AddSeconds(20)
while ((Reply '/roundtime') -notmatch 'Round time:' -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 1 }
$null = Reply '/decontamination disable'
$null = Reply '/wave forcepause NTF true'
$null = Reply '/wave forcepause Chaos true'
$debug = Reply '/bot_debug'
$scpNick = if ($debug -match "(?<![\d.])$($scp.id) (SCPSL Round Bot \d+) role=") { $Matches[1] } else { throw "No bot_debug line for $($scp.id)" }
Note "scp bot id=$($scp.id) nick=$scpNick humans=$(($humans | ForEach-Object { $_.id }) -join ',')"

# Door anchors differ per seed; resolve each preference against the native list.
$doorReply = Reply '/doorslist'
$doors = @([regex]::Matches($doorReply, '[A-Z0-9][A-Z0-9_]{1,30}') | ForEach-Object { $_.Value } | Sort-Object -Unique)
function Door([string[]]$preferred) {
    foreach ($name in $preferred) { if ($doors -contains $name) { return $name } }
    $lcz = @($doors | Where-Object { $_ -match 'LCZ|^914$|^GR18$|^330$' })
    if ($lcz.Count) { return $lcz[0] }
    throw "None of $($preferred -join ',') exist; doors=$($doors -join ',')"
}

function Place($player, [string]$role, [string]$door) {
    $null = Reply "/forcerole $($player.id) $role"
    Start-Sleep -Milliseconds 600
    $null = Reply "/doortp $($player.id) $door"
}

function Spectate {
    $ui = Get-LabUi
    $pattern = [regex]::Escape($scpNick) + '(\D|$)'
    $entry = @($ui | Where-Object { $_.type -eq 'Button' -and "$($_.text)" -match $pattern })[0]
    if (-not $entry) { Note "no spectator-list entry for $scpNick"; return $false }
    $x = ($entry.rect.x0 + $entry.rect.x1) / 2
    $y = ($entry.rect.y0 + $entry.rect.y1) / 2
    $null = Invoke-LabInput @{id='spectate-scp';frames=30;inputFrames=4;keys=@(323);cursorX=$x;cursorY=$y}
    return $true
}

# doortp to LCZ_ARMORY lands inside the keycard armory, so no anchor uses it.
$segments = @(
    @{ role='Scp049'; scpDoor=@('914'); doors=@(@('914'), @('LCZ_WC'), @('LCZ_CAFE'), @('GR18')); roles=@('ClassD','Scientist','FacilityGuard','ClassD'); seconds=150 },
    @{ role='Scp096'; scpDoor=@('LCZ_CAFE'); doors=@(@('LCZ_CAFE'), @('LCZ_WC'), @('GR18'), @('914')); roles=@('ClassD','ClassD','Scientist','FacilityGuard'); seconds=120 },
    # Unnamed doors keep closing, so the chase runs into closed doors that SCP-106 walks through.
    @{ role='Scp106'; scpDoor=@('914'); doors=@(@('914'), @('LCZ_WC'), @('GR18'), @('LCZ_CAFE')); roles=@('ClassD','Scientist','ClassD','Scientist'); seconds=120; closeDoors=$true },
    # A Surface target needs no line of sight: SCP-106 chases a Class-D locked in the nuke building.
    @{ role='Scp106'; scpDoor=@('SURFACE_GATE'); doors=@(@('SURFACE_NUKE')); roles=@('ClassD'); seconds=60; lockDoor='SURFACE_NUKE'; spectators=$true },
    # SCP-939 lunges only from door ambushes, which it starts on some room entries.
    @{ role='Scp939'; scpDoor=@('GR18'); doors=@(@('GR18'), @('914'), @('LCZ_CAFE'), @('LCZ_WC')); roles=@('ClassD','Scientist','ClassD','Scientist'); seconds=240 },
    @{ role='Scp3114'; corpse=$true; scpDoor=@('LCZ_CAFE'); doors=@(@('LCZ_CAFE'), @('LCZ_ARMORY'), @('914'), @('GR18')); roles=@('ClassD','Scientist','ClassD','FacilityGuard'); seconds=150 },
    @{ role='Scp0492'; corpse=$true; hurt=$true; scpDoor=@('914'); doors=@(@('914'), @('LCZ_WC'), @('GR18'), @('LCZ_ARMORY')); roles=@('ClassD','ClassD','Scientist','ClassD'); seconds=60 },
    @{ role='Scp173'; scpDoor=@('LCZ_WC'); doors=@(@('LCZ_WC'), @('LCZ_CAFE'), @('914'), @('GR18')); roles=@('ClassD','Scientist','ClassD','FacilityGuard'); seconds=90 }
)

$null = Reply "/forcerole $($client.id) Overwatch"
# The Overwatch splash ignores list clicks until it fades.
Start-Sleep -Seconds 10
$started = $false
$take = $null
try {
    Invoke-LabSetup 'record-start scp-abilities' | Out-Null
    $started = $true
    WaitRecording 'recording' 45
    $take = Get-Date
    Note 'recording started'
    foreach ($segment in $segments) {
        $scpDoor = Door $segment.scpDoor
        for ($i = 0; $i -lt $humans.Count; $i++) {
            if ($i -lt $segment.roles.Count) { Place $humans[$i] $segment.roles[$i] (Door $segment.doors[$i]) }
            elseif ($segment.spectators) { $null = Reply "/forcerole $($humans[$i].id) Spectator" }
        }
        if ($segment.lockDoor) {
            $null = Reply "/close $($segment.lockDoor)"
            $null = Reply "/lock $($segment.lockDoor)"
        }
        if ($segment.corpse) {
            # The first human dies at the SCP's anchor so a fresh corpse is waiting there.
            $null = Reply "/kill $($humans[0].id)"
        }
        Place $scp $segment.role $scpDoor
        if ($segment.hurt) { $null = Reply "/hp $($scp.id) 120" }
        Start-Sleep -Seconds 2
        $selected = Spectate
        $offset = [int]((Get-Date) - $take).TotalSeconds
        Note "SEGMENT role=$($segment.role) door=$scpDoor takeOffset=${offset}s spectated=$selected"
        $end = (Get-Date).AddSeconds($segment.seconds)
        $nextDebug = Get-Date
        while ((Get-Date) -lt $end) {
            if ((Get-Date) -ge $nextDebug) { $nextDebug = (Get-Date).AddSeconds(15); $null = Reply '/bot_debug' }
            if ($segment.closeDoors) { $null = Invoke-LabServer '/close !*' }
            Start-Sleep -Seconds $(if ($segment.closeDoors) { 3 } else { 1 })
        }
        if ($segment.lockDoor) { $null = Reply "/unlock $($segment.lockDoor)" }
    }
}
finally {
    if ($started) {
        Invoke-LabSetup 'record-stop' | Out-Null
        WaitRecording 'complete' 300
        Note 'recording complete'
    }
    $traces = Join-Path (Split-Path (Split-Path $Context.Evidence)) "Server/AppData/SCP Secret Laboratory/LabAPI/plugins/$($Context.Port)/SCPSLBot/traces"
    if (Test-Path $traces) { Copy-Item (Join-Path $traces '*.jsonl') $Context.Evidence }
}
