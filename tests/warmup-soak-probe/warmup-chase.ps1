param($Context)
# Moving-target Surface chase for the build comparison. warmup-compare.ps1 is dominated by bots
# killing a respawning client near its spawn, which leaves pursuit unexercised, so here the client
# carries a large health pool (hits still register as native damage) and runs a fixed Surface circuit
# with native input while firing short bursts at the nearest bot.
$minutes=if($Context.Options.minutes){[double]$Context.Options.minutes}else{5}
$log="$($Context.Evidence)\scenario-server.txt"
$id=$Context.Actor.id
$W=119; $A=97; $D=100; $Shift=304; $Space=32; $Fire=323; $Reload=114; $Primary=49
$circuit=@(@{x=128;z=-46},@{x=100;z=-46},@{x=64;z=-43},@{x=36;z=-41.5},@{x=64;z=-43},@{x=100;z=-46})

function Server([string]$Command) { $reply=(Invoke-LabServer $Command) -join "`n"; "> $Command`n$reply" | Add-Content $log -Encoding utf8; $reply }
function Note([string]$Text) { "[$(Get-Date -Format HH:mm:ss.fff)] $Text" | Add-Content $log -Encoding utf8; $null=Invoke-LabServer "/soakprobe mark $Text" }
function IsLiving($actor) { $actor -and $actor.role -notin 'Spectator','Overwatch','Tutorial','Filmmaker','Destroyed','None' }
function Flat($from,$to) { $x=$from.x-$to.x; $z=$from.z-$to.z; [Math]::Sqrt($x*$x+$z*$z) }

$null=Server "/soakprobe start $($Context.Evidence)\probe.jsonl"
if((Server '/roundtime') -notmatch 'Round time:') { throw 'Warmup did not start the round' }
$null=Invoke-LabSetup 'record-start warmup-chase'
$deadline=(Get-Date).AddSeconds(30)
do { Start-Sleep -Milliseconds 300; $phase=((Invoke-LabSetup 'record-status').setupResponse | ConvertFrom-Json).phase } until ($phase -eq 'recording' -or (Get-Date) -gt $deadline)
if($phase -ne 'recording') { throw "Recording did not start: $phase" }

$null=Server "/god $id disable"
$null=Server "/forcerole $id Spectator"
$null=Wait-LabActor { param($a) IsLiving $a } 'Warmup did not respawn the spectator' -Seconds 30
$null=Server "/hp $id 20000"
$null=Invoke-LabInput @{frames=30;inputFrames=2;keys=@($Primary)}
Note 'play-start'

$end=(Get-Date).AddMinutes($minutes)
$life=$null; $leg=0; $step=0; $stuck=0; $bursts=0; $previous=$null; $errors=0
while((Get-Date) -lt $end) {
    try {
        $all=@(Observe)
        $me=@($all | Where-Object id -eq $id)[0]
        if(!(IsLiving $me) -or !$me.ready) { Start-Sleep -Milliseconds 400; continue }
        if($me.life -ne $life) {
            $life=$me.life; $previous=$null; $leg=0
            Note "client-life $life role=$($me.role)"
            $null=Server "/hp $id 20000"
            $null=Invoke-LabInput @{frames=30;inputFrames=2;keys=@($Primary)}
            continue
        }
        $step++
        if($step % 4 -eq 0) {
            $bots=@($all | Where-Object { $_.dummy -and (IsLiving $_) -and $_.role -like 'Chaos*' })
            $target=$bots | Sort-Object { Flat $me.position $_.position } | Select-Object -First 1
            if($target -and (Flat $me.position $target.position) -le 30) {
                $null=Set-LabAim -Target $target.position
                $null=Invoke-LabInput @{frames=30;keys=@($Fire)}
                $bursts++
                if($bursts % 6 -eq 0) { $null=Invoke-LabInput @{frames=190;inputFrames=2;keys=@($Reload,$W)} }
                continue
            }
        }
        $goal=$circuit[$leg % $circuit.Count]
        if((Flat $me.position $goal) -lt 5) { $leg++; $goal=$circuit[$leg % $circuit.Count]; $stuck=0 }
        $null=Set-LabLook -Yaw ([Math]::Atan2($goal.x-$me.position.x,$goal.z-$me.position.z)*180/[Math]::PI) -Pitch 0
        if($previous -and (Flat $me.position $previous) -lt 1) {
            $stuck++
            $side=if($stuck % 2) { $A } else { $D }
            $null=Invoke-LabInput @{frames=50;keys=@($W,$side,$Space)}
            if($stuck -ge 6) { $leg++; $stuck=0 }
        }
        else { $null=Invoke-LabInput @{frames=75;keys=@($W,$Shift)} }
        $previous=$me.position
    }
    catch {
        $errors++
        "loop error: $($_.Exception.Message)" | Add-Content $log -Encoding utf8
        if($errors -gt 200) { throw }
        Start-Sleep -Milliseconds 400
    }
}
Note "play-end errors=$errors legs=$leg bursts=$bursts"
$null=Server '/bot_status'
$null=Invoke-LabScreenshot -Name 'chase-end'
$null=Server '/soakprobe stop'
$null=Invoke-LabSetup 'record-stop'
Complete-LabRecordings
