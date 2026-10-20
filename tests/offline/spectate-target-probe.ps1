param($Context)
$ErrorActionPreference = 'Stop'
# Probe which native spectator path follows the SCPSLBot dummy: plain Spectator vs Overwatch,
# with native Shoot taps/holds. Each attempt leaves a screenshot for review.
$client = @(Observe -ClientsOnly)[0]
function Bot { @(Observe | Where-Object { $_.id -ne $client.id })[0] }
$deadline = (Get-Date).AddSeconds(60)
while (-not (Bot) -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 1 }
$bot = Bot
$null = Invoke-LabServer '/forcestart'
Start-Sleep -Seconds 5
$null = Invoke-LabServer "/forcerole $($bot.id) ClassD"
$null = Invoke-LabServer '/wave forcepause NTF true'
$null = Invoke-LabServer '/wave forcepause Chaos true'

# A native death runs the spectator's automatic target selection (Overwatch and forcerole
# Spectator do not); the paused waves keep the dead client spectating.
$null = Invoke-LabServer "/forcerole $($client.id) Scientist"
Start-Sleep -Seconds 3
"kill => $(Invoke-LabServer "/kill $($client.id)")" | Set-Content "$($Context.Evidence)\probe.log"
Start-Sleep -Seconds 4
$null = Invoke-LabScreenshot -Name death-auto-4s
Start-Sleep -Seconds 8
$null = Invoke-LabScreenshot -Name death-auto-12s
$null = Invoke-LabInput @{id='death-tap';frames=45;inputFrames=2;keys=@(323)}
$null = Invoke-LabScreenshot -Name death-tap
