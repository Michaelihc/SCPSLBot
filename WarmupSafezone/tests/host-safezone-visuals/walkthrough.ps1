param($Context)
# Real-client walkthrough of WarmupSafezone visuals and the narrowed throw policy.
# 1. Class-D cells: native ClassD spawn; look around (no indicator by design); a pistol shot shows
#    the blocked-action hint.
# 2. SCP-914: gate panel from inside the room; blocked shot hint; SCP-018, SCP-2176 and flash
#    throws are cancelled and kept; a medkit toss and a frag throw complete, and the frag does not
#    change the thrower's life.
# 3. Surface: the configured boundary wall and label, then walking across it.
. 'C:/Users/Michael/source-from-fsp9-2026-08-15/scpsl-plugins-metarepo/.tests/offline-clients/tools/host-aim.ps1'
$id=$Context.Actor.id
$log="$($Context.Evidence)\scenario-server.txt"
$marks=[System.Collections.Generic.List[object]]::new()
$results=[ordered]@{}
function Server([string]$Command) {
    $reply=Invoke-LabServer $Command
    "> $Command`n$($reply -join "`n")" | Add-Content $log -Encoding utf8
    return ($reply -join "`n")
}
function Mark([string]$Name) {
    $marks.Add([pscustomobject]@{name=$Name;utc=(Get-Date).ToUniversalTime().ToString('o')})
    $marks | ConvertTo-Json -Depth 4 | Set-Content "$($Context.Evidence)\marks.json" -Encoding utf8
}
function Actor { @(Observe | Where-Object id -eq $id)[0] }
function Wait-For([scriptblock]$Condition,[string]$Message,[int]$Seconds=20) {
    $deadline=(Get-Date).AddSeconds($Seconds)
    do { $value=& $Condition; if($value) { return $value }; Start-Sleep -Milliseconds 400 } while((Get-Date) -lt $deadline)
    throw $Message
}
function Place($p) {
    $text=Server ("labplacecheck {0:0.###} {1:0.###} {2:0.###}" -f $p.x,$p.y,$p.z)
    $line=@($text -split "`n" | Where-Object { $_ -match 'OFFLINE_LAB_PLACEMENT' })[0]
    if(-not $line) { throw "Placement check was not delivered: $text" }
    return ($line.Substring($line.IndexOf('{')) | ConvertFrom-Json)
}
function Flat($a,$b) { [Math]::Sqrt([Math]::Pow($a.x-$b.x,2)+[Math]::Pow($a.z-$b.z,2)) }
function Has-Item([string]$Type) { @((Actor).items | Where-Object { "$_" -eq $Type }).Count -gt 0 }
function Save { $results | ConvertTo-Json -Depth 8 | Set-Content "$($Context.Evidence)\scenario.json" -Encoding utf8 }
function Hold-Only([int]$ItemId,[string]$Type,[int]$Hotkey) {
    $null=Server "/strip $id"
    $null=Server "/give $id $ItemId"
    $null=Wait-For { Has-Item $Type } "$Type was not given"
    $null=Invoke-LabInput @{frames=40;inputFrames=2;keys=@($Hotkey)}
    $null=Wait-For { $a=Actor; if("$($a.held)" -eq $Type) { $a } } "$Type was not selected with hotkey $Hotkey" 6
}
function Look-Around([string]$Name) {
    $yaw=(Actor).look.yaw
    foreach($step in 0..3) {
        $null=Set-LabLook -Yaw ((($yaw + 90*$step) + 540) % 360 - 180) -Pitch 5
        $null=Invoke-LabScreenshot -Name "$Name-$step"
    }
    $null=Set-LabLook -Yaw ((($yaw) + 540) % 360 - 180) -Pitch 5
}

# Arrange the round.
$null=Server '/roundlock on'
$null=Server '/forcestart'
Start-Sleep -Seconds 4
$results.status=Server 'safezone status'
if($results.status -notmatch 'classd_cells=True') { throw "Candidate safezone status unexpected: $($results.status)" }

# 1. Class-D cells.
$null=Server "/forcerole $id ClassD"
$null=Wait-For { $a=Actor; if($a -and $a.ready -and $a.role -eq 'ClassD') { $a } } 'Client did not become ClassD'
$null=Server "/god $id 1"
Start-Sleep -Seconds 2
$cells=Place (Actor).position
$results.cellsRoom=$cells.room.name
if($cells.room.name -ne 'LczClassDSpawn') { throw "ClassD did not spawn in the cells: $($cells.room.name)" }
Mark 'cells-arrived'
Look-Around 'cells'
Hold-Only 13 'GunCOM15' 49
$null=Invoke-LabInput @{id='cells-blocked-shot';frames=120;inputFrames=2;keys=@(323);capture=$true;audio=$true}
$null=Invoke-LabScreenshot -Name 'cells-after-shot'
Mark 'cells-shot'
Save

