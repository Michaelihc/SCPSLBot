param($Context)
# Recon for the warmup comparison: what round state, roles and bots a joining client meets, the SSS
# menu it receives, and whether role routing reaches each arena.
$log="$($Context.Evidence)\scenario-server.txt"
$id=$Context.Actor.id
function Server([string]$Command) { $reply=(Invoke-LabServer $Command) -join "`n"; "> $Command`n$reply" | Add-Content $log -Encoding utf8; $reply }
function Dump([string]$Label) {
    "== $Label" | Add-Content $log -Encoding utf8
    (Observe | ForEach-Object { "$($_.id) $($_.role) dummy=$($_.dummy) hp=$($_.health) god=$($_.godMode) pos=$($_.position.x),$($_.position.y),$($_.position.z) held=$($_.held)" }) | Add-Content $log -Encoding utf8
}
$null=Server "/soakprobe start $($Context.Evidence)\probe.jsonl"
Dump 'connected'
$null=Server '/bot_warmup'
$null=Server '/bot_status'
$null=Server '/bot_server_status'
$null=Server '/roundtime'
Get-LabSettings | ConvertTo-Json -Depth 8 | Set-Content "$($Context.Evidence)\sss-lobby.json" -Encoding utf8
$null=Server '/forcestart'
Start-Sleep -Seconds 12
Dump 'after-forcestart'
$null=Server '/roundtime'
$null=Server '/bot_server_status'
$null=Server "/forcerole $id NtfPrivate"
Start-Sleep -Seconds 4
Dump 'ntf'
$null=Server "/god $id disable"
$null=Invoke-LabScreenshot -Name surface-spawn
Get-LabSettings | ConvertTo-Json -Depth 8 | Set-Content "$($Context.Evidence)\sss-ntf.json" -Encoding utf8
Start-Sleep -Seconds 10
Dump 'ntf+14s'
$null=Server '/bot_server_status'
$null=Server "/forcerole $id ClassD"
Start-Sleep -Seconds 4
Dump 'surface-to-pvpve'
$null=Server "/forcerole $id NtfPrivate"
Start-Sleep -Seconds 12
Dump 'pvpve-ntf'
$null=Server '/bot_server_status'
$null=Invoke-LabScreenshot -Name pvpve
$null=Server "/forcerole $id Scp173"
Start-Sleep -Seconds 4
Dump 'pvpve-scp'
Server '/soakprobe stop' | Out-Null
