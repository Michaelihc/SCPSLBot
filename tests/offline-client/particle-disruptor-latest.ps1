param($Context)

$actorId = $Context.Actor.id
$setupReply = Invoke-LabServer "/give $actorId 47"
$setupReply | Set-Content "$($Context.Evidence)\particle-disruptor-setup.txt" -Encoding utf8
if ($setupReply -notmatch 'Done!') {
    throw "Native give did not grant ParticleDisruptor: $setupReply"
}

Start-Sleep -Milliseconds 750
$beforePlayers = @(Observe | Where-Object { $_.id -eq $actorId })
if ($beforePlayers.Count -ne 1) {
    throw 'Connected client was not observable after granting ParticleDisruptor'
}

$before = $beforePlayers[0]
$before | ConvertTo-Json -Depth 9 |
    Set-Content "$($Context.Evidence)\particle-disruptor-before.json" -Encoding utf8
if (-not $before.ready -or $before.items -notcontains 'ParticleDisruptor') {
    throw 'ParticleDisruptor was not present on the ready client before native selection'
}

# The only firearm occupies the native primary-weapon hotkey. Record the key press
# and a six-second tail so a disconnect/crash or the held 3-X model is observable.
$receipt = Invoke-LabInput @{
    id = 'equip-particle-disruptor'
    frames = 360
    inputFrames = 2
    keys = @(49)
    capture = $true
    audio = $true
}

$afterPlayers = @(Observe | Where-Object { $_.id -eq $actorId })
$evidence = @{
    actorId = $actorId
    before = $before
    after = if ($afterPlayers.Count -eq 1) { $afterPlayers[0] } else { $null }
    input = $receipt
}
$evidence | ConvertTo-Json -Depth 9 |
    Set-Content "$($Context.Evidence)\particle-disruptor-result.json" -Encoding utf8

if ($afterPlayers.Count -ne 1) {
    throw 'Client disconnected or became unobservable while equipping ParticleDisruptor'
}

$after = $afterPlayers[0]
if (-not $after.ready -or $after.life -ne $before.life) {
    throw 'Client was no longer ready in the same life after equipping ParticleDisruptor'
}
if ($after.firearm.type -ne 'ParticleDisruptor') {
    throw "Native selection did not leave ParticleDisruptor equipped; current firearm: $($after.firearm.type)"
}
