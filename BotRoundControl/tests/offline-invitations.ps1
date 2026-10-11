param($Context)
$ErrorActionPreference = 'Stop'
if ($Context.ClientIds.Count -ne 4) { throw 'This walkthrough needs four authenticated clients' }
$duelClients = @($Context.ClientIds)
$duelActors = @($Context.Actors)
$duelIds = @($duelActors | ForEach-Object { $_.id })
$duelChecks = [Collections.Generic.List[string]]::new()
function DuelActor([int]$Index) { @(Observe -ClientsOnly | Where-Object id -eq $duelIds[$Index])[0] }
function DuelStatus { (Invoke-LabServer '/duel_status') -join ' ' }
function DuelPoint([double]$X,[double]$Y,[double]$Z) { @{x=$duelOrigin.x+$X;y=$duelOrigin.y+$Y;z=$duelOrigin.z+$Z} }
function DuelCommand([int]$Index,[string]$Command) { $null = Invoke-LabSetup $Command -ClientId $duelClients[$Index] }
function AssertDuelViewer([int]$Index) {
    $actor = DuelActor $Index
    if ($actor.role -ne 'NtfPrivate' -or [Math]::Abs($actor.position.z-($duelOrigin.z+18)) -gt 3 -or [Math]::Abs($actor.position.y-($duelOrigin.y+3.5)) -gt 2 -or @($actor.items).Count -ne 0) {
        throw "Player $($duelIds[$Index]) is not an unarmed viewer: $($actor | ConvertTo-Json -Depth 8 -Compress)"
    }
}
function AssertDuelMatch([int]$Left,[int]$Right) {
    $status = DuelStatus
    if ($status -notmatch "state=fighting; challenger=$($duelIds[$Left]); opponent=$($duelIds[$Right]);") { throw "Wrong active pair: $status" }
    foreach ($index in @($Left,$Right)) {
        $actor = DuelActor $index
        if (@($actor.items) -notcontains 'GunE11SR' -or [Math]::Abs($actor.position.y-$duelOrigin.y) -gt 2 -or [Math]::Abs($actor.position.z-$duelOrigin.z) -gt 15) {
            throw "Pair acceptance did not create native duel loadout/placement: $($actor | ConvertTo-Json -Depth 8 -Compress)"
        }
    }
    if ((DuelActor $Right).role -ne 'ChaosConscript') { throw 'Opponent did not enter the native Chaos role' }
}
function DuelBoard([double]$X,[double]$Y,[string]$Name,[int]$Index=0) {
    $null = Set-LabAim -Target (DuelPoint $X $Y 15.5) -ClientId $duelClients[$Index] -ActorId $duelIds[$Index]
    Invoke-LabInput @{id=$Name;frames=60;inputFrames=2;keys=@(101);capture=$true;audio=$true} -ClientId $duelClients[$Index]
}
$null = Invoke-LabServer '/forcestart'
Start-Sleep -Seconds 4
foreach ($actor in $duelActors) { $null = Invoke-LabServer "/forcerole $($actor.id) NtfPrivate" }
Start-Sleep -Seconds 2
$status = DuelStatus
if ($status -notmatch 'origin=\(([-\d.]+), ([-\d.]+), ([-\d.]+)\)') { throw "Missing arena origin: $status" }
$duelOrigin = @{x=[double]$Matches[1];y=[double]$Matches[2];z=[double]$Matches[3]}
$settings = Get-LabSettings -ClientId $duelClients[0]
if (-not ($settings.settings | Where-Object { $_.type -eq 'SSDropdownSetting' -and ($_.options -join ' ') -match '1v1' })) {
    throw 'Authenticated client did not receive the duel zone option'
}
foreach ($index in 0..3) { DuelCommand $index '.duel' }
Start-Sleep -Seconds 1
foreach ($index in 0..3) { AssertDuelViewer $index }
$beforeRepeat = DuelActor 0
DuelCommand 0 '.duel'
if ((DuelActor 0).life -ne $beforeRepeat.life) { throw 'Repeated entry reset the viewer' }
$duelChecks.Add('all authenticated players enter viewing; repeated entry preserves life')
$null = DuelBoard -1.15 3.2 'duel-select-everyone'
$board = Invoke-LabScreenshot -Name duel-viewer-selection -ClientId $duelClients[0]
$null = DuelBoard 1.15 3.2 'duel-invite-everyone'
if ((DuelStatus) -notmatch 'invited=3') { throw 'Native E board did not invite the three other eligible players' }
$invitation = Invoke-LabScreenshot -Name duel-invitation -ClientId $duelClients[1]
DuelCommand 1 ".accept $($duelIds[2])"
if ((DuelStatus) -notmatch 'state=idle') { throw 'Wrong sender ID claimed the arena' }
Start-Sleep -Seconds 20
DuelCommand 1 ".accept $($duelIds[0])"
Start-Sleep -Seconds 1
AssertDuelMatch 0 1
DuelCommand 2 '.yes'
AssertDuelViewer 2
AssertDuelViewer 3
$duelChecks.Add('native board invitation accepts inviter ID within 30 seconds; first acceptance wins')
DuelCommand 1 '.leave'
Start-Sleep -Seconds 1
AssertDuelViewer 0
DuelCommand 1 '.duel'
DuelCommand 0 ".duel $($duelIds[1])"
DuelCommand 1 '.yes'
AssertDuelMatch 0 1
DuelCommand 2 '.challenge 999999'
if ((DuelStatus) -notmatch 'requests=0') { throw 'Invalid arena target created a request' }
foreach ($iteration in 1..2) { DuelCommand 2 ".challenge $($duelIds[0])" }
if ((DuelStatus) -notmatch 'requests=1') { throw 'Repeated challenges were missing or duplicated' }
DuelCommand 0 ".accept $($duelIds[2])"
AssertDuelMatch 0 1
$duelChecks.Add('spectator challenges replace duplicates and cannot interrupt active combat')
DuelCommand 1 '.leave'
AssertDuelViewer 0
DuelCommand 3 ".duel $($duelIds[0])"
DuelCommand 0 '.accept'
AssertDuelMatch 3 0
if ((DuelStatus) -notmatch 'requests=0') { throw 'Accepting an ordinary invitation retained the competing viewer request' }
$duelChecks.Add('no-ID acceptance prefers an ordinary offer over an older incoming viewer challenge')
DuelCommand 0 '.leave'
DuelCommand 0 '.duel'
DuelCommand 1 '.duel'
DuelCommand 0 ".duel $($duelIds[1])"
Start-Sleep -Seconds 31
$beforeExpiry = DuelActor 1
DuelCommand 1 ".accept $($duelIds[0])"
if ((DuelStatus) -notmatch 'state=idle' -or (DuelActor 1).life -ne $beforeExpiry.life) { throw 'Expired invitation started a duel' }
$duelChecks.Add('expired invitation cannot claim a match')
DuelCommand 0 ".duel $($duelIds[1])"
DuelCommand 1 '.accept'
AssertDuelMatch 0 1
DuelCommand 2 ".duel $($duelIds[3])"
DuelCommand 3 ".duel $($duelIds[2])"
if ((DuelStatus) -notmatch 'invited=2') { throw 'Unpaired viewers could not maintain independent offers during combat' }
DuelCommand 3 ".accept $($duelIds[2])"
if ((DuelStatus) -notmatch "queued=1;.*queue=$($duelIds[2])\+$($duelIds[3]);") { throw 'Accepted pair did not queue behind the active pair' }
AssertDuelMatch 0 1
AssertDuelViewer 2
AssertDuelViewer 3
$queuedBefore = DuelActor 3
DuelCommand 3 '.accept'
DuelCommand 3 ".duel $($duelIds[0])"
if ((DuelActor 3).life -ne $queuedBefore.life -or (DuelStatus) -notmatch 'queued=1') { throw 'Repeated actions changed an already queued pair' }
$queued = Invoke-LabScreenshot -Name duel-pair-queue -ClientId $duelClients[2]
$duelChecks.Add('independent invitations continue during combat; accepted pair waits intact and repeated actions preserve it')
@{checks=@($duelChecks);origin=$duelOrigin;board=$board;invitation=$invitation;queue=$queued;status=(DuelStatus)} |
    ConvertTo-Json -Depth 10 | Set-Content "$($Context.Evidence)/duel-invitations.json" -Encoding utf8
