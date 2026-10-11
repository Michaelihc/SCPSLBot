param($Context)
$ErrorActionPreference = 'Stop'
if ($Context.ClientIds.Count -ne 3) { throw 'This walkthrough needs three authenticated clients' }
$duelClients = @($Context.ClientIds)
$duelActors = @($Context.Actors)
$duelOwner = $duelActors[0].id
$duelRecipient = $duelActors[1].id
$duelOther = $duelActors[2].id
$duelChecks = [Collections.Generic.List[string]]::new()
function DuelActor([int]$Id) { @(Observe -ClientsOnly | Where-Object id -eq $Id)[0] }
function DuelStatus { (Invoke-LabServer '/duel_status') -join ' ' }
function DuelPoint([double]$X, [double]$Y, [double]$Z) { @{x=$duelOrigin.x+$X;y=$duelOrigin.y+$Y;z=$duelOrigin.z+$Z} }
function DuelPress([double]$X, [string]$Name) {
    $null = Set-LabAim -Target (DuelPoint $X 0.7 -10.5) -ClientId $duelClients[0] -ActorId $duelOwner
    if ($Name -match '^invite-') { $script:duelSentAt = [DateTime]::UtcNow }
    $null = Invoke-LabInput @{id=$Name;frames=60;inputFrames=2;keys=@(323);capture=$true;audio=$true} -ClientId $duelClients[0]
}
function SendDuelInvitations([string]$Case) {
    $null = Invoke-LabSetup '.duel' -ClientId $duelClients[0]
    Start-Sleep -Seconds 1
    $gunActor = DuelActor $duelOwner
    if ($gunActor.held -ne 'GunE11SR') { $null = Invoke-LabInventory -Slot 1 -Expect GunE11SR -ClientId $duelClients[0] -ActorId $duelOwner }
    DuelPress -1.15 "select-$Case"
    DuelPress 1.15 "invite-$Case"
    if ((DuelStatus) -notmatch "state=lobby; challenger=$duelOwner; opponent=0; invited=2") { throw "Invitations were not sent for $Case" }
}
function AssertDuelMatch([int]$Opponent) {
    $owner = DuelActor $duelOwner
    $target = DuelActor $Opponent
    if ($owner.role -ne 'NtfPrivate' -or $target.role -ne 'ChaosConscript' -or
        [Math]::Abs($owner.position.y-$duelOrigin.y) -gt 3 -or [Math]::Abs($target.position.y-$duelOrigin.y) -gt 3 -or
        [Math]::Abs($target.position.z-$duelOrigin.z) -gt 10 -or @($target.items | Where-Object type -eq 'GunE11SR').Count -ne 1) {
        throw "Acceptance did not create the native duel: $($target | ConvertTo-Json -Depth 7 -Compress)"
    }
    if ((DuelStatus) -notmatch "state=fighting; challenger=$duelOwner; opponent=$Opponent; invited=0") { throw 'Accepted match did not claim and clear the invitation slot' }
}
$null = Invoke-LabServer '/forcestart'
Start-Sleep -Seconds 4
foreach ($actor in $duelActors) { $null = Invoke-LabServer "/forcerole $($actor.id) NtfPrivate" }
Start-Sleep -Seconds 2
$surface = DuelActor $duelOwner
$null = Invoke-LabSetup '.duel' -ClientId $duelClients[0]
$status = DuelStatus
if ($status -notmatch 'origin=\(([-\d.]+), ([-\d.]+), ([-\d.]+)\)') { throw "Missing arena position: $status" }
$duelOrigin = @{x=[double]$Matches[1];y=[double]$Matches[2];z=[double]$Matches[3]}
if ($duelOrigin.y -lt $surface.position.y+100) { throw 'Arena was not above Surface' }
SendDuelInvitations 'no-id'
Start-Sleep -Seconds 20
$elapsed = ([DateTime]::UtcNow-$duelSentAt).TotalSeconds
if ($elapsed -ge 28) { throw "Setup consumed the invitation window ($elapsed seconds)" }
$null = Invoke-LabSetup '.yes' -ClientId $duelClients[1]
Start-Sleep -Seconds 1
AssertDuelMatch $duelRecipient
$duelChecks.Add('no-ID acceptance after more than 20 seconds of a 30-second invitation')
$late = DuelActor $duelOther
$null = Invoke-LabSetup '.accept' -ClientId $duelClients[2]
$lateAfter = DuelActor $duelOther
if ($lateAfter.life -ne $late.life -or $lateAfter.position.y -gt $duelOrigin.y-50) { throw 'Late acceptance moved the third player into the match' }
AssertDuelMatch $duelRecipient
$duelChecks.Add('first acceptance excludes later recipients')
$null = Invoke-LabSetup '.leave' -ClientId $duelClients[1]
Start-Sleep -Seconds 1
$null = Invoke-LabSetup '.spectate' -ClientId $duelClients[1]
$null = Invoke-LabSetup '.spectate' -ClientId $duelClients[2]
SendDuelInvitations 'sender-id'
$viewerBefore = DuelActor $duelRecipient
$null = Invoke-LabSetup ".accept $duelOther" -ClientId $duelClients[1]
if ((DuelStatus) -notmatch 'state=lobby.*invited=2' -or (DuelActor $duelRecipient).life -ne $viewerBefore.life) { throw 'Incorrect sender ID consumed an invitation' }
$elapsed = ([DateTime]::UtcNow-$duelSentAt).TotalSeconds
if ($elapsed -ge 28) { throw "Sender-ID setup consumed the invitation window ($elapsed seconds)" }
$null = Invoke-LabSetup ".accept $duelOwner" -ClientId $duelClients[1]
Start-Sleep -Seconds 1
AssertDuelMatch $duelRecipient
$duelChecks.Add('inviter ID accepts an ordinary invitation from the viewing deck; wrong ID leaves it usable')
$accepted = Invoke-LabScreenshot -Name duel-sender-id-accepted -ClientId $duelClients[1]
$null = Invoke-LabSetup ".challenge $duelOwner" -ClientId $duelClients[2]
$null = Invoke-LabSetup ".accept $duelOther" -ClientId $duelClients[0]
AssertDuelMatch $duelRecipient
$null = Invoke-LabSetup '.leave' -ClientId $duelClients[1]
$null = Invoke-LabSetup ".accept $duelOther" -ClientId $duelClients[0]
Start-Sleep -Seconds 1
AssertDuelMatch $duelOther
$duelChecks.Add('spectator request waits for the current match and accepts by sender ID afterwards')
$null = Invoke-LabSetup '.leave' -ClientId $duelClients[2]
SendDuelInvitations 'expiry'
Start-Sleep -Seconds 31
$beforeExpiredAccept = DuelActor $duelRecipient
$null = Invoke-LabSetup ".accept $duelOwner" -ClientId $duelClients[1]
if ((DuelStatus) -notmatch 'state=idle' -or (DuelActor $duelRecipient).life -ne $beforeExpiredAccept.life) { throw 'An expired invitation started a match' }
$duelChecks.Add('expired invitation cannot claim the arena')
@{checks=@($duelChecks);origin=$duelOrigin;accepted=$accepted;status=(DuelStatus)} |
    ConvertTo-Json -Depth 8 | Set-Content "$($Context.Evidence)/duel-invitations.json" -Encoding utf8
