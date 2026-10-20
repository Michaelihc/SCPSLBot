param($Context)
$ErrorActionPreference = 'Stop'
# Probe native spectator selection with a placed pointer: Shoot at screen centre, then a click on
# the bot's spectator-list entry located through Get-LabUi. Screenshots after each attempt.
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
$null = Invoke-LabServer "/forcerole $($client.id) Overwatch"
Start-Sleep -Seconds 10

$ui = Get-LabUi
$ui | ConvertTo-Json -Depth 8 | Set-Content "$($Context.Evidence)\ui.json" -Encoding utf8
$null = Invoke-LabScreenshot -Name overwatch-before

$centre = Invoke-LabInput @{id='shoot-centre';frames=45;inputFrames=2;keys=@(323);cursorX=0.5;cursorY=0.5}
$centre | ConvertTo-Json -Depth 8 | Set-Content "$($Context.Evidence)\shoot-centre.json" -Encoding utf8
$null = Invoke-LabScreenshot -Name shoot-centre

# Spectator list entry: the control whose text names the bot.
$entry = @($ui | Where-Object { "$($_.text) $($_.name)" -match 'Round Bot|Spectat' })[0]
if ($entry) {
    $x = ($entry.rect.x0 + $entry.rect.x1) / 2
    $y = ($entry.rect.y0 + $entry.rect.y1) / 2
    $click = Invoke-LabInput @{id='click-list-entry';frames=45;inputFrames=4;keys=@(323);cursorX=$x;cursorY=$y}
    $click | ConvertTo-Json -Depth 8 | Set-Content "$($Context.Evidence)\click-list-entry.json" -Encoding utf8
    $null = Invoke-LabScreenshot -Name click-list-entry
}
else {
    # Fall back to the entry's on-screen position from earlier screenshots (left edge, mid height).
    $click = Invoke-LabInput @{id='click-list-guess';frames=45;inputFrames=4;keys=@(323);cursorX=0.07;cursorY=0.5}
    $click | ConvertTo-Json -Depth 8 | Set-Content "$($Context.Evidence)\click-list-guess.json" -Encoding utf8
    $null = Invoke-LabScreenshot -Name click-list-guess
}
