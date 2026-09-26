param($Context)
# StatsBots skill rating: a real client fires at live managed warmup bots; the rating must sample
# hits/shots/kills and combat time, show on the HUD and persist after the flush interval.
$ErrorActionPreference = 'Stop'
$dir = $PSScriptRoot
while ($dir -and !(Test-Path "$dir/.tests/offline-clients/tools/host-aim.ps1")) { $dir = Split-Path $dir -Parent }
if (!$dir) { throw 'Cannot locate .tests/offline-clients/tools/host-aim.ps1 from the scenario path' }
. "$dir/.tests/offline-clients/tools/host-aim.ps1"

$me = $Context.Actor.id
$uid = '76561190000000001@steam'
function Log($name, $value) { @{step=$name;value=$value} | ConvertTo-Json -Depth 8 -Compress | Add-Content "$($Context.Evidence)/skill.jsonl" }
# Observe hides dummies; bot targets come from the raw read-only observer snapshot.
function ObserveBots {
    $text = & "$($Context.Root)\LocalAdmin\LocalAdmin.exe" ctl $Context.Port console labobserve 2>&1
    $line = @($text | Where-Object { $_.ToString().StartsWith('OFFLINE_LAB_SNAPSHOT ') })[0]
    if (!$line) { throw 'Missing server observer snapshot' }
    return @(($line.ToString().Substring(21) | ConvertFrom-Json).players | Where-Object { $_.dummy -and $_.role -like 'Chaos*' -and $_.health -gt 0 })
}
function Status { $r = Invoke-LabServer "/statsbots status $uid"; Log 'status' $r; return "$r" }
function Field($text, $name) {
    $m = [regex]::Match($text, "(?<![A-Za-z])$name=([0-9.]+)")
    if (!$m.Success) { throw "Status lacks $name`: $text" }
    return [double]::Parse($m.Groups[1].Value, [Globalization.CultureInfo]::InvariantCulture)
}

# Arrange: warmup round, player as NTF with a held firearm.
Log 'roundlock' (Invoke-LabServer '/roundlock on')
Log 'forcestart' (Invoke-LabServer '/forcestart')
$null = Invoke-LabInput @{frames=300}
Log 'warmup' (Invoke-LabServer '/bot_warmup')
Log 'role' (Invoke-LabServer "/forcerole $me NtfPrivate")
$null = Invoke-LabInput @{frames=180}
$actor = @(Observe | Where-Object id -eq $me)[0]
if ($actor.role -ne 'NtfPrivate') { throw "Player role is $($actor.role), expected NtfPrivate" }
$guns = @($actor.items)
$slot = 0; for ($i = 0; $i -lt $guns.Count; $i++) { if ("$($guns[$i])" -like 'Gun*') { $slot = $i + 1; break } }
if ($slot -eq 0) { throw "No firearm in NTF inventory: $($guns -join ',')" }
Invoke-LabInventory -Slot $slot -Expect "$($guns[$slot-1])"
$before = Status

# Act: repeatedly aim at the nearest live managed bot and hold native fire.
$engagements = 0
for ($round = 0; $round -lt 14 -and $engagements -lt 8; $round++) {
    $self = @(Observe | Where-Object id -eq $me)[0]
    $bots = @(ObserveBots)
    $target = $bots | Sort-Object { [Math]::Pow($_.position.x - $self.position.x, 2) + [Math]::Pow($_.position.z - $self.position.z, 2) } | Select-Object -First 1
    if (!$target) { $null = Invoke-LabInput @{frames=120}; continue }
    $dist = [Math]::Sqrt([Math]::Pow($target.position.x - $self.position.x, 2) + [Math]::Pow($target.position.z - $self.position.z, 2))
    Log 'target' @{id=$target.id;distance=$dist}
    try { $null = Set-LabAim -Target @{x=$target.position.x; y=$target.position.y + 0.2; z=$target.position.z} }
    catch { Log 'aim-failed' "$_"; $null = Invoke-LabInput @{frames=60}; continue }
    $receipt = Invoke-LabInput @{id="fire-$round";frames=180;keys=@(323);capture=$true;audio=$true;expectAudio=$true}
    Log 'fire' $receipt
    $engagements++
}
if ($engagements -eq 0) { throw 'No managed bot was available to engage' }

# Assert: sampled live, then persisted after the flush interval.
$live = Status
$shots = Field $live 'shots'; $hits = Field $live 'hits'; $combat = Field $live 'combatSeconds'
if ($shots -le (Field $before 'shots')) { throw "Skill shots did not increase: $live" }
if ($hits -le 0) { throw "No firearm hit on a managed bot was sampled: $live" }
if ($combat -le 0) { throw "No combat time was sampled: $live" }
if ((Field $live 'accuracy') -gt 1) { throw "Accuracy above 1: $live" }
Invoke-LabScreenshot -Name hud | Out-Null
$null = Invoke-LabInput @{frames=2400}  # > flush_seconds (30 s) at 60 FPS
$flushed = Status
if ((Field $flushed 'pendingShots') -ne 0) { throw "Pending skill sample was not flushed: $flushed" }
if ((Field $flushed 'shots') -lt $shots - 0.01) { throw "Persisted shots lower than sampled: $flushed" }
@{before=$before;live=$live;flushed=$flushed;engagements=$engagements} | ConvertTo-Json -Depth 6 | Set-Content "$($Context.Evidence)/scenario.json" -Encoding utf8
