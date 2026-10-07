param($Context)
$ErrorActionPreference = 'Stop'
if ($Context.ClientIds.Count -ne 3) { throw 'This walkthrough needs three authenticated clients' }
$duelClients = @($Context.ClientIds)
$duelActors = @($Context.Actors)
$duelOwner = $duelActors[0].id
$duelOpponent = $duelActors[1].id
$duelViewer = $duelActors[2].id
$duelChecks = [Collections.Generic.List[string]]::new()
function DuelActor([int]$Id) { @(Observe -ClientsOnly | Where-Object id -eq $Id)[0] }
function DuelPoint([double]$X,[double]$Y,[double]$Z) { @{x=$duelOrigin.x+$X;y=$duelOrigin.y+$Y;z=$duelOrigin.z+$Z} }
function DuelPress([double]$X,[double]$Y,[int]$Key,[string]$Name) {
    $null = Set-LabAim -Target (DuelPoint $X $Y -10.5) -ClientId $duelClients[0] -ActorId $duelOwner
    Invoke-LabInput @{id=$Name;frames=60;inputFrames=2;keys=@($Key);capture=$true;audio=$true} -ClientId $duelClients[0]
}
$null = Invoke-LabServer '/forcestart'
Start-Sleep -Seconds 4
foreach ($actor in $duelActors) { $null = Invoke-LabServer "/forcerole $($actor.id) NtfPrivate" }
Start-Sleep -Seconds 2
$null = Invoke-LabSetup '.duel' -ClientId $duelClients[0]
Start-Sleep -Seconds 2
$entered = DuelActor $duelOwner
$duelOrigin = @{x=$entered.position.x;y=$entered.position.y-1;z=$entered.position.z+13}
# Root settles on the floor after entry; use the model's floor top plus FPC half-height.
$status = (Invoke-LabServer '/duel_status') -join ' '
if ($status -notmatch 'origin=\(([-\d.]+), ([-\d.]+), ([-\d.]+)\)') { throw "Missing arena position in status: $status" }
$duelOrigin = @{x=[double]$Matches[1];y=[double]$Matches[2];z=[double]$Matches[3]}
$null = Invoke-LabSetup '.duel' -ClientId $duelClients[1]
if ((DuelActor $duelOpponent).position.y -gt $duelOrigin.y-50) { throw 'A second challenger entered the reserved arena' }
$duelChecks.Add('single reservation')
$gunActor = DuelActor $duelOwner
if ($gunActor.held -ne 'GunE11SR') { $null = Invoke-LabInventory -Slot 1 -Expect GunE11SR -ClientId $duelClients[0] -ActorId $duelOwner }
$null = DuelPress -1.15 2.05 323 'select-first-name'
# Move near the left-hand row through native walking, then E toggles the selected name.
$null = Set-LabLook -Yaw 0 -Pitch 0 -ClientId $duelClients[0] -ActorId $duelOwner
$null = Invoke-LabInput @{frames=12;keys=@(119)} -ClientId $duelClients[0]
$null = Invoke-LabInput @{frames=18;keys=@(97)} -ClientId $duelClients[0]
$null = DuelPress -1.15 2.05 101 'interact-first-name'
$null = Set-LabLook -Yaw 0 -Pitch 0 -ClientId $duelClients[0] -ActorId $duelOwner
$null = Invoke-LabInput @{frames=18;keys=@(100)} -ClientId $duelClients[0]
$null = Invoke-LabInput @{frames=12;keys=@(115)} -ClientId $duelClients[0]
$null = DuelPress 1.15 2.05 323 'select-second-name'
$null = DuelPress -1.15 0.7 323 'select-everyone'
$board = Invoke-LabScreenshot -Name duel-selected-board -ClientId $duelClients[0]
$null = DuelPress 1.15 0.7 323 'send-duel-invitations'
$null = Invoke-LabSetup '.yes' -ClientId $duelClients[1]
Start-Sleep -Seconds 1
$first = DuelActor $duelOpponent
if ($first.role -ne 'ChaosConscript' -or $first.position.y -lt $duelOrigin.y-2) { throw 'First acceptance did not enter the duel' }
$null = Invoke-LabSetup '.accept' -ClientId $duelClients[2]
if ((DuelActor $duelViewer).position.y -gt $duelOrigin.y-50) { throw 'Late acceptance entered the active match' }
$duelChecks.Add('first acceptance wins')
$null = Invoke-LabSetup '.watch' -ClientId $duelClients[2]
Start-Sleep -Seconds 2
$viewing = DuelActor $duelViewer
if ([Math]::Abs($viewing.position.z-($duelOrigin.z+18)) -gt 3) { throw 'Spectator did not enter the protected viewing deck' }
$null = Set-LabAim -Target (DuelPoint 0 0 0) -ClientId $duelClients[2] -ActorId $duelViewer
$viewShot = Invoke-LabScreenshot -Name duel-viewing-deck -ClientId $duelClients[2]
$null = Invoke-LabSetup '.spectate' -ClientId $duelClients[2]
Start-Sleep -Seconds 2
if ((DuelActor $duelViewer).position.y -gt $duelOrigin.y-50) { throw 'Spectator toggle did not return to Surface' }
$duelChecks.Add('spectator toggle')
# Sustained native fire into the side wall, depletion, reload, then a lethal native fight.
$gunActor = DuelActor $duelOwner
if ($gunActor.held -ne 'GunE11SR') { $null = Invoke-LabInventory -Slot 1 -Expect GunE11SR -ClientId $duelClients[0] -ActorId $duelOwner }
$null = Set-LabAim -Target (DuelPoint 9.95 1 -8) -ClientId $duelClients[0] -ActorId $duelOwner
$empty = Invoke-LabInput @{id='duel-sustained-fire';frames=420;keys=@(323);capture=$true;audio=$true;expectAudio=$true} -ClientId $duelClients[0]
if ((DuelActor $duelOwner).firearm.ammo -ne 0) { throw 'Sustained fire did not deplete the magazine' }
$reload = Invoke-LabInput @{id='duel-reload';frames=240;inputFrames=2;keys=@(114);capture=$true;audio=$true} -ClientId $duelClients[0]
if ((DuelActor $duelOwner).firearm.ammo -le 0) { throw 'Native reload did not refill the magazine' }
$duelChecks.Add('native depletion and reload')
$target = DuelActor $duelOpponent
$null = Set-LabAim -Target @{x=$target.position.x;y=$target.position.y+0.45;z=$target.position.z} -ClientId $duelClients[0] -ActorId $duelOwner
$fight = Invoke-LabInput @{id='duel-native-fight';frames=180;keys=@(323);capture=$true;audio=$true;expectAudio=$true} -ClientId $duelClients[0]
Start-Sleep -Seconds 2
$afterOwner = DuelActor $duelOwner
$afterOpponent = DuelActor $duelOpponent
if ($afterOwner.position.y -gt $duelOrigin.y-50 -or $afterOpponent.position.y -gt $duelOrigin.y-50) { throw 'Native duel death did not release both participants to Surface' }
$duelChecks.Add('combat and death cleanup')
# A fresh entry proves the slot was released after native death.
$null = Invoke-LabSetup '.duel' -ClientId $duelClients[2]
Start-Sleep -Seconds 1
if ((DuelActor $duelViewer).position.y -lt $duelOrigin.y-2) { throw 'Released match slot could not be reused' }
$null = Invoke-LabSetup '.leave' -ClientId $duelClients[2]
$duelChecks.Add('slot reuse')
@{checks=@($duelChecks);board=$board;viewer=$viewShot;depletion=$empty;reload=$reload;fight=$fight;owner=$afterOwner;opponent=$afterOpponent} |
    ConvertTo-Json -Depth 10 | Set-Content "$($Context.Evidence)/duel-walkthrough.json" -Encoding utf8
