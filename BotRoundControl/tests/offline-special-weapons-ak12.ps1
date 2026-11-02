param($Context)
$ErrorActionPreference = 'Stop'

# Measures the AK-12 against a plain AK from the same Tutorial attacker: shots over the same held frames,
# and the health a bot loses per hit, which compares fairly because both guns face the same armor.
$firearmHotkey = 49
$dropKey = 116
$reloadKey = 114
$fire = 323
$actorId = $Context.Actor.id
$results = [ordered]@{}

function Me { @(Observe | Where-Object id -eq $actorId)[0] }
function Reply([string]$Command) { (Invoke-LabServer $Command) -join "`n" }
function Enemies { @(Observe | Where-Object { $_.id -ne $actorId -and $_.role -like 'Chaos*' -and $_.health -gt 0 }) }
function Distance($Left, $Right) {
    $dx = $Left.x - $Right.x; $dy = $Left.y - $Right.y; $dz = $Left.z - $Right.z
    [Math]::Sqrt(($dx * $dx) + ($dy * $dy) + ($dz * $dz))
}
function NearestEnemy([double]$Within) {
    $deadline = (Get-Date).AddSeconds(90)
    do {
        $me = Me
        $near = @(Enemies | Sort-Object { Distance $_.position $me.position } | Select-Object -First 1)
        if ($near.Count -and (Distance $near[0].position $me.position) -le $Within) { return $near[0] }
        Start-Sleep -Seconds 1
    } while ((Get-Date) -lt $deadline)
    throw "No enemy came within $Within m"
}
# Bots ignore Tutorial, so meet them as NTF and change role in place (flag 0 keeps the position).
function MeetEnemiesAsTutorial {
    $null = Reply "/forcerole $actorId NtfPrivate"
    Start-Sleep -Seconds 3
    $null = NearestEnemy 18
    $null = Reply "/forcerole $actorId Tutorial 0"
    Start-Sleep -Seconds 1
    $null = Reply "/strip $actorId"
}
function Equip([string]$Id) {
    $null = Invoke-LabInput @{id=$Id;frames=150;inputFrames=2;keys=@($firearmHotkey);capture=$true}
    if ((Me).held -ne 'GunAK') { throw "AK not held: $((Me).held)" }
}
function HitLosses([string]$Name) {
    $losses = [Collections.Generic.List[double]]::new()
    for ($shot = 0; $shot -lt 24 -and $losses.Count -lt 6; $shot++) {
        $enemy = NearestEnemy 30
        $null = Set-LabAim -TargetActorId $enemy.id
        $null = Invoke-LabInput @{id="$Name-$shot";frames=20;inputFrames=2;keys=@($fire)}
        Start-Sleep -Milliseconds 250
        $after = @(Observe | Where-Object id -eq $enemy.id)[0]
        if ($after.life -eq $enemy.life -and $after.health -lt $enemy.health) {
            $losses.Add([Math]::Round($enemy.health - $after.health, 1))
        }
    }
    @($losses)
}
function ShotsOverHold([string]$Id) {
    $null = Set-LabLook -Yaw 0 -Pitch 40
    $before = (Me).shots
    $null = Invoke-LabInput @{id=$Id;frames=120;keys=@($fire);capture=$true}
    Start-Sleep -Milliseconds 300
    (Me).shots - $before
}
function Save { $results | ConvertTo-Json -Depth 6 | Set-Content "$($Context.Evidence)/special-weapons-ak12.json" -Encoding utf8 }

$null = Reply '/bot_warmup standard'
$null = Reply '/forcestart'
Start-Sleep -Seconds 5
if ((Reply '/special_weapon list') -notmatch 'ak-12') { throw 'AK-12 is not registered' }

# GunAK is item 40.
MeetEnemiesAsTutorial
$null = Reply "/give $actorId 40"
Start-Sleep -Milliseconds 500
Equip 'native-select'
$results.nativeHitLoss = HitLosses 'native-hit'
Save
$null = Reply "/strip $actorId"
$null = Reply "/give $actorId 40"
Start-Sleep -Milliseconds 500
Equip 'native-select-2'
$results.nativeShots = ShotsOverHold 'native-hold'
Save

MeetEnemiesAsTutorial
$reply = Reply "/special_weapon grant ak-12 $actorId"
if ($reply -notmatch 'Granted') { throw "Grant failed: $reply" }
Start-Sleep -Milliseconds 500
Equip 'ak12-select'
$results.ak12Screenshot = Invoke-LabScreenshot -Name 'ak12-held'
$results.ak12HitLoss = HitLosses 'ak12-hit'
Save
$null = Invoke-LabInput @{id='ak12-reload-before-hold';frames=300;inputFrames=2;keys=@($reloadKey)}
$results.ak12Shots = ShotsOverHold 'ak12-hold'
Save

$null = Invoke-LabInput @{id='ak12-drop';frames=40;inputFrames=2;keys=@($dropKey)}
if ((Me).items -notcontains 'GunAK') { throw 'The AK-12 was dropped' }

# Sustained fire through depletion, then a reload from the granted reserve.
for ($burst = 0; $burst -lt 6 -and (Me).firearm.ammo -gt 0; $burst++) {
    $null = Invoke-LabInput @{id="ak12-deplete-$burst";frames=120;keys=@($fire);capture=$true}
}
$results.ammoAfterDepletion = (Me).firearm
$null = Invoke-LabInput @{id='ak12-reload';frames=300;inputFrames=2;keys=@($reloadKey);capture=$true}
$results.ammoAfterReload = (Me).firearm
Save
if ($results.ammoAfterReload.ammo -le $results.ammoAfterDepletion.ammo) { throw 'The AK-12 did not reload' }

$null = Reply "/forcerole $actorId NtfPrivate"
Start-Sleep -Seconds 2
if ((Reply '/special_weapon status') -notmatch 'tracked=0') { throw 'A role change kept the AK-12' }
if ($results.ak12Shots -le $results.nativeShots) { throw "The AK-12 was not faster: $($results.ak12Shots) vs $($results.nativeShots)" }
