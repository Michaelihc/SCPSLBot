param($Context)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/offline-invitations.ps1" -Context $Context
# The invitation walkthrough leaves 0+1 fighting and 2+3 queued.
$null = Invoke-LabInventory -Slot 1 -Expect GunE11SR -ClientId $duelClients[0] -ActorId $duelIds[0]
$viewerBefore = DuelActor 2
$null = Set-LabAim -Target @{x=$viewerBefore.position.x;y=$viewerBefore.position.y+0.2;z=$viewerBefore.position.z} -ClientId $duelClients[0] -ActorId $duelIds[0]
$null = Invoke-LabInput @{id='duel-viewer-protection';frames=60;inputFrames=12;keys=@(323,324);capture=$true;audio=$true;expectAudio=$true} -ClientId $duelClients[0]
if ((DuelActor 2).health -ne $viewerBefore.health) { throw 'Active fighter damaged a queued viewer' }
$duelChecks.Add('queued viewers remain protected from native combat damage')
$null = Set-LabAim -Target (DuelPoint 9.95 1 -8) -ClientId $duelClients[0] -ActorId $duelIds[0]
$empty = Invoke-LabInput @{id='duel-depletion';frames=420;keys=@(323);capture=$true;audio=$true;expectAudio=$true} -ClientId $duelClients[0]
if ((DuelActor 0).firearm.ammo -ne 0) { throw 'Sustained fire did not deplete the magazine' }
$reload = Invoke-LabInput @{id='duel-reload';frames=240;inputFrames=2;keys=@(114);capture=$true;audio=$true} -ClientId $duelClients[0]
if ((DuelActor 0).firearm.ammo -le 0) { throw 'Native reload did not refill the magazine' }
$duelChecks.Add('sustained native fire through depletion and reload')
$fight = @()
for ($burst = 0; $burst -lt 12; $burst++) {
    $target = DuelActor 1
    if ($target.role -ne 'ChaosConscript') { break }
    $null = Set-LabAim -Target @{x=$target.position.x;y=$target.position.y+0.2;z=$target.position.z} -ClientId $duelClients[0] -ActorId $duelIds[0]
    $fight += Invoke-LabInput @{id="duel-native-fight-$burst";frames=30;inputFrames=12;keys=@(323,324);capture=$true;audio=$true;expectAudio=$true} -ClientId $duelClients[0]
}
Start-Sleep -Seconds 2
AssertDuelViewer 0
AssertDuelViewer 1
AssertDuelMatch 2 3
if ((DuelStatus) -notmatch 'queued=0') { throw 'Next pair remained in the waiting queue after promotion' }
$handoff = Invoke-LabScreenshot -Name duel-next-pair-entered -ClientId $duelClients[0]
$duelChecks.Add('native death returns both fighters to viewers and automatically promotes the next agreed pair')
# A challenge survives the current match until the target can accept it.
DuelCommand 0 ".challenge $($duelIds[2])"
DuelCommand 3 '.leave'
AssertDuelViewer 2
DuelCommand 2 ".accept $($duelIds[0])"
AssertDuelMatch 0 2
DuelCommand 2 '.leave'
AssertDuelViewer 0
$duelChecks.Add('surviving fighter accepts the pending spectator challenge after match end')
foreach ($index in 1..3) { DuelCommand $index '.duel' }
DuelCommand 0 ".duel $($duelIds[1])"
DuelCommand 1 '.accept'
DuelCommand 2 ".duel $($duelIds[3])"
DuelCommand 3 '.accept'
DuelCommand 3 '.watch'
if ((DuelStatus) -notmatch 'queued=0') { throw 'Leaving the deck did not cancel the pair' }
AssertDuelViewer 2
$duelChecks.Add('queued player leaving cancels both commitments and releases the other viewer')
DuelCommand 3 '.duel'
DuelCommand 2 ".duel $($duelIds[3])"
DuelCommand 3 '.accept'
$null = Invoke-LabServer "/forcerole $($duelIds[3]) Tutorial"
Start-Sleep -Seconds 1
if ((DuelActor 3).role -ne 'Tutorial' -or (DuelStatus) -notmatch 'queued=0') { throw 'Tutorial transition retained a queued pair or changed the native role' }
AssertDuelViewer 2
$duelChecks.Add('queued Tutorial transition preserves the native role and releases the pair')
$null = Invoke-LabServer "/forcerole $($duelIds[3]) NtfPrivate"
DuelCommand 3 '.duel'
DuelCommand 2 ".duel $($duelIds[3])"
DuelCommand 3 '.accept'
DuelCommand 3 'disconnect'
Start-Sleep -Seconds 2
if ((DuelStatus) -notmatch 'queued=0') { throw 'Disconnected viewer retained a queued pair' }
AssertDuelViewer 2
DuelCommand 1 '.leave'
AssertDuelViewer 0
DuelCommand 0 ".duel $($duelIds[2])"
DuelCommand 2 '.accept'
AssertDuelMatch 0 2
$duelChecks.Add('queued disconnect cleans up; freed viewers can rematch')
DuelCommand 2 '.leave'
DuelCommand 2 '.watch'
AssertDuelViewer 2
DuelCommand 2 '.watch'
if ((DuelActor 2).position.y -gt $duelOrigin.y-50) { throw 'Idle viewing toggle did not return to Surface' }
DuelCommand 0 '.leave'
$duelChecks.Add('idle viewing, explicit forfeit and exit release ownership')
@{checks=@($duelChecks);handoff=$handoff;depletion=$empty;reload=$reload;fight=$fight;status=(DuelStatus)} |
    ConvertTo-Json -Depth 10 | Set-Content "$($Context.Evidence)/duel-walkthrough.json" -Encoding utf8
