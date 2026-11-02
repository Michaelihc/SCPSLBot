param($Context)
$ErrorActionPreference = 'Stop'

# Run with --clients 2: the launcher's third-person pose and hits on a real player need a second client.
$primaryUse = 104
$firearmHotkey = 49
$walkBack = 115
$walkRight = 100
$fire = 323
$owner = $Context.Actors[0]
$viewer = $Context.Actors[1]
$ownerClient = $Context.ClientIds[0]
$viewerClient = $Context.ClientIds[1]
$results = [ordered]@{}

function Actor([int]$Id) { @(Observe | Where-Object id -eq $Id)[0] }
function Reply([string]$Command) { (Invoke-LabServer $Command) -join "`n" }
function Distance($Left, $Right) {
    $dx = $Left.x - $Right.x; $dy = $Left.y - $Right.y; $dz = $Left.z - $Right.z
    [Math]::Sqrt(($dx * $dx) + ($dy * $dy) + ($dz * $dz))
}
function FaceEachOther {
    $null = Set-LabAim -TargetActorId $viewer.id -ClientId $ownerClient -ActorId $owner.id
    $null = Set-LabAim -TargetActorId $owner.id -ClientId $viewerClient -ActorId $viewer.id
}
function Shot([string]$Name, [int]$ClientId) { Invoke-LabScreenshot -Name $Name -ClientId $ClientId }
function SelectFirearm {
    $null = Invoke-LabInput @{id='select';frames=60;inputFrames=2;keys=@($firearmHotkey);capture=$true} -ClientId $ownerClient
}
function PressRocket([string]$Id, [int]$Frames) {
    $null = Invoke-LabInput @{id=$Id;frames=$Frames;inputFrames=2;keys=@($primaryUse);capture=$true} -ClientId $ownerClient
}
function WasHurt($Before) {
    $after = Actor $viewer.id
    $after.life -ne $Before.life -or $after.health -lt $Before.health
}

$null = Reply '/bot_warmup standard'
$null = Reply '/forcestart'
Start-Sleep -Seconds 5
$null = Reply "/forcerole $($owner.id) Tutorial"
$null = Reply "/forcerole $($viewer.id) Tutorial"
Start-Sleep -Seconds 3
$null = Reply "/strip $($owner.id)"
$null = Reply "/strip $($viewer.id)"
$null = Reply "/god $($owner.id) enable"

$reply = Reply "/special_weapon grant guided-rocket $($owner.id)"
if ($reply -notmatch 'Granted') { throw "Grant failed: $reply" }
Start-Sleep -Milliseconds 500
SelectFirearm

$null = Invoke-LabInput @{id='viewer-back';frames=110;keys=@($walkBack)} -ClientId $viewerClient
FaceEachOther
$results.ownerView = Shot 'owner-view' $ownerClient
$results.observerFront = Shot 'observer-front' $viewerClient
$null = Invoke-LabInput @{id='viewer-side';frames=90;keys=@($walkRight)} -ClientId $viewerClient
FaceEachOther
$results.observerSide = Shot 'observer-side' $viewerClient

# Friendly fire is off, so a Tutorial cannot hurt another Tutorial with this or any native weapon.
$before = Actor $viewer.id
PressRocket 'tutorial-rocket' 170
Start-Sleep -Seconds 2
$results.tutorialVsTutorialHurt = WasHurt $before
if ($results.tutorialVsTutorialHurt) { throw 'A rocket hurt a same-team Tutorial with friendly fire off' }

# Warmup sends NTF to its Surface arena, so meet there; flag 0 then keeps the owner in place as Tutorial.
$null = Reply "/forcerole $($owner.id) NtfPrivate"
$null = Reply "/forcerole $($viewer.id) NtfPrivate"
Start-Sleep -Seconds 4
$null = Reply "/forcerole $($owner.id) Tutorial 0"
Start-Sleep -Seconds 1
$null = Reply "/strip $($owner.id)"
$null = Reply "/god $($owner.id) enable"
$reply = Reply "/special_weapon grant guided-rocket $($owner.id)"
if ($reply -notmatch 'Granted') { throw "Grant failed: $reply" }
Start-Sleep -Milliseconds 500
SelectFirearm
FaceEachOther
$results.separationMeters = [Math]::Round((Distance (Actor $owner.id).position (Actor $viewer.id).position), 1)
$before = Actor $viewer.id
PressRocket 'tutorial-rocket-at-ntf' 12
$results.observerReload = Shot 'observer-reload' $viewerClient
$results.ownerReload = Shot 'owner-reload' $ownerClient
$null = Invoke-LabInput @{id='rocket-flight';frames=150;capture=$true} -ClientId $ownerClient
$results.tutorialVsNtfHurt = WasHurt $before
if (!$results.tutorialVsNtfHurt) { throw "A Tutorial rocket did not hurt an NTF player: $($results | ConvertTo-Json -Depth 4 -Compress)" }

$null = Reply "/strip $($owner.id)"
$null = Reply "/forcerole $($viewer.id) NtfPrivate"
Start-Sleep -Seconds 4
$reply = Reply "/special_weapon grant graviton-disruptor $($owner.id)"
if ($reply -notmatch 'Granted') { throw "Grant failed: $reply" }
Start-Sleep -Milliseconds 500
$null = Invoke-LabInput @{id='select-graviton';frames=150;inputFrames=2;keys=@($firearmHotkey);capture=$true} -ClientId $ownerClient

# Past the 2 m core, a shockwave leaves a 100 HP player alive, so the throw is observable.
$me = Actor $owner.id
$before = Actor $viewer.id
$dx = $me.position.x - $before.position.x
$dz = $me.position.z - $before.position.z
$length = [Math]::Max(0.1, [Math]::Sqrt(($dx * $dx) + ($dz * $dz)))
$floor = @{
    x=$before.position.x + ($dx / $length * 3)
    y=$before.position.y - 0.9
    z=$before.position.z + ($dz / $length * 3)
}
$null = Set-LabAim -Target $floor -ClientId $ownerClient -ActorId $owner.id
$null = Invoke-LabInput @{id='graviton-shot';frames=150;keys=@($fire);capture=$true} -ClientId $ownerClient
$after = Actor $viewer.id
$results.shockwaveHurt = WasHurt $before
$results.shockwaveMovedMeters = [Math]::Round((Distance $before.position $after.position), 2)
$results.ownerRole = (Actor $owner.id).role
$results.viewerRole = $after.role
if (!$results.shockwaveHurt) { throw 'A Tutorial shockwave did not hurt an NTF player' }

$results | ConvertTo-Json -Depth 6 | Set-Content "$($Context.Evidence)/special-weapons-observer.json" -Encoding utf8
