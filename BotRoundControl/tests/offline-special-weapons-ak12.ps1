param($Context)
$ErrorActionPreference = 'Stop'

# Run with --clients 2. Measures the AK-12 against a plain AK: shots over the same held frames, and the
# health an unarmored real player loses per hit. The lab starts clients in god mode.
$firearmHotkey = 49
$dropKey = 116
$reloadKey = 114
$fire = 323
$owner = $Context.Actors[0]
$viewer = $Context.Actors[1]
$ownerClient = $Context.ClientIds[0]
$results = [ordered]@{}

function Actor([int]$Id) { @(Observe | Where-Object id -eq $Id)[0] }
function Reply([string]$Command) { (Invoke-LabServer $Command) -join "`n" }
function ReadyViewer {
    $null = Reply "/forcerole $($viewer.id) NtfPrivate 0"
    Start-Sleep -Milliseconds 500
    $null = Reply "/strip $($viewer.id)"
    $null = Reply "/god $($viewer.id) disable"
    $null = Reply "/cleareffects $($viewer.id)"
}
function Equip([string]$Id) {
    $null = Invoke-LabInput @{id=$Id;frames=150;inputFrames=2;keys=@($firearmHotkey);capture=$true} -ClientId $ownerClient
    if ((Actor $owner.id).held -ne 'GunAK') { throw "AK not held: $((Actor $owner.id).held)" }
}
# Every hit lands on a fresh 100 HP body, so the loss is one bullet's damage.
function HitLosses([string]$Name) {
    $losses = [Collections.Generic.List[double]]::new()
    for ($shot = 0; $shot -lt 24 -and $losses.Count -lt 3; $shot++) {
        ReadyViewer
        $null = Set-LabAim -TargetActorId $viewer.id -ClientId $ownerClient -ActorId $owner.id
        $before = (Actor $viewer.id).health
        $null = Invoke-LabInput @{id="$Name-$shot";frames=24;inputFrames=2;keys=@($fire)} -ClientId $ownerClient
        Start-Sleep -Milliseconds 300
        $after = Actor $viewer.id
        if ($after.health -gt 0 -and $after.health -lt $before) { $losses.Add([Math]::Round($before - $after.health, 1)) }
    }
    @($losses)
}
function ShotsOverHold([string]$Id) {
    $before = (Actor $owner.id).shots
    $null = Invoke-LabInput @{id=$Id;frames=120;keys=@($fire);capture=$true} -ClientId $ownerClient
    Start-Sleep -Milliseconds 300
    (Actor $owner.id).shots - $before
}

$null = Reply '/bot_warmup standard'
$null = Reply '/forcestart'
Start-Sleep -Seconds 5
if ((Reply '/special_weapon list') -notmatch 'ak-12') { throw 'AK-12 is not registered' }
$null = Reply "/forcerole $($owner.id) NtfPrivate"
$null = Reply "/forcerole $($viewer.id) NtfPrivate"
Start-Sleep -Seconds 4
$null = Reply "/forcerole $($owner.id) Tutorial 0"
Start-Sleep -Seconds 1
$null = Reply "/strip $($owner.id)"

$null = Reply "/give $($owner.id) 40"
Start-Sleep -Milliseconds 500
Equip 'native-select'
$results.nativeHitLoss = HitLosses 'native-hit'
$null = Set-LabLook -Yaw 0 -Pitch 60 -ClientId $ownerClient -ActorId $owner.id
$results.nativeShots = ShotsOverHold 'native-hold'
$results.nativeFit = (Actor $owner.id).firearm

$null = Reply "/strip $($owner.id)"
Start-Sleep -Seconds 1
$reply = Reply "/special_weapon grant ak-12 $($owner.id)"
if ($reply -notmatch 'Granted') { throw "Grant failed: $reply" }
Start-Sleep -Milliseconds 500
Equip 'ak12-select'
$results.ak12Screenshot = Invoke-LabScreenshot -Name 'ak12-held' -ClientId $ownerClient
$results.ak12HitLoss = HitLosses 'ak12-hit'
$null = Set-LabLook -Yaw 0 -Pitch 60 -ClientId $ownerClient -ActorId $owner.id
$results.ak12Shots = ShotsOverHold 'ak12-hold'
$results.ak12Fit = (Actor $owner.id).firearm

$null = Invoke-LabInput @{id='ak12-drop';frames=40;inputFrames=2;keys=@($dropKey)} -ClientId $ownerClient
if ((Actor $owner.id).items -notcontains 'GunAK') { throw 'The AK-12 was dropped' }

# Sustained fire through depletion, then a reload from the granted reserve.
for ($burst = 0; $burst -lt 6 -and (Actor $owner.id).firearm.ammo -gt 0; $burst++) {
    $null = Invoke-LabInput @{id="ak12-deplete-$burst";frames=120;keys=@($fire);capture=$true} -ClientId $ownerClient
}
$results.ammoAfterDepletion = (Actor $owner.id).firearm
$null = Invoke-LabInput @{id='ak12-reload';frames=300;inputFrames=2;keys=@($reloadKey);capture=$true} -ClientId $ownerClient
$results.ammoAfterReload = (Actor $owner.id).firearm
if ($results.ammoAfterReload.ammo -le $results.ammoAfterDepletion.ammo) { throw 'The AK-12 did not reload' }

$null = Reply "/forcerole $($owner.id) NtfPrivate"
Start-Sleep -Seconds 2
if ((Reply '/special_weapon status') -notmatch 'tracked=0') { throw 'A role change kept the AK-12' }

$results | ConvertTo-Json -Depth 6 | Set-Content "$($Context.Evidence)/special-weapons-ak12.json" -Encoding utf8
if ($results.ak12Shots -le $results.nativeShots) { throw "The AK-12 was not faster: $($results.ak12Shots) vs $($results.nativeShots)" }
