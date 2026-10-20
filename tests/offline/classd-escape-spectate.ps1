param($Context)
$ErrorActionPreference = 'Stop'
# Normal round with one SCPSLBot Class-D: the real client spectates it after a native death and one uncut
# take records the autonomous escape (keycards, SCP-914, checkpoints, elevators, Surface exit).
$escapeDeadlineSeconds = 1500
$log = Join-Path $Context.Evidence 'classd-escape.log'
function Note([string]$text) { "$(Get-Date -Format HH:mm:ss) $text" | Add-Content $log -Encoding utf8 }
function Reply([string]$command) { $reply = "$(Invoke-LabServer $command)"; Note "$command => $reply"; $reply }
function RecordingState { (Invoke-LabSetup 'record-status').setupResponse | ConvertFrom-Json }
function WaitRecording([string]$phase, [int]$seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    do {
        $state = RecordingState
        if ($state.error -or $state.phase -eq 'failed') { throw "Continuous recording failed: $($state.error)" }
        if ($state.phase -eq $phase) { return }
        Start-Sleep -Milliseconds 200
    } while ((Get-Date) -lt $deadline)
    throw "Continuous recording did not reach $phase"
}

$client = @(Observe -ClientsOnly)[0]
function Bot { @(Observe | Where-Object { $_.id -ne $client.id })[0] }

$deadline = (Get-Date).AddSeconds(60)
while (-not (Bot) -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 1 }
$bot = Bot
if (-not $bot) { throw 'normal_round_bot_count did not create the SCPSLBot dummy' }
Note "client=$($client.id) bot=$($bot.id) role=$($bot.role)"

if ((Reply '/forcestart') -notmatch 'Forced round start') { throw 'Round did not start' }
$deadline = (Get-Date).AddSeconds(20)
while ((Reply '/roundtime') -notmatch 'Round time:' -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 1 }
Start-Sleep -Seconds 2
$null = Reply "/forcerole $($bot.id) ClassD"
$null = Reply '/decontamination disable'
$null = Reply '/wave forcepause NTF true'
$null = Reply '/wave forcepause Chaos true'
# Overwatch and a forced Spectator role start without a target; a native death runs the
# spectator's automatic target selection, and the paused waves keep the client spectating.
$null = Reply "/forcerole $($client.id) Scientist"
Start-Sleep -Seconds 3
$null = Reply "/god $($client.id) disable"
$null = Reply "/kill $($client.id)"
Start-Sleep -Seconds 12
$bot = Bot
$client = @(Observe -ClientsOnly)[0]
if ($bot.role -ne 'ClassD') { throw "Bot is $($bot.role), not ClassD" }
if ($client.role -ne 'Spectator') { throw "Client is $($client.role), not Spectator" }
Note "spectating client camera=$($client.camera | ConvertTo-Json -Compress) bot=$($bot.position | ConvertTo-Json -Compress)"
$null = Invoke-LabScreenshot -Name spectate-start

$escaped = $false
$started = $false
$roundStart = Get-Date
try {
    Invoke-LabSetup 'record-start classd-escape' | Out-Null
    $started = $true
    WaitRecording 'recording' 45
    Note 'recording started'
    # Respawn the bot in the Class-D cells so the take covers the escape from the start; the
    # spectator target follows the same player through the role change.
    $null = Reply "/forcerole $($bot.id) ClassD"
    $roundStart = Get-Date
    Start-Sleep -Seconds 2
    $nextDebug = Get-Date
    while (((Get-Date) - $roundStart).TotalSeconds -lt $escapeDeadlineSeconds) {
        $bot = Bot
        if ($bot.role -eq 'Spectator') { Note "bot died at $($bot.position | ConvertTo-Json -Compress)"; break }
        if ($bot.role -ne 'ClassD') {
            $escaped = $true
            Note "bot escaped as $($bot.role) after $([int]((Get-Date) - $roundStart).TotalSeconds)s"
            Start-Sleep -Seconds 6
            break
        }
        if ((Get-Date) -ge $nextDebug) {
            $nextDebug = (Get-Date).AddSeconds(15)
            $null = Reply '/bot_debug'
        }
        Start-Sleep -Seconds 1
    }
}
finally {
    if ($started) {
        Invoke-LabSetup 'record-stop' | Out-Null
        WaitRecording 'complete' 300
        Note 'recording complete'
    }
    # Per-port plugin state is deleted at shutdown; keep the round trace with the evidence.
    $traces = Join-Path (Split-Path (Split-Path $Context.Evidence)) "Server/AppData/SCP Secret Laboratory/LabAPI/plugins/$($Context.Port)/SCPSLBot/traces"
    if (Test-Path $traces) { Copy-Item (Join-Path $traces '*.jsonl') $Context.Evidence }
}

if (-not $escaped) { throw "Class-D bot did not escape within $escapeDeadlineSeconds s; see $log" }
