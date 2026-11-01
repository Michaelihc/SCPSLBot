param($Context)
$ErrorActionPreference = 'Stop'

# Run through BotRoundControl/tests/package-special-weapons.py and special-weapons-bindings.json.
# Primary Use is bound to H, so a native Mouse0 shot cannot stand in for a rocket launch.
$primaryUse = 104
$firearmHotkey = 49
$dropKey = 116
$fire = 323
$actorId = $Context.Actor.id
$checks = [Collections.Generic.List[string]]::new()

function Me { @(Observe | Where-Object id -eq $actorId)[0] }
function Reply([string]$Command) { (Invoke-LabServer $Command) -join "`n" }
function Status { Reply '/special_weapon status' }
function Grant([string]$Weapon) {
    $reply = Reply "/special_weapon grant $Weapon $actorId"
    if ($reply -notmatch 'Granted') { throw "Grant of $Weapon failed: $reply" }
    Start-Sleep -Milliseconds 500
}
function Tap([string]$Id, [int]$Key, [int]$Frames = 30) {
    Invoke-LabInput @{id=$Id;frames=$Frames;inputFrames=2;keys=@($Key);capture=$true;audio=$true}
}
function Enemies {
    @(Observe | Where-Object { $_.id -ne $actorId -and $_.role -like 'Chaos*' -and $_.health -gt 0 })
}
function Distance($Left, $Right) {
    $dx = $Left.x - $Right.x; $dy = $Left.y - $Right.y; $dz = $Left.z - $Right.z
    [Math]::Sqrt(($dx * $dx) + ($dy * $dy) + ($dz * $dz))
}
function WaitForEnemy([double]$Within, [int]$Seconds = 90) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    do {
        $me = Me
        $near = @(Enemies | Sort-Object { Distance $_.position $me.position } | Select-Object -First 1)
        if ($near.Count -and (Distance $near[0].position $me.position) -le $Within) { return $near[0] }
        Start-Sleep -Seconds 1
    } while ((Get-Date) -lt $deadline)
    throw "No enemy came within $Within m: $(Observe | ConvertTo-Json -Depth 6 -Compress)"
}
# A killed bot respawns at full health under the same id, so a new life also counts as a hit.
function EnemyState { $map = @{}; foreach ($enemy in Enemies) { $map[$enemy.id] = $enemy }; $map }
function WasHurt($Before) {
    $after = EnemyState
    foreach ($id in $Before.Keys) {
        if (!$after.ContainsKey($id) -or $after[$id].life -ne $Before[$id].life -or $after[$id].health -lt $Before[$id].health) {
            return $true
        }
    }
    $false
}

$list = Reply '/special_weapon list'
if ($list -notmatch 'graviton-disruptor' -or $list -notmatch 'guided-rocket') { throw "Weapons missing: $list" }
$null = Reply '/bot_warmup standard'
$null = Reply '/forcestart'
Start-Sleep -Seconds 5
$null = Reply "/forcerole $actorId NtfPrivate"
Start-Sleep -Seconds 3
$null = Reply "/strip $actorId"
$null = Reply "/god $actorId enable"
if ((Status) -notmatch 'enabled=True tracked=0') { throw "Runtime not running: $(Status)" }

$bindings = Invoke-LabSetup 'bindings'
$bindings | ConvertTo-Json -Depth 10 | Set-Content "$($Context.Evidence)/special-weapons-bindings.json" -Encoding utf8
if (($bindings | ConvertTo-Json -Depth 10) -notmatch '1090001') { throw 'The client did not receive Primary Use' }
$checks.Add('client received the shared Primary Use key')

# Guided rocket: launcher replaces the native viewmodel, and only the bound key fires.
Grant 'guided-rocket'
if ((Me).items -notcontains 'GunShotgun') { throw 'Launcher carrier was not granted' }
$null = Tap 'rocket-select' $firearmHotkey 60
if ((Me).held -ne 'None') { throw "Native viewmodel was not hidden: $((Me).held)" }
$launcherShot = Invoke-LabScreenshot -Name rocket-held
$checks.Add('selecting the launcher hides the native shotgun')

$enemy = WaitForEnemy 30
$before = EnemyState
$null = Set-LabAim -TargetActorId $enemy.id
$null = Tap 'rocket-first-shot' $primaryUse 12
$null = Tap 'rocket-press-during-reload' $primaryUse 12
if ((Me).items -notcontains 'GunShotgun') { throw 'Launcher spent during reload' }
$reloadShot = Invoke-LabScreenshot -Name rocket-reloading
$null = Invoke-LabInput @{id='rocket-reload-wait';frames=180;capture=$true;audio=$true}

$enemy = WaitForEnemy 30
$null = Set-LabAim -TargetActorId $enemy.id
$null = Tap 'rocket-second-shot' $primaryUse 170
if ((Me).items -notcontains 'GunShotgun') { throw 'A press during reload spent a rocket' }
$checks.Add('reload gates the next launch')

