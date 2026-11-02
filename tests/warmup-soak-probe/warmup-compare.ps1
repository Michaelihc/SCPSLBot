param($Context)
# One real client in Standard warmup, run identically against the production and release builds.
# Options: arena = surface | pvpve | lcz, minutes = soak length, idleSeconds = spawn wait before play.
#
# The client enters through the warmup layer: a spectator respawn lands on the default Surface arena,
# then RA role changes follow the native Surface evacuation routing (human -> HCZ/EZ, SCP -> LCZ). The
# SSS Arena preset Apply button performs the same role + arena change but cannot be pressed by the
# input adapter. All play uses native mouse/keyboard input.
$arena=if($Context.Options.arena){[string]$Context.Options.arena}else{'surface'}
$minutes=if($Context.Options.minutes){[double]$Context.Options.minutes}else{6}
$idleSeconds=if($null -ne $Context.Options.idleSeconds){[int]$Context.Options.idleSeconds}else{30}
if($arena -notin 'surface','pvpve','lcz') { throw "Unknown arena $arena" }
$log="$($Context.Evidence)\scenario-server.txt"
$id=$Context.Actor.id
$W=119; $A=97; $D=100; $Shift=304; $Space=32; $Fire=323; $Reload=114; $Primary=49

function Server([string]$Command) { $reply=(Invoke-LabServer $Command) -join "`n"; "> $Command`n$reply" | Add-Content $log -Encoding utf8; $reply }
function Note([string]$Text) { "[$(Get-Date -Format HH:mm:ss.fff)] $Text" | Add-Content $log -Encoding utf8; $null=Invoke-LabServer "/soakprobe mark $Text" }
function Actor { @(Observe | Where-Object id -eq $id)[0] }
function IsLiving($actor) { $actor -and $actor.role -notin 'Spectator','Overwatch','Tutorial','Filmmaker','Destroyed','None' }
function Distance($from,$to) { $x=$from.x-$to.x; $y=$from.y-$to.y; $z=$from.z-$to.z; [Math]::Sqrt($x*$x+$y*$y+$z*$z) }
function IsHostile($me,$other) {
    if(!$other.dummy -or !(IsLiving $other) -or $other.id -eq $me.id) { return $false }
    if($other.role -like 'Scp*') { return $me.role -notlike 'Scp*' }
    $meNtf=$me.role -like 'Ntf*' -or $me.role -in 'FacilityGuard','Scientist'
    $otherNtf=$other.role -like 'Ntf*'
    $otherChaos=$other.role -like 'Chaos*'
    if($meNtf) { return $otherChaos }
    if($me.role -eq 'ClassD' -or $me.role -like 'Chaos*') { return $otherNtf }
    return $false
}
function Status([string]$Label) {
    $reply=Invoke-LabServer '/bot_server_status'
    $fields=($reply -join ' ') -replace '.*?(desired=\d+).*?(arenas=[^;]*).*?(ai_last_fault=[^;]*).*?(parked=\d+).*?(nav_backend=[^;]*).*',"`$1 `$2 `$3 `$4 `$5"
    Note "status $Label $fields"
}

$null=Server "/soakprobe start $($Context.Evidence)\probe.jsonl"
$null=Server '/bot_warmup'
$null=Server '/roundtime'
if((Server '/roundtime') -notmatch 'Round time:') { throw 'Warmup did not start the round' }
Get-LabSettings | ConvertTo-Json -Depth 8 | Set-Content "$($Context.Evidence)\sss-before-spawn.json" -Encoding utf8

$null=Invoke-LabSetup "record-start warmup-$arena"
$deadline=(Get-Date).AddSeconds(30)
do { Start-Sleep -Milliseconds 300; $phase=((Invoke-LabSetup 'record-status').setupResponse | ConvertFrom-Json).phase } until ($phase -eq 'recording' -or (Get-Date) -gt $deadline)
if($phase -ne 'recording') { throw "Recording did not start: $phase" }

# Join flow: leave the runner's god-mode Tutorial fixture and let warmup respawn the spectator.
$null=Server "/god $id disable"
$null=Server "/forcerole $id Spectator"
Note 'join-spectator'
$null=Wait-LabActor { param($a) IsLiving $a } 'Warmup did not respawn the spectator' -Seconds 30
$first=Actor
Note "join-spawn role=$($first.role)"
if($first.godMode) { throw 'Client kept god mode' }
if($first.position.y -lt 200) { throw "Default arena did not place the client on Surface: $($first.position | ConvertTo-Json -Compress)" }
Start-Sleep -Seconds 2
Get-LabSettings | ConvertTo-Json -Depth 8 | Set-Content "$($Context.Evidence)\sss-alive.json" -Encoding utf8
$null=Invoke-LabScreenshot -Name "hud-spawn"

