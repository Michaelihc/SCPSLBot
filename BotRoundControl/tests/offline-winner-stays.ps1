param($Context)
$ErrorActionPreference = 'Stop'
if ($Context.ClientIds.Count -ne 3) { throw 'This walkthrough needs three authenticated clients and keep_winner_in_arena: true' }
$winnerClients = @($Context.ClientIds)
$winnerActors = @($Context.Actors)
$winnerIds = @($winnerActors | ForEach-Object { $_.id })
function WinnerActor([int]$Index) { @(Observe -ClientsOnly | Where-Object id -eq $winnerIds[$Index])[0] }
function WinnerCommand([int]$Index,[string]$Command) { $null = Invoke-LabSetup $Command -ClientId $winnerClients[$Index] }
function WinnerStatus { (Invoke-LabServer '/duel_status') -join ' ' }
$null = Invoke-LabServer '/forcestart'
Start-Sleep -Seconds 4
foreach ($actor in $winnerActors) { $null = Invoke-LabServer "/forcerole $($actor.id) NtfPrivate" }
Start-Sleep -Seconds 2
foreach ($index in 0..2) { WinnerCommand $index '.duel' }
$status = WinnerStatus
if ($status -notmatch 'origin=\(([-\d.]+), ([-\d.]+), ([-\d.]+)\)') { throw "Missing arena origin: $status" }
$winnerOriginZ = [double]$Matches[3]
WinnerCommand 0 ".duel $($winnerIds[1])"
WinnerCommand 1 '.accept'
WinnerCommand 2 ".challenge $($winnerIds[0])"
WinnerCommand 1 '.leave'
Start-Sleep -Seconds 1
$retained = WinnerActor 0
if ((WinnerStatus) -notmatch "state=idle;.*resident=$($winnerIds[0]);" -or @($retained.items) -notcontains 'GunE11SR' -or [Math]::Abs($retained.position.z-($winnerOriginZ-8)) -gt 2) {
    throw 'Enabled winner-stays did not retain the winner on the arena'
}
$stay = Invoke-LabScreenshot -Name duel-winner-retained -ClientId $winnerClients[0]
WinnerCommand 0 ".accept $($winnerIds[2])"
if ((WinnerStatus) -notmatch "state=fighting; challenger=$($winnerIds[2]); opponent=$($winnerIds[0]);") { throw 'Retained winner could not accept a spectator challenge' }
WinnerCommand 2 '.leave'
WinnerCommand 1 ".duel $($winnerIds[2])"
WinnerCommand 2 '.accept'
Start-Sleep -Seconds 1
$yielded = WinnerActor 0
if ((WinnerStatus) -notmatch "state=fighting; challenger=$($winnerIds[1]); opponent=$($winnerIds[2]);" -or @($yielded.items).Count -ne 0 -or [Math]::Abs($yielded.position.z-($winnerOriginZ+18)) -gt 3) {
    throw 'Retained winner did not yield to the next intact pair'
}
$yield = Invoke-LabScreenshot -Name duel-winner-yields -ClientId $winnerClients[0]
WinnerCommand 2 '.leave'
WinnerCommand 1 '.leave'
WinnerCommand 0 '.leave'
@{checks=@('winner retained when arena has no next pair','retained winner accepts spectator challenge','winner yields to next intact agreed pair');retained=$retained;yielded=$yielded;stay=$stay;yield=$yield;status=(WinnerStatus)} |
    ConvertTo-Json -Depth 10 | Set-Content "$($Context.Evidence)/duel-winner-stays.json" -Encoding utf8