$enemy = WaitForEnemy 30
$null = Set-LabAim -TargetActorId $enemy.id
$null = Tap 'rocket-third-shot' $primaryUse 90
if ((Me).items -contains 'GunShotgun') { throw 'Spent launcher stayed in the inventory' }
if ((Status) -notmatch 'tracked=0') { throw "Spent launcher stayed tracked: $(Status)" }
$checks.Add('the last rocket consumes the launcher and clears tracking')

$deadline = (Get-Date).AddSeconds(8)
while (!(WasHurt $before) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
if (!(WasHurt $before)) { throw 'No rocket damaged an enemy' }
$checks.Add('rockets damage enemies')

# Graviton: bound to the player, and a floor hit beside an enemy reaches it through the shockwave.
Grant 'graviton-disruptor'
$null = Tap 'graviton-select' $firearmHotkey 150
if ((Me).held -ne 'ParticleDisruptor') { throw "Disruptor not held: $((Me).held)" }
$null = Tap 'graviton-drop-attempt' $dropKey 40
if ((Me).items -notcontains 'ParticleDisruptor') { throw 'A bound weapon was dropped' }
$checks.Add('a bound weapon cannot be dropped')

$enemy = WaitForEnemy 22
$before = EnemyState
$me = Me
$toPlayer = @{x=$me.position.x-$enemy.position.x;z=$me.position.z-$enemy.position.z}
$length = [Math]::Max(0.1, [Math]::Sqrt(($toPlayer.x * $toPlayer.x) + ($toPlayer.z * $toPlayer.z)))
$floor = @{
    x=$enemy.position.x + ($toPlayer.x / $length * 2.5)
    y=$enemy.position.y - 0.9
    z=$enemy.position.z + ($toPlayer.z / $length * 2.5)
}
$null = Set-LabAim -Target $floor
$null = Tap 'graviton-shockwave' $fire 150
if (!(WasHurt $before)) { throw 'The shockwave beside an enemy dealt no damage' }
$shockwaveShot = Invoke-LabScreenshot -Name graviton-after-shockwave
$checks.Add('a floor shot damages a nearby enemy through the shockwave')

# Sustained native fire through depletion: the carrier ending must end tracking.
for ($burst = 0; $burst -lt 12 -and (Me).items -contains 'ParticleDisruptor'; $burst++) {
    $null = Tap "graviton-depletion-$burst" $fire 120
}
Start-Sleep -Seconds 1
$depleted = (Me).items -notcontains 'ParticleDisruptor'
"depleted=$depleted status=$(Status) items=$((Me).items -join ',')" | Set-Content "$($Context.Evidence)/special-weapons-depletion.txt" -Encoding utf8
if ($depleted -and (Status) -notmatch 'tracked=0') { throw "A consumed disruptor stayed tracked: $(Status)" }
$checks.Add("sustained fire through depletion (consumed=$depleted)")

# Lifecycle: a weapon ends with the life and with Standard warmup.
$null = Reply "/strip $actorId"
Start-Sleep -Seconds 1
if ((Status) -notmatch 'tracked=0 selected=0') { throw "Inventory clear left state: $(Status)" }
Grant 'guided-rocket'
$null = Tap 'rocket-reselect' $firearmHotkey 40
$null = Reply "/forcerole $actorId ChaosConscript"
Start-Sleep -Seconds 2
if ((Me).items -contains 'GunShotgun' -or (Status) -notmatch 'tracked=0 selected=0') { throw "Role change kept the weapon: $(Status)" }
$checks.Add('a role change removes the weapon')

$null = Reply "/forcerole $actorId NtfPrivate"
Start-Sleep -Seconds 2
$null = Reply "/god $actorId enable"
Grant 'graviton-disruptor'
$null = Reply '/bot_warmup none'
Start-Sleep -Seconds 2
if ((Me).items -contains 'ParticleDisruptor') { throw 'Leaving warmup kept the weapon' }
if ((Status) -notmatch 'enabled=False tracked=0') { throw "Leaving warmup kept state: $(Status)" }
$null = Reply '/bot_warmup standard'
Start-Sleep -Seconds 2
if ((Status) -notmatch 'enabled=True tracked=0') { throw "Runtime did not return: $(Status)" }
$checks.Add('leaving Standard warmup takes weapons back and returning re-enables them')

# Duel viewers may not shoot, and a key-fired rocket answers the same policy as a native shot.
$null = Invoke-LabSetup '.duel'
Start-Sleep -Seconds 3
$duelStatus = Reply '/duel_status'
Grant 'guided-rocket'
$null = Tap 'duel-rocket-select' $firearmHotkey 40
foreach ($press in 1..4) {
    $null = Tap "duel-rocket-press-$press" $primaryUse 160
}
if ((Me).items -notcontains 'GunShotgun') { throw "A duel viewer fired rockets: $duelStatus" }
$checks.Add('a duel viewer cannot fire')
$null = Invoke-LabSetup '.leave'

@{checks=@($checks);launcher=$launcherShot;reload=$reloadShot;shockwave=$shockwaveShot;duel=$duelStatus} |
    ConvertTo-Json -Depth 8 | Set-Content "$($Context.Evidence)/special-weapons-walkthrough.json" -Encoding utf8
