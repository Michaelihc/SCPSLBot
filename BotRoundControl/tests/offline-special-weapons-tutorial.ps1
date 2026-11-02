param($Context)
$ErrorActionPreference = 'Stop'

# Tutorial attackers against bots. Run through package-special-weapons.py with special-weapons-bindings.json.
$primaryUse = 104
$firearmHotkey = 49
$fire = 323
$actorId = $Context.Actor.id
$rows = [Collections.Generic.List[string]]::new()

function Me { @(Observe | Where-Object id -eq $actorId)[0] }
function Reply([string]$Command) { (Invoke-LabServer $Command) -join "`n" }
function Enemies { @(Observe | Where-Object { $_.id -ne $actorId -and $_.role -like 'Chaos*' -and $_.health -gt 0 }) }
function Distance($Left, $Right) {
    $dx = $Left.x - $Right.x; $dy = $Left.y - $Right.y; $dz = $Left.z - $Right.z
    [Math]::Sqrt(($dx * $dx) + ($dy * $dy) + ($dz * $dz))
}
function WaitEnemy([double]$Within) {
    $deadline = (Get-Date).AddSeconds(90)
    do {
        $me = Me
        $near = @(Enemies | Sort-Object { Distance $_.position $me.position } | Select-Object -First 1)
        if ($near.Count -and (Distance $near[0].position $me.position) -le $Within) { return $near[0] }
        Start-Sleep -Seconds 1
    } while ((Get-Date) -lt $deadline)
    throw "No enemy came within $Within m"
}
function State { $map = @{}; foreach ($enemy in Enemies) { $map[$enemy.id] = $enemy }; $map }
function WasHurt($Before) {
    $after = State
    foreach ($id in $Before.Keys) {
        if (!$after.ContainsKey($id) -or $after[$id].life -ne $Before[$id].life -or $after[$id].health -lt $Before[$id].health) {
            return $true
        }
    }
    $false
}
function Record([string]$Row) {
    $rows.Add($Row)
    $rows -join "`n" | Set-Content "$($Context.Evidence)/tutorial-hits.txt" -Encoding utf8
}
# Bots ignore Tutorial, so meet them as NTF and change role in place (flag 0 keeps the position).
function MeetEnemiesAsTutorial {
    $null = Reply "/forcerole $actorId NtfPrivate"
    Start-Sleep -Seconds 3
    $null = Reply "/god $actorId enable"
    $null = WaitEnemy 22
    $null = Reply "/forcerole $actorId Tutorial 0"
    Start-Sleep -Seconds 1
    $null = Reply "/strip $actorId"
    $null = Reply "/god $actorId enable"
}

$null = Reply '/bot_warmup standard'
$null = Reply '/forcestart'
Start-Sleep -Seconds 5

MeetEnemiesAsTutorial
$null = Reply "/special_weapon grant guided-rocket $actorId"
Start-Sleep -Milliseconds 500
$null = Invoke-LabInput @{id='select-rocket';frames=60;inputFrames=2;keys=@($firearmHotkey);capture=$true}
$enemy = WaitEnemy 30
$before = State
$null = Set-LabAim -TargetActorId $enemy.id
$null = Invoke-LabInput @{id='rocket';frames=170;inputFrames=2;keys=@($primaryUse);capture=$true}
Start-Sleep -Seconds 2
Record "rocket hurt=$(WasHurt $before) target=$($enemy.role)"

MeetEnemiesAsTutorial
$null = Reply "/special_weapon grant graviton-disruptor $actorId"
Start-Sleep -Milliseconds 500
$null = Invoke-LabInput @{id='select-graviton';frames=150;inputFrames=2;keys=@($firearmHotkey);capture=$true}
$enemy = WaitEnemy 22
$before = State
$me = Me
$dx = $me.position.x - $enemy.position.x
$dz = $me.position.z - $enemy.position.z
$length = [Math]::Max(0.1, [Math]::Sqrt(($dx * $dx) + ($dz * $dz)))
$null = Set-LabAim -Target @{x=$enemy.position.x + ($dx / $length * 2.5);y=$enemy.position.y - 0.9;z=$enemy.position.z + ($dz / $length * 2.5)}
$null = Invoke-LabInput @{id='shockwave';frames=150;inputFrames=2;keys=@($fire);capture=$true}
Start-Sleep -Seconds 1
Record "shockwave hurt=$(WasHurt $before) target=$($enemy.role)"