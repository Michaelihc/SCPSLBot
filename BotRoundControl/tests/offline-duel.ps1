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
function AssertDuelViewer([int]$Id) {
    $actor = DuelActor $Id
    if ($actor.role -ne 'NtfPrivate' -or [Math]::Abs($actor.position.z-($duelOrigin.z+18)) -gt 3 -or [Math]::Abs($actor.position.y-($duelOrigin.y+3.5)) -gt 2) {
        throw "Player $Id did not remain on the protected viewing deck: $($actor | ConvertTo-Json -Depth 6 -Compress)"
    }
    if (@($actor.items).Count -ne 0) { throw "Viewer $Id retained a combat loadout" }
}
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
Start-Sleep -Seconds 1
AssertDuelViewer $duelOpponent
$null = Invoke-LabSetup '.watch' -ClientId $duelClients[2]
Start-Sleep -Seconds 1
AssertDuelViewer $duelViewer
AssertDuelViewer $duelOpponent
$duelChecks.Add('single reservation with multiple lobby viewers')
$null = Invoke-LabSetup '.challenge 999999' -ClientId $duelClients[2]
if (((Invoke-LabServer '/duel_status') -join ' ') -notmatch 'requests=0') { throw 'Invalid target created a challenge request' }
$null = Invoke-LabSetup '.challenge' -ClientId $duelClients[2]
$null = Invoke-LabSetup '.challenge' -ClientId $duelClients[2]
if (((Invoke-LabServer '/duel_status') -join ' ') -notmatch 'requests=1') { throw 'Repeated viewer requests were duplicated or missing' }
$null = Invoke-LabSetup ".accept $duelViewer" -ClientId $duelClients[0]
Start-Sleep -Seconds 1
if ((DuelActor $duelViewer).role -ne 'ChaosConscript') { throw 'Arena player could not accept an incoming viewer challenge' }
AssertDuelViewer $duelOpponent
$null = Invoke-LabSetup '.leave' -ClientId $duelClients[2]
Start-Sleep -Seconds 1
AssertDuelViewer $duelOwner
$duelChecks.Add('incoming viewer challenge, duplicate replacement and invalid target rejection')
$null = Invoke-LabSetup '.duel' -ClientId $duelClients[0]
$null = Invoke-LabSetup '.watch' -ClientId $duelClients[2]
Start-Sleep -Seconds 1
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
$invitation = Invoke-LabScreenshot -Name duel-arena-invitation -ClientId $duelClients[1]
$null = Invoke-LabSetup '.yes' -ClientId $duelClients[1]
Start-Sleep -Seconds 1
$first = DuelActor $duelOpponent
if ($first.role -ne 'ChaosConscript' -or $first.position.y -lt $duelOrigin.y-2) { throw 'First acceptance did not enter the duel' }
$null = Invoke-LabSetup '.accept' -ClientId $duelClients[2]
AssertDuelViewer $duelViewer
$duelChecks.Add('first acceptance wins')
$null = Invoke-LabSetup ".challenge $duelOwner" -ClientId $duelClients[2]
$null = Invoke-LabSetup ".accept $duelViewer" -ClientId $duelClients[0]
if ((DuelActor $duelOpponent).role -ne 'ChaosConscript') { throw 'Incoming challenge interrupted the current opponent' }
AssertDuelViewer $duelViewer
if (((Invoke-LabServer '/duel_status') -join ' ') -notmatch 'state=fighting.*requests=1') { throw 'Incoming challenge was not queued during combat' }
$duelChecks.Add('incoming request cannot interrupt active 1v1')
# Leaving the deck cancels the first request; requeue after the toggle checks below.
$null = Invoke-LabSetup '.watch' -ClientId $duelClients[2]
Start-Sleep -Seconds 2
if ((DuelActor $duelViewer).position.y -gt $duelOrigin.y-50) { throw 'Spectator toggle did not return to Surface' }
$null = Invoke-LabSetup '.duel' -ClientId $duelClients[2]
Start-Sleep -Seconds 2
$viewing = DuelActor $duelViewer
if ([Math]::Abs($viewing.position.z-($duelOrigin.z+18)) -gt 3) { throw 'Spectator did not enter the protected viewing deck' }
$null = Set-LabAim -Target (DuelPoint 0 0 0) -ClientId $duelClients[2] -ActorId $duelViewer
$viewShot = Invoke-LabScreenshot -Name duel-viewing-deck -ClientId $duelClients[2]
$null = Invoke-LabSetup '.spectate' -ClientId $duelClients[2]
Start-Sleep -Seconds 2
if ((DuelActor $duelViewer).position.y -gt $duelOrigin.y-50) { throw 'Spectator toggle did not return to Surface' }
$duelChecks.Add('spectator toggle')
$null = Invoke-LabSetup '.view' -ClientId $duelClients[2]
Start-Sleep -Seconds 1
AssertDuelViewer $duelViewer
$null = Invoke-LabSetup ".challenge $duelOwner" -ClientId $duelClients[2]
# Combatants cannot damage viewers; their bullets still use the native firearm path.
$gunActor = DuelActor $duelOwner
if ($gunActor.held -ne 'GunE11SR') { $null = Invoke-LabInventory -Slot 1 -Expect GunE11SR -ClientId $duelClients[0] -ActorId $duelOwner }
$protectedViewer = DuelActor $duelViewer
$null = Set-LabAim -Target @{x=$protectedViewer.position.x;y=$protectedViewer.position.y+0.2;z=$protectedViewer.position.z} -ClientId $duelClients[0] -ActorId $duelOwner
$null = Invoke-LabInput @{id='duel-viewer-protection';frames=60;inputFrames=12;keys=@(323,324);capture=$true;audio=$true;expectAudio=$true} -ClientId $duelClients[0]
if ((DuelActor $duelViewer).health -ne $protectedViewer.health) { throw 'Combatant damaged a protected viewer' }
$duelChecks.Add('viewer damage isolation')
# Sustained native fire into the side wall, depletion, reload, then a lethal native fight.
$gunActor = DuelActor $duelOwner
if ($gunActor.held -ne 'GunE11SR') { $null = Invoke-LabInventory -Slot 1 -Expect GunE11SR -ClientId $duelClients[0] -ActorId $duelOwner }
$null = Set-LabAim -Target (DuelPoint 9.95 1 -8) -ClientId $duelClients[0] -ActorId $duelOwner
$empty = Invoke-LabInput @{id='duel-sustained-fire';frames=420;keys=@(323);capture=$true;audio=$true;expectAudio=$true} -ClientId $duelClients[0]
if ((DuelActor $duelOwner).firearm.ammo -ne 0) { throw 'Sustained fire did not deplete the magazine' }
$reload = Invoke-LabInput @{id='duel-reload';frames=240;inputFrames=2;keys=@(114);capture=$true;audio=$true} -ClientId $duelClients[0]
if ((DuelActor $duelOwner).firearm.ammo -le 0) { throw 'Native reload did not refill the magazine' }
$duelChecks.Add('native depletion and reload')
$fight = @()
# Native recoil lifts long bursts above the target. Reacquire the torso between short bursts.
for ($burst = 0; $burst -lt 12; $burst++) {
    $target = DuelActor $duelOpponent
    if ($target.role -ne 'ChaosConscript') { break }
    $null = Set-LabAim -Target @{x=$target.position.x;y=$target.position.y+0.2;z=$target.position.z} -ClientId $duelClients[0] -ActorId $duelOwner
    $fight += Invoke-LabInput @{id="duel-native-fight-$burst";frames=30;inputFrames=12;keys=@(323,324);capture=$true;audio=$true;expectAudio=$true} -ClientId $duelClients[0]
}
Start-Sleep -Seconds 2
$afterOwner = DuelActor $duelOwner
$afterOpponent = DuelActor $duelOpponent
if ($afterOpponent.role -eq 'ChaosConscript') {
    @{owner=$afterOwner;opponent=$afterOpponent;status=(Invoke-LabServer '/duel_status')} | ConvertTo-Json -Depth 10 | Set-Content "$($Context.Evidence)/duel-fight-failure.json" -Encoding utf8
    throw 'Native duel fire did not finish the match'
}
AssertDuelViewer $duelOwner
AssertDuelViewer $duelOpponent
AssertDuelViewer $duelViewer
if (((Invoke-LabServer '/duel_status') -join ' ') -notmatch 'state=idle.*viewers=3') { throw 'Ended match did not retain all three viewers' }
$null = Set-LabAim -Target (DuelPoint 0 1 0) -ClientId $duelClients[0] -ActorId $duelOwner
$afterMatchShot = Invoke-LabScreenshot -Name duel-post-match-viewers -ClientId $duelClients[0]
$duelChecks.Add('native death returns both fighters to viewers and retains existing viewers')
$null = Invoke-LabSetup '.duel' -ClientId $duelClients[0]
$null = Invoke-LabSetup ".accept $duelViewer" -ClientId $duelClients[0]
Start-Sleep -Seconds 1
if ((DuelActor $duelViewer).role -ne 'ChaosConscript') { throw 'Surviving fighter could not accept the queued viewer challenge' }
AssertDuelViewer $duelOpponent
$null = Invoke-LabSetup '.leave' -ClientId $duelClients[2]
Start-Sleep -Seconds 1
AssertDuelViewer $duelOwner
$null = Invoke-LabSetup '.watch' -ClientId $duelClients[2]
Start-Sleep -Seconds 1
$duelChecks.Add('queued request survives match end and starts a subsequent 1v1')
# A viewer can challenge and invite another viewer for a rematch.
$null = Invoke-LabSetup '.duel' -ClientId $duelClients[0]
Start-Sleep -Seconds 1
$gunActor = DuelActor $duelOwner
if ($gunActor.held -ne 'GunE11SR') { $null = Invoke-LabInventory -Slot 1 -Expect GunE11SR -ClientId $duelClients[0] -ActorId $duelOwner }
$null = DuelPress -1.15 0.7 323 'rematch-select-viewers'
$null = DuelPress 1.15 0.7 323 'rematch-invite-viewers'
$null = Invoke-LabSetup '.accept' -ClientId $duelClients[1]
Start-Sleep -Seconds 1
if ((DuelActor $duelOpponent).role -ne 'ChaosConscript') { throw 'Viewer could not accept a rematch' }
$null = Invoke-LabSetup '.leave' -ClientId $duelClients[1]
Start-Sleep -Seconds 1
if ((DuelActor $duelOpponent).position.y -gt $duelOrigin.y-50) { throw 'Explicit forfeit did not return the departing fighter to Surface' }
AssertDuelViewer $duelOwner
AssertDuelViewer $duelViewer
$duelChecks.Add('viewer rematch and explicit forfeit')
# A fresh entry proves the slot was released after native death.
$null = Invoke-LabSetup '.duel' -ClientId $duelClients[2]
Start-Sleep -Seconds 1
if ((DuelActor $duelViewer).position.y -lt $duelOrigin.y-2) { throw 'Released match slot could not be reused' }
$null = Invoke-LabSetup '.leave' -ClientId $duelClients[2]
$duelChecks.Add('slot reuse')
# Viewing remains available with no challenger or active match.
$null = Invoke-LabSetup '.watch' -ClientId $duelClients[2]
Start-Sleep -Seconds 1
AssertDuelViewer $duelViewer
$null = Invoke-LabSetup '.watch' -ClientId $duelClients[2]
Start-Sleep -Seconds 1
if ((DuelActor $duelViewer).position.y -gt $duelOrigin.y-50) { throw 'Idle spectator toggle did not return to Surface' }
$duelChecks.Add('idle viewing and explicit exit')
$null = Invoke-LabSetup '.duel' -ClientId $duelClients[2]
$null = Invoke-LabSetup '.challenge' -ClientId $duelClients[0]
$null = Invoke-LabServer "/forcerole $duelViewer Tutorial"
Start-Sleep -Seconds 1
if ((DuelActor $duelViewer).role -ne 'Tutorial' -or ((Invoke-LabServer '/duel_status') -join ' ') -notmatch 'state=idle') { throw 'External Tutorial transition retained duel ownership or changed the native role' }
if (((Invoke-LabServer '/duel_status') -join ' ') -notmatch 'requests=0') { throw 'External role transition retained incoming requests' }
$duelChecks.Add('external role transition')
$null = Invoke-LabServer "/forcerole $duelViewer NtfPrivate"
$null = Invoke-LabSetup '.duel' -ClientId $duelClients[2]
$null = Invoke-LabSetup '.challenge' -ClientId $duelClients[0]
$null = Invoke-LabSetup 'disconnect' -ClientId $duelClients[2]
Start-Sleep -Seconds 2
if (((Invoke-LabServer '/duel_status') -join ' ') -notmatch 'state=idle') { throw 'Disconnected challenger retained the slot' }
if (((Invoke-LabServer '/duel_status') -join ' ') -notmatch 'requests=0') { throw 'Disconnected target retained incoming requests' }
$null = Invoke-LabSetup '.duel' -ClientId $duelClients[0]
if ((DuelActor $duelOwner).position.y -lt $duelOrigin.y-2) { throw 'Disconnected challenger slot could not be reused' }
$null = Invoke-LabSetup '.leave' -ClientId $duelClients[0]
$duelChecks.Add('native disconnect and slot reuse')
@{checks=@($duelChecks);board=$board;invitation=$invitation;viewer=$viewShot;postMatch=$afterMatchShot;depletion=$empty;reload=$reload;fight=$fight;owner=$afterOwner;opponent=$afterOpponent} |
    ConvertTo-Json -Depth 10 | Set-Content "$($Context.Evidence)/duel-walkthrough.json" -Encoding utf8