# 2. SCP-914: enter the room through the opened gate, then face the gate panel.
$null=Server "/doortp $id 914"
Start-Sleep -Seconds 1
$gate=(Actor).position
$gateCheck=Place $gate
$results.gateRoom=$gateCheck.room.name
if($gateCheck.room.name -ne 'Lcz914') { throw "doortp 914 did not resolve inside the SCP-914 room: $($gateCheck.room.name)" }
$origin=$gateCheck.room.origin
$null=Server '/open 914'
Start-Sleep -Seconds 2
$null=Set-LabAim -Target @{x=$origin.x;y=((Actor).position.y+0.6);z=$origin.z}
foreach($i in 1..10) {
    if((Flat (Actor).position $gate) -ge 3.5) { break }
    $null=Invoke-LabInput @{frames=20;keys=@(119)}
}
$inside=Actor
$results.insideDistanceFromGate=Flat $inside.position $gate
Mark '914-inside'
$results.statusInside=Server 'safezone status'
Look-Around '914'
$null=Set-LabAim -Target @{x=$gate.x;y=$gate.y+1.6;z=$gate.z}
$null=Invoke-LabInput @{id='914-panel';frames=90;capture=$true}
$null=Invoke-LabScreenshot -Name '914-panel'
Mark '914-panel'

# Throws face the room interior so nothing leaves the room.
$null=Set-LabAim -Target @{x=$origin.x;y=((Actor).position.y+0.6);z=$origin.z}
Hold-Only 13 'GunCOM15' 49
$null=Invoke-LabInput @{id='914-blocked-shot';frames=120;inputFrames=2;keys=@(323);capture=$true;audio=$true}
$results.blocked=[ordered]@{}
foreach($case in @(@{id=31;type='SCP018'},@{id=43;type='SCP2176'},@{id=26;type='GrenadeFlash'})) {
    Hold-Only $case.id $case.type 103
    $null=Invoke-LabInput @{id="914-throw-$($case.type.ToLowerInvariant())";frames=150;inputFrames=30;keys=@(323);capture=$true;audio=$true}
    Start-Sleep -Seconds 1
    $kept=Has-Item $case.type
    $results.blocked[$case.type]=$kept
    Save
    if(-not $kept) { throw "$($case.type) throw from SCP-914 was not cancelled" }
}
Mark '914-blocked-throws'

Hold-Only 14 'Medkit' 120
$null=Invoke-LabInput @{id='914-toss-medkit';frames=120;inputFrames=2;keys=@(116);capture=$true;audio=$true}
Start-Sleep -Seconds 1
$results.medkitTossed=-not (Has-Item 'Medkit')
Save
if(-not $results.medkitTossed) { throw 'Medkit toss from SCP-914 was blocked' }

$null=Server "/god $id 0"
$lifeBefore=(Actor).life
$healthBefore=(Actor).health
Hold-Only 25 'GrenadeHE' 103
$null=Invoke-LabInput @{id='914-throw-frag';frames=480;inputFrames=30;keys=@(323);capture=$true;audio=$true;expectAudio=$true}
$after=Actor
$results.fragThrown=-not (Has-Item 'GrenadeHE')
$results.fragLifeUnchanged=($after.life -eq $lifeBefore -and $after.role -eq 'ClassD')
$results.fragHealth=@{before=$healthBefore;after=$after.health}
$null=Server "/god $id 1"
Save
if(-not $results.fragThrown) { throw 'Frag throw from SCP-914 was blocked' }
if(-not $results.fragLifeUnchanged) { throw 'Frag thrown inside SCP-914 killed or changed the thrower' }
if($after.health -lt $healthBefore-0.01) { throw "Frag thrown inside SCP-914 damaged its thrower: $healthBefore -> $($after.health)" }
Mark '914-allowed-throws'

# 3. Surface boundary wall and label.
$doors=Server '/doorslist'
$surfaceDoor=@('ESCAPE_PRIMARY','ESCAPE_SECONDARY','SURFACE_GATE','GATE_B') | Where-Object { $doors -match "\b$_\b" } | Select-Object -First 1
if(-not $surfaceDoor) { throw "No known Surface door in doorslist: $doors" }
$null=Server "/strip $id"
$null=Server "/doortp $id $surfaceDoor"
Start-Sleep -Seconds 2
$surface=Actor
$results.surfaceDoor=$surfaceDoor
$results.surfaceStart=$surface.position
Mark 'surface-arrived'
$label=@{x=136.45;y=295.8;z=-16.86}
$null=Set-LabAim -Target $label
$null=Invoke-LabInput @{id='surface-wall';frames=120;capture=$true}
$null=Invoke-LabScreenshot -Name 'surface-wall'
$null=Set-LabAim -Target @{x=$label.x;y=((Actor).position.y+0.6);z=$label.z}
$null=Invoke-LabInput @{id='surface-walk';frames=360;keys=@(119,304);capture=$true;audio=$true}
$null=Invoke-LabScreenshot -Name 'surface-after-walk'
$results.surfaceEnd=(Actor).position
Mark 'surface-walked'
Save
