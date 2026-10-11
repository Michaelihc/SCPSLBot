param($Context)
$ErrorActionPreference = 'Stop'
if ($Context.ClientIds.Count -ne 3) { throw 'This walkthrough needs three authenticated clients and keep_winner_in_arena: true' }
$hillClients = @($Context.ClientIds)
$hillActors = @($Context.Actors)
$hillIds = @($hillActors | ForEach-Object { $_.id })
$hillChecks = [Collections.Generic.List[string]]::new()
function HillActor([int]$Index) { @(Observe -ClientsOnly | Where-Object id -eq $hillIds[$Index])[0] }
function HillCommand([int]$Index,[string]$Command) { $null = Invoke-LabSetup $Command -ClientId $hillClients[$Index] }
function HillStatus { (Invoke-LabServer '/duel_status') -join ' ' }
function AssertHillMatch([int]$Left,[int]$Right) {
    if ((HillStatus) -notmatch "state=fighting; challenger=$($hillIds[$Left]); opponent=$($hillIds[$Right]);") { throw "Wrong king match: $(HillStatus)" }
    foreach ($index in @($Left,$Right)) {
        $actor = HillActor $index
        if (@($actor.items) -notcontains 'GunE11SR' -or [Math]::Abs($actor.position.y-$hillOrigin.y) -gt 2 -or [Math]::Abs($actor.position.z-$hillOrigin.z) -gt 15) {
            throw "King match has no native loadout/placement: $($actor | ConvertTo-Json -Depth 7 -Compress)"
        }
    }
}
function AssertHillViewer([int]$Index) {
    $actor = HillActor $Index
    if (@($actor.items).Count -ne 0 -or $actor.role -ne 'NtfPrivate' -or [Math]::Abs($actor.position.z-($hillOrigin.z+18)) -gt 3) {
        throw "Defeated king did not return unarmed to viewing: $($actor | ConvertTo-Json -Depth 7 -Compress)"
    }
}
$null = Invoke-LabServer '/forcestart'
Start-Sleep -Seconds 4
foreach ($actor in $hillActors) { $null = Invoke-LabServer "/forcerole $($actor.id) NtfPrivate" }
Start-Sleep -Seconds 2
foreach ($index in 0..2) { HillCommand $index '.duel' }
$status = HillStatus
if ($status -notmatch 'origin=\(([-\d.]+), ([-\d.]+), ([-\d.]+)\)') { throw "Missing arena origin: $status" }
$hillOrigin = @{x=[double]$Matches[1];y=[double]$Matches[2];z=[double]$Matches[3]}
# An empty arena opens with the first two consenting individual entrants.
HillCommand 0 '.challenge'
if ((HillStatus) -notmatch "state=idle;.*challenge_queue=$($hillIds[0]);") { throw 'First individual did not wait for an opening opponent' }
HillCommand 1 '.challenge'
AssertHillMatch 0 1
HillCommand 2 ".challenge $($hillIds[0])"
HillCommand 2 '.challenge'
if ((HillStatus) -notmatch "challengers=1; challenge_queue=$($hillIds[2]);") { throw 'Individual queue entry or duplicate protection failed' }
AssertHillViewer 2
$queued = Invoke-LabScreenshot -Name duel-king-individual-queue -ClientId $hillClients[2]
$hillChecks.Add('first two individuals open arena in FIFO order; third entrant queues once')
HillCommand 1 '.leave'
AssertHillMatch 0 2
if ((HillStatus) -notmatch 'challengers=0') { throw 'Winner did not consume the first challenger entry' }
HillCommand 1 '.duel'
HillCommand 1 '.challenge'
if ((HillStatus) -notmatch "challenge_queue=$($hillIds[1]);") { throw 'Later challenger did not queue for the following match' }
$hillChecks.Add('winner stays and automatically faces next individual')
$null = Invoke-LabInventory -Slot 1 -Expect GunE11SR -ClientId $hillClients[2] -ActorId $hillIds[2]
$null = Set-LabAim -Target @{x=$hillOrigin.x+9.95;y=$hillOrigin.y+1;z=$hillOrigin.z+8} -ClientId $hillClients[2] -ActorId $hillIds[2]
$empty = Invoke-LabInput @{id='king-depletion';frames=420;keys=@(323);capture=$true;audio=$true;expectAudio=$true} -ClientId $hillClients[2]
if ((HillActor 2).firearm.ammo -ne 0) { throw 'King challenger did not deplete the native magazine' }
$reload = Invoke-LabInput @{id='king-reload';frames=240;inputFrames=2;keys=@(114);capture=$true;audio=$true} -ClientId $hillClients[2]
if ((HillActor 2).firearm.ammo -le 0) { throw 'King challenger did not reload natively' }
$fight = @()
for ($burst = 0; $burst -lt 12; $burst++) {
    if ((HillStatus) -notmatch "challenger=$($hillIds[0]); opponent=$($hillIds[2]);") { break }
    $target = HillActor 0
    $null = Set-LabAim -Target @{x=$target.position.x;y=$target.position.y+0.2;z=$target.position.z} -ClientId $hillClients[2] -ActorId $hillIds[2]
    $fight += Invoke-LabInput @{id="king-native-fight-$burst";frames=30;inputFrames=12;keys=@(323,324);capture=$true;audio=$true;expectAudio=$true} -ClientId $hillClients[2]
}
Start-Sleep -Seconds 2
AssertHillViewer 0
AssertHillMatch 2 1
if ((HillActor 2).health -ne 100) { throw 'New king did not receive fresh match health' }
$transfer = Invoke-LabScreenshot -Name duel-king-transferred -ClientId $hillClients[0]
$hillChecks.Add('native depletion/reload and lethal fight transfer championship; defeated king respawns on deck')
# Invitations enroll both players individually rather than reserving a pair.
HillCommand 1 '.leave'
HillCommand 1 '.duel'
HillCommand 0 ".duel $($hillIds[1])"
HillCommand 1 ".accept $($hillIds[0])"
AssertHillMatch 2 0
if ((HillStatus) -notmatch "challengers=1; challenge_queue=$($hillIds[1]);") { throw 'Accepted invitation did not enroll both individuals, inviter first, against the king' }
$hillChecks.Add('accepted invitations become individual entries; king remains against the next player')
HillCommand 1 '.watch'
if ((HillStatus) -notmatch 'challengers=0') { throw 'Leaving did not cancel the individual queue entry' }
AssertHillMatch 2 0
HillCommand 0 '.leave'
if ((HillStatus) -notmatch "state=idle;.*resident=$($hillIds[2]);") { throw 'Winner did not stay alone when queue emptied' }
$hillChecks.Add('individual cancellation preserves active match; winner remains when queue empties')
HillCommand 1 '.duel'
HillCommand 1 '.challenge'
AssertHillMatch 2 1
HillCommand 2 'disconnect'
Start-Sleep -Seconds 2
if ((HillStatus) -notmatch "state=idle;.*resident=$($hillIds[1]);") { throw 'Disconnecting king retained ownership or lost surviving challenger' }
HillCommand 0 '.duel'
HillCommand 0 '.challenge'
AssertHillMatch 1 0
$null = Invoke-LabServer "/forcerole $($hillIds[1]) Tutorial"
Start-Sleep -Seconds 1
if ((HillActor 1).role -ne 'Tutorial' -or (HillStatus) -notmatch "state=idle;.*resident=$($hillIds[0]);") { throw 'Tutorial transition retained king ownership or changed native role' }
HillCommand 0 '.leave'
$hillChecks.Add('king disconnect and external Tutorial transition release ownership and preserve the successor')
@{checks=@($hillChecks);queue=$queued;transfer=$transfer;depletion=$empty;reload=$reload;fight=$fight;status=(HillStatus)} |
    ConvertTo-Json -Depth 10 | Set-Content "$($Context.Evidence)/duel-winner-stays.json" -Encoding utf8
