param($Context)
# Real-client walkthrough of WarmupSafezone visuals and the narrowed throw policy.
# 1. Class-D cells: native ClassD spawn; look around; a pistol shot shows the blocked-action hint;
#    noclip to the nearest exit on the safezone grid cell and film the cyan boundary from both sides.
# 2. SCP-914: gate panel from inside the room; blocked shot hint; SCP-018, SCP-2176, flash and frag
#    throws and SCP-018/SCP-2176 T tosses are cancelled and kept; a medkit toss completes.
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
function Fly-To($Target,[double]$Stop=0.7) {
    foreach($i in 1..40) {
        $a=Actor
        $d=Flat $a.position $Target
        if($d -le $Stop) { return $a }
        $null=Set-LabAim -Target @{x=$Target.x;y=$a.position.y+0.6;z=$Target.z}
        $null=Invoke-LabInput @{frames=[int][Math]::Max(3,[Math]::Min(20,$d*3));keys=@(119)}
    }
    throw "Could not fly to ($($Target.x),$($Target.z))"
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
if($results.status -notmatch 'classd_cells=True' -or $results.status -notmatch 'GrenadeHE') { throw "Candidate safezone status unexpected: $($results.status)" }

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

# Cells boundary: noclip from the spawn to the exit nearest the cell, film the boundary from inside,
# cross it and film it from outside.
$tile=[regex]::Match($results.status,'classd_cells_tile=x=([-\d.]+)\.\.([-\d.]+) z=([-\d.]+)\.\.([-\d.]+) floor=([-\d.]+) exits=\[([^\]]*)\]')
if(-not $tile.Success) { throw "Class-D cells tile missing from status: $($results.status)" }
$center=@{x=([double]$tile.Groups[1].Value+[double]$tile.Groups[2].Value)/2;z=([double]$tile.Groups[3].Value+[double]$tile.Groups[4].Value)/2}
$floor=[double]$tile.Groups[5].Value
$spawn=(Actor).position
$exits=@([regex]::Matches($tile.Groups[6].Value,'\(([-\d.]+),([-\d.]+),([-\d.]+)\)') | ForEach-Object { @{x=[double]$_.Groups[1].Value;y=[double]$_.Groups[2].Value;z=[double]$_.Groups[3].Value} })
$results.cellsTile=$tile.Value
if($exits.Count -eq 0) { throw 'No Class-D cells exit found on the safezone cell edge' }
$exit=$exits | Sort-Object { Flat $_ $spawn } | Select-Object -First 1
$dx=$exit.x-$center.x; $dz=$exit.z-$center.z
if([Math]::Abs($dx) -ge [Math]::Abs($dz)) { $dir=@{x=[Math]::Sign($dx);z=0} } else { $dir=@{x=0;z=[Math]::Sign($dz)} }
$insidePoint=@{x=$exit.x-3.5*$dir.x;z=$exit.z-3.5*$dir.z}
$outsidePoint=@{x=$exit.x+3.5*$dir.x;z=$exit.z+3.5*$dir.z}
$results.cellsExit=$exit
$null=Server "/noclip $id 1"
$null=Invoke-LabInput @{frames=20;inputFrames=2;keys=@(308)}
$null=Wait-For { $a=Actor; if($a.noclip) { $a } } 'Noclip did not enable' 6
$null=Fly-To $insidePoint
$null=Set-LabAim -Target @{x=$exit.x;y=$floor+1.3;z=$exit.z}
$null=Invoke-LabInput @{frames=20;inputFrames=2;keys=@(101)}
$null=Invoke-LabInput @{id='cells-boundary-inside';frames=90;capture=$true}
$null=Invoke-LabScreenshot -Name 'cells-boundary-inside'
$null=Invoke-LabInput @{id='cells-boundary-cross';frames=90;keys=@(119);inputFrames=45;capture=$true}
$null=Fly-To $outsidePoint
$null=Set-LabAim -Target @{x=$exit.x;y=$floor+1.3;z=$exit.z}
$null=Invoke-LabInput @{id='cells-boundary-outside';frames=90;capture=$true}
$null=Invoke-LabScreenshot -Name 'cells-boundary-outside'
$null=Invoke-LabInput @{frames=20;inputFrames=2;keys=@(308)}
$null=Server "/noclip $id 0"
Mark 'cells-boundary'
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
$null=Set-LabAim -Target @{x=$gate.x;y=$gate.y+0.9;z=$gate.z}
$null=Invoke-LabInput @{id='914-panel';frames=90;capture=$true}
$null=Invoke-LabScreenshot -Name '914-panel'
Mark '914-panel'

# Throws face the room interior so nothing leaves the room.
$null=Set-LabAim -Target @{x=$origin.x;y=((Actor).position.y+0.6);z=$origin.z}
Hold-Only 13 'GunCOM15' 49
$null=Invoke-LabInput @{id='914-blocked-shot';frames=120;inputFrames=2;keys=@(323);capture=$true;audio=$true}
$results.blocked=[ordered]@{}
foreach($case in @(@{id=31;type='SCP018'},@{id=43;type='SCP2176'},@{id=26;type='GrenadeFlash'},@{id=25;type='GrenadeHE'})) {
    Hold-Only $case.id $case.type 103
    $null=Invoke-LabInput @{id="914-throw-$($case.type.ToLowerInvariant())";frames=150;inputFrames=30;keys=@(323);capture=$true;audio=$true}
    Start-Sleep -Seconds 1
    $kept=Has-Item $case.type
    $results.blocked[$case.type]=$kept
    Save
    if(-not $kept) { throw "$($case.type) throw from SCP-914 was not cancelled" }
}
Mark '914-blocked-throws'

# Native T toss (the drop path) of SCP-018 and SCP-2176 is also cancelled.
$results.blockedToss=[ordered]@{}
foreach($case in @(@{id=31;type='SCP018'},@{id=43;type='SCP2176'})) {
    Hold-Only $case.id $case.type 103
    $null=Invoke-LabInput @{id="914-toss-$($case.type.ToLowerInvariant())";frames=120;inputFrames=2;keys=@(116);capture=$true;audio=$true}
    Start-Sleep -Seconds 1
    $kept=Has-Item $case.type
    $results.blockedToss[$case.type]=$kept
    Save
    if(-not $kept) { throw "$($case.type) T toss from SCP-914 was not cancelled" }
}
Mark '914-blocked-tosses'

Hold-Only 14 'Medkit' 120
$null=Invoke-LabInput @{id='914-toss-medkit';frames=120;inputFrames=2;keys=@(116);capture=$true;audio=$true}
Start-Sleep -Seconds 1
$results.medkitTossed=-not (Has-Item 'Medkit')
Save
if(-not $results.medkitTossed) { throw 'Medkit toss from SCP-914 was blocked' }

Mark '914-medkit-toss'

# Gate panel from the corridor side: walk back through the open gate and turn around.
$away=@{x=$gate.x-$origin.x;z=$gate.z-$origin.z}
$len=[Math]::Sqrt($away.x*$away.x+$away.z*$away.z)
$outside=@{x=$gate.x+3.5*$away.x/$len;z=$gate.z+3.5*$away.z/$len}
$null=Fly-To $outside 0.8
$null=Set-LabAim -Target @{x=$gate.x;y=$gate.y+0.9;z=$gate.z}
$null=Invoke-LabInput @{id='914-panel-outside';frames=90;capture=$true}
$null=Invoke-LabScreenshot -Name '914-panel-outside'
Mark '914-panel-outside'

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
