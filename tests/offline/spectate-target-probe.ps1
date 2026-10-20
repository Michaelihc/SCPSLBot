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

$null = Invoke-LabServer "/forcerole $($client.id) Spectator"
Start-Sleep -Seconds 3
$null = Invoke-LabScreenshot -Name spectator-auto
$null = Invoke-LabInput @{id='spectator-tap';frames=45;inputFrames=2;keys=@(323)}
$null = Invoke-LabScreenshot -Name spectator-tap
$null = Invoke-LabInput @{id='spectator-hold';frames=45;inputFrames=12;keys=@(323)}
$null = Invoke-LabScreenshot -Name spectator-hold

$null = Invoke-LabServer "/forcerole $($client.id) Overwatch"
Start-Sleep -Seconds 3
$null = Invoke-LabInput @{id='overwatch-hold';frames=45;inputFrames=12;keys=@(323)}
$null = Invoke-LabScreenshot -Name overwatch-hold
$null = Invoke-LabInput @{id='overwatch-zoom';frames=45;inputFrames=12;keys=@(324)}
$null = Invoke-LabScreenshot -Name overwatch-zoom
