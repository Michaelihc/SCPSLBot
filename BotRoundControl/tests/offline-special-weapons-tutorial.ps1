param($Context)
$ErrorActionPreference = 'Stop'
$primaryUse = 104; $firearmHotkey = 49; $fire = 323
$actorId = $Context.Actor.id
function Me { @(Observe | Where-Object id -eq $actorId)[0] }
function Reply([string]$Command) { (Invoke-LabServer $Command) -join "`n" }
function Enemies { @(Observe | Where-Object { $_.id -ne $actorId -and $_.role -like 'Chaos*' -and $_.health -gt 0 }) }
function Distance($a, $b) { $x=$a.x-$b.x; $y=$a.y-$b.y; $z=$a.z-$b.z; [Math]::Sqrt($x*$x+$y*$y+$z*$z) }
function WaitEnemy([double]$Within) {
  $end=(Get-Date).AddSeconds(90)
  do { $m=Me; $n=@(Enemies | Sort-Object { Distance $_.position $m.position } | Select-Object -First 1)
       if ($n.Count -and (Distance $n[0].position $m.position) -le $Within) { return $n[0] }; Start-Sleep 1 } while ((Get-Date) -lt $end)
  throw 'No enemy close enough'
}
function State { $h=@{}; foreach ($e in Enemies) { $h[$e.id]=$e }; $h }
function Hurt($b) { $a=State; foreach ($k in $b.Keys) { if (!$a.ContainsKey($k) -or $a[$k].life -ne $b[$k].life -or $a[$k].health -lt $b[$k].health) { return $true } }; $false }
$null = Reply '/bot_warmup standard'; $null = Reply '/forcestart'; Start-Sleep 5
$null = Reply "/forcerole $actorId Tutorial"; Start-Sleep 3; $null = Reply "/strip $actorId"; $null = Reply "/god $actorId enable"
$rows=[Collections.Generic.List[string]]::new()
$null = Reply "/special_weapon grant guided-rocket $actorId"; Start-Sleep -Milliseconds 500
$null = Invoke-LabInput @{id='select';frames=60;inputFrames=2;keys=@($firearmHotkey);capture=$true}
foreach ($shot in 1..2) {
  $e = WaitEnemy 25; $before = State
  $null = Set-LabAim -TargetActorId $e.id
  $null = Invoke-LabInput @{id="rocket-$shot";frames=170;inputFrames=2;keys=@($primaryUse);capture=$true}
  Start-Sleep 2
  $rows.Add("rocket $shot hurt=$(Hurt $before) target=$($e.id) role=$($e.role) dist=$([Math]::Round((Distance $e.position (Me).position),1)) items=$((Me).items -join ',')")
}
$null = Reply "/strip $actorId"; Start-Sleep 1
$null = Reply "/special_weapon grant graviton-disruptor $actorId"; Start-Sleep -Milliseconds 500
$null = Invoke-LabInput @{id='select-g';frames=150;inputFrames=2;keys=@($firearmHotkey);capture=$true}
$e = WaitEnemy 18; $before = State
$m=Me; $dx=$m.position.x-$e.position.x; $dz=$m.position.z-$e.position.z; $len=[Math]::Max(0.1,[Math]::Sqrt($dx*$dx+$dz*$dz))
$null = Set-LabAim -Target @{x=$e.position.x+($dx/$len*2.5);y=$e.position.y-0.9;z=$e.position.z+($dz/$len*2.5)}
$null = Invoke-LabInput @{id='shockwave';frames=150;inputFrames=2;keys=@($fire);capture=$true}
Start-Sleep 1
$rows.Add("shockwave hurt=$(Hurt $before)")
$rows -join "`n" | Set-Content "$($Context.Evidence)/tutorial-repro.txt" -Encoding utf8
