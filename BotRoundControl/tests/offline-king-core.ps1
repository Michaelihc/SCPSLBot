param($Context,[switch]$ModelViews)
$ErrorActionPreference = 'Stop'
if ($Context.ClientIds.Count -ne 2) { throw 'This walkthrough needs two authenticated clients and keep_winner_in_arena: true' }
$coreClients = @($Context.ClientIds)
$coreActors = @($Context.Actors)
$coreIds = @($coreActors | ForEach-Object { $_.id })
$coreChecks = [Collections.Generic.List[string]]::new()
function CoreActor([int]$Index) { @(Observe -ClientsOnly | Where-Object id -eq $coreIds[$Index])[0] }
function CoreCommand([int]$Index,[string]$Command) { $null = Invoke-LabSetup $Command -ClientId $coreClients[$Index] }
function CoreStatus { (Invoke-LabServer '/duel_status') -join ' ' }
function AssertCoreMatch([int]$Left,[int]$Right) {
    if ((CoreStatus) -notmatch "state=fighting; challenger=$($coreIds[$Left]); opponent=$($coreIds[$Right]);") { throw "Wrong king match: $(CoreStatus)" }
    foreach ($index in @($Left,$Right)) {
        $actor = CoreActor $index
        if (@($actor.items) -notcontains 'GunE11SR' -or [Math]::Abs($actor.position.y-$coreOrigin.y) -gt 2 -or [Math]::Abs($actor.position.z-$coreOrigin.z) -gt 15) {
            throw "King match has no native loadout/placement: $($actor | ConvertTo-Json -Depth 7 -Compress)"
        }
    }
}
function AssertCoreResident([int]$Index) {
    if ((CoreStatus) -notmatch "state=idle;.*resident=$($coreIds[$Index]);.*challengers=0") { throw "Wrong resident king: $(CoreStatus)" }
    if ((CoreActor $Index).health -ne 100 -or @((CoreActor $Index).items) -notcontains 'GunE11SR') { throw 'Resident king has no fresh native health/loadout' }
}
$null = Invoke-LabServer '/forcestart'
Start-Sleep -Seconds 4
foreach ($actor in $coreActors) { $null = Invoke-LabServer "/forcerole $($actor.id) NtfPrivate" }
Start-Sleep -Seconds 2
foreach ($index in 0..1) { CoreCommand $index '.duel' }
$status = CoreStatus
if ($status -notmatch 'king=True;.*origin=\(([-\d.]+), ([-\d.]+), ([-\d.]+)\)') { throw "Missing king arena origin/config: $status" }
$coreOrigin = @{x=[double]$Matches[1];y=[double]$Matches[2];z=[double]$Matches[3]}
if ($status -notmatch 'viewing=\(([-\d.]+), ([-\d.]+), ([-\d.]+)\)') { throw "Missing viewing platform: $status" }
$coreViewing = @{x=[double]$Matches[1];y=[double]$Matches[2];z=[double]$Matches[3]}
if ($ModelViews) {
    $viewer = CoreActor 0
    if ([Math]::Abs($viewer.position.y-($coreOrigin.y+5.544851)) -gt 0.25 -or @($viewer.items).Count -ne 0) { throw 'Viewer is not standing unarmed on the supplied raised platform' }
    $life = $viewer.life
    CoreCommand 0 '.duel'
    if ((CoreActor 0).life -ne $life) { throw 'Repeated entry reset the viewer' }
    $null = Set-LabLook -Yaw 180 -Pitch 0 -ClientId $coreClients[0] -ActorId $coreIds[0]
    $null = Invoke-LabScreenshot -Name duel-model-board -ClientId $coreClients[0]
    $walk = Invoke-LabInput @{id='model-platform-walk';frames=60;inputFrames=12;keys=@(119);capture=$true;audio=$true} -ClientId $coreClients[0]
    $walked = CoreActor 0
    if ([Math]::Abs($walked.position.z-$viewer.position.z) -lt 0.25 -or [Math]::Abs($walked.position.y-$viewer.position.y) -gt 0.25) { throw 'Native walking did not stay on the supplied platform' }
    $null = Invoke-LabInput @{id='model-platform-return';frames=60;inputFrames=12;keys=@(115);capture=$true;audio=$true} -ClientId $coreClients[0]
    foreach ($view in @(@{name='duel-model-1v1';x=-9.900002;y=1.52485085;z=0.67},@{name='duel-model-motto';x=9.849998;y=1.52485085;z=0.67},@{name='duel-model-overview';x=0;y=0;z=0})) {
        $null = Set-LabAim -Target @{x=$coreOrigin.x+$view.x;y=$coreOrigin.y+$view.y;z=$coreOrigin.z+$view.z} -ClientId $coreClients[0] -ActorId $coreIds[0]
        $null = Invoke-LabScreenshot -Name $view.name -ClientId $coreClients[0]
    }
    foreach ($button in @(@{id='model-select-all';x=-1.15},@{id='model-invite';x=1.15})) {
        $null = Set-LabAim -Target @{x=$coreViewing.x+$button.x;y=$coreViewing.y-0.3;z=$coreViewing.z-1.5} -ClientId $coreClients[0] -ActorId $coreIds[0]
        $null = Invoke-LabInput @{id=$button.id;frames=60;inputFrames=2;keys=@(101);capture=$true;audio=$true} -ClientId $coreClients[0]
    }
    if ((CoreStatus) -notmatch 'invited=1') { throw 'Native E board did not send an invitation from the raised platform' }
    $coreChecks.Add('supplied platform supports native walking and repeated entry; repositioned native E controls send invitations')
}
CoreCommand 0 '.challenge'
if ((CoreStatus) -notmatch "state=idle;.*challenge_queue=$($coreIds[0]);") { throw 'First individual did not wait for an opening opponent' }
CoreCommand 0 '.challenge'
if ((CoreStatus) -notmatch 'challengers=1') { throw 'Duplicate individual queue entry' }
CoreCommand 0 '.watch'
if ((CoreStatus) -notmatch 'challengers=0') { throw 'Leaving did not cancel the individual entry' }
CoreCommand 0 '.duel'
CoreCommand 0 '.challenge'
CoreCommand 1 '.challenge'
AssertCoreMatch 0 1
$coreChecks.Add('individual entry waits for an opponent; duplicate rejection and cancellation; first two entrants open arena')
CoreCommand 1 '.leave'
AssertCoreResident 0
$retained = Invoke-LabScreenshot -Name duel-core-king-retained -ClientId $coreClients[0]
CoreCommand 1 '.duel'
CoreCommand 1 ".challenge $($coreIds[0])"
AssertCoreMatch 0 1
$coreChecks.Add('forfeit retains king alone; later individual automatically challenges king')
$null = Invoke-LabInventory -Slot 1 -Expect GunE11SR -ClientId $coreClients[1] -ActorId $coreIds[1]
$null = Set-LabAim -Target @{x=$coreOrigin.x+9.95;y=$coreOrigin.y+1;z=$coreOrigin.z+8} -ClientId $coreClients[1] -ActorId $coreIds[1]
$empty = Invoke-LabInput @{id='core-king-depletion';frames=420;keys=@(323);capture=$true;audio=$true;expectAudio=$true} -ClientId $coreClients[1]
if ((CoreActor 1).firearm.ammo -ne 0) { throw 'King challenger did not deplete the native magazine' }
$reload = Invoke-LabInput @{id='core-king-reload';frames=240;inputFrames=2;keys=@(114);capture=$true;audio=$true} -ClientId $coreClients[1]
if ((CoreActor 1).firearm.ammo -le 0) { throw 'King challenger did not reload natively' }
$fight = @()
for ($burst = 0; $burst -lt 12; $burst++) {
    if ((CoreStatus) -notmatch "challenger=$($coreIds[0]); opponent=$($coreIds[1]);") { break }
    $target = CoreActor 0
    $null = Set-LabAim -Target @{x=$target.position.x;y=$target.position.y+0.2;z=$target.position.z} -ClientId $coreClients[1] -ActorId $coreIds[1]
    $fight += Invoke-LabInput @{id="core-king-native-fight-$burst";frames=30;inputFrames=12;keys=@(323,324);capture=$true;audio=$true;expectAudio=$true} -ClientId $coreClients[1]
}
Start-Sleep -Seconds 2
AssertCoreResident 1
$defeated = CoreActor 0
if (@($defeated.items).Count -ne 0 -or $defeated.role -ne 'NtfPrivate' -or [Math]::Abs($defeated.position.z-$coreViewing.z) -gt 3 -or [Math]::Abs($defeated.position.y-$coreViewing.y) -gt 0.25) { throw 'Defeated king did not respawn unarmed on viewing deck' }
$transfer = Invoke-LabScreenshot -Name duel-core-king-transferred -ClientId $coreClients[0]
$coreChecks.Add('native depletion/reload and lethal fight transfer championship; defeated king respawns unarmed; new king gets fresh health')
CoreCommand 1 ".duel $($coreIds[0])"
CoreCommand 0 ".accept $($coreIds[1])"
AssertCoreMatch 1 0
$coreChecks.Add('resident king invitation accepts inviter ID and enrolls challenger without duplicating king')
$null = Invoke-LabServer "/forcerole $($coreIds[1]) Tutorial"
Start-Sleep -Seconds 1
if ((CoreActor 1).role -ne 'Tutorial') { throw 'External Tutorial transition changed the native role' }
AssertCoreResident 0
$coreChecks.Add('external Tutorial transition releases king ownership and preserves successor')
$null = Invoke-LabServer "/forcerole $($coreIds[1]) NtfPrivate"
CoreCommand 1 '.duel'
CoreCommand 1 '.challenge'
AssertCoreMatch 0 1
CoreCommand 0 'disconnect'
Start-Sleep -Seconds 2
AssertCoreResident 1
CoreCommand 1 '.leave'
if ((CoreStatus) -notmatch 'state=idle;.*viewers=0;.*resident=0;.*challengers=0') { throw 'Final king exit retained ownership' }
$coreChecks.Add('king disconnect preserves surviving challenger; final exit releases arena ownership')
@{checks=@($coreChecks);retained=$retained;transfer=$transfer;depletion=$empty;reload=$reload;fight=$fight;status=(CoreStatus)} |
    ConvertTo-Json -Depth 10 | Set-Content "$($Context.Evidence)/duel-king-core.json" -Encoding utf8