if($arena -eq 'pvpve') {
    $null=Server "/forcerole $id ClassD"
    $null=Wait-LabActor { param($a) $a.role -eq 'ClassD' -and $a.position.y -lt 200 } 'Surface human routing did not reach HCZ/EZ'
    Start-Sleep -Seconds 1
    $null=Server "/forcerole $id NtfPrivate"
    $null=Wait-LabActor { param($a) $a.role -eq 'NtfPrivate' -and $a.position.y -lt 200 } 'Arena role was not applied'
}
elseif($arena -eq 'lcz') {
    $null=Server "/forcerole $id Scp173"
    $null=Wait-LabActor { param($a) $a.role -eq 'Scp173' -and $a.position.y -lt 200 } 'Surface SCP routing did not reach LCZ'
    Start-Sleep -Seconds 1
    $null=Server "/forcerole $id ClassD"
    $null=Wait-LabActor { param($a) $a.role -eq 'ClassD' -and $a.position.y -lt 200 } 'Arena role was not applied'
}
# Optional fixture: stand at a native door inside the arena, past the Class-D cells safezone, so the
# arena's bots can contact the client. RA doortp is the validated native placement.
if($Context.Options.door) {
    $null=Server "/doortp $id $($Context.Options.door)"
    Start-Sleep -Seconds 1
}
Start-Sleep -Seconds 3
Status 'arena-entered'
Note "arena-entered $arena pos=$((Actor).position | ConvertTo-Json -Compress)"
$null=Invoke-LabScreenshot -Name "hud-$arena"

$null=Invoke-LabInput @{frames=30;inputFrames=2;keys=@($Primary)}
Note 'idle-start'
Start-Sleep -Seconds $idleSeconds
Note 'play-start'

$end=(Get-Date).AddMinutes($minutes)
$nextStatus=(Get-Date).AddSeconds(60)
$nextShot=(Get-Date).AddSeconds(90)
$life=$null; $burstFrames=0; $lastPosition=$null; $shots=0; $errors=0
while((Get-Date) -lt $end) {
    try {
        if((Get-Date) -gt $nextStatus) { Status 'tick'; $nextStatus=(Get-Date).AddSeconds(60) }
        if((Get-Date) -gt $nextShot) { $shots++; $null=Invoke-LabScreenshot -Name "hud-$arena-$shots"; $nextShot=(Get-Date).AddSeconds(90) }
        $all=@(Observe)
        $me=@($all | Where-Object id -eq $id)[0]
        if(!(IsLiving $me) -or !$me.ready) { Start-Sleep -Milliseconds 500; continue }
        if($me.life -ne $life) {
            $life=$me.life; $burstFrames=0; $lastPosition=$null
            Note "client-life $life role=$($me.role)"
            $null=Invoke-LabInput @{frames=30;inputFrames=2;keys=@($Primary)}
            continue
        }
        $hostile=@($all | Where-Object { IsHostile $me $_ } | Sort-Object { Distance $me.position $_.position })
        $target=$hostile | Select-Object -First 1
        $range=if($target) { Distance $me.position $target.position } else { [double]::MaxValue }
        if($target -and $range -le 26) {
            $null=Set-LabAim -Target $target.position
            $null=Invoke-LabInput @{frames=40;keys=@($Fire)}
            $burstFrames+=40
            if($burstFrames -ge 200) {
                $null=Invoke-LabInput @{frames=190;inputFrames=2;keys=@($Reload)}
                $burstFrames=0
            }
        }
        elseif($arena -eq 'surface' -and $target -and $target.position.y -gt 200) {
            # Close the distance as a Surface player would; a rejected move sidesteps and hops.
            $dx=$target.position.x-$me.position.x; $dz=$target.position.z-$me.position.z
            $null=Set-LabLook -Yaw ([Math]::Atan2($dx,$dz)*180/[Math]::PI) -Pitch 0
            if($lastPosition -and (Distance $me.position $lastPosition) -lt 1) {
                $side=if((Get-Random -Maximum 2) -eq 0) { $A } else { $D }
                $null=Invoke-LabInput @{frames=50;keys=@($W,$side,$Space)}
            }
            else { $null=Invoke-LabInput @{frames=75;keys=@($W,$Shift)} }
            $lastPosition=$me.position
        }
        else { Start-Sleep -Milliseconds 1000 }
    }
    catch {
        $errors++
        "loop error: $($_.Exception.Message)" | Add-Content $log -Encoding utf8
        if($errors -gt 200) { throw }
        Start-Sleep -Milliseconds 500
    }
}
Note "play-end errors=$errors"
Status 'end'
$null=Server '/bot_status'
$null=Invoke-LabScreenshot -Name "hud-$arena-end"
Observe | ConvertTo-Json -Depth 6 | Set-Content "$($Context.Evidence)\final-actors.json" -Encoding utf8
$null=Server '/soakprobe stop'
$null=Invoke-LabSetup 'record-stop'
Complete-LabRecordings
