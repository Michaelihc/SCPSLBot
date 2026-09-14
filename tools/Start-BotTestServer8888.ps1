[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$port = 8888
$serverRoot = 'C:\Program Files (x86)\Steam\steamapps\common\SCP Secret Laboratory Dedicated Server'
$localAdmin = Join-Path $serverRoot 'LocalAdmin.exe'
$stateRoot = Join-Path $env:APPDATA 'SCP Secret Laboratory\LabAPI\state\8888'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
# ServerKeybinds.Compat is AnyCPU and ignores the solution's x64 platform folder. Its canonical
# Release artifact is emitted directly under bin\Release; bin\x64 can retain a stale assembly from
# older project settings and must never be deployed.
$compatKeybinds = Join-Path $repositoryRoot 'ServerKeybinds.Compat\bin\Release\net48\ServerKeybinds.dll'
$portDependencyRoot = Join-Path $env:APPDATA 'SCP Secret Laboratory\LabAPI\dependencies\8888'
$deployedKeybinds = Join-Path $portDependencyRoot 'ServerKeybinds.dll'

if (-not (Test-Path -LiteralPath $localAdmin -PathType Leaf)) {
    throw "LocalAdmin.exe was not found at '$localAdmin'."
}

# The runtime navmesh needs readable collider meshes; Steam validation and game updates restore the
# stock (unreadable, streamed) asset files, so refuse to start an unpatched server.
$patcherProject = Join-Path $PSScriptRoot 'NavMeshAssetPatcher\NavMeshAssetPatcher.csproj'
$patcherDll = Join-Path $PSScriptRoot 'NavMeshAssetPatcher\bin\Release\net8.0\NavMeshAssetPatcher.dll'
if (-not (Test-Path -LiteralPath $patcherDll -PathType Leaf)) {
    dotnet build $patcherProject -c Release | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw 'Failed to build tools/NavMeshAssetPatcher.'
    }
}

& dotnet $patcherDll verify --server $serverRoot
if ($LASTEXITCODE -ne 0) {
    throw "The dedicated server assets are not patched for the runtime navmesh. Apply with: dotnet `"$patcherDll`" patch --server `"$serverRoot`""
}

$dedicatedGame = Join-Path $serverRoot 'SCPSL.exe'
$allProcesses = @(Get-CimInstance Win32_Process)
$existingGames = @($allProcesses | Where-Object {
    $_.Name -eq 'SCPSL.exe' -and
    $_.ExecutablePath -eq $dedicatedGame -and
    $_.CommandLine -match '(^|\s)-port8888(\s|$)'
})
$existingGameParents = @($existingGames | ForEach-Object ParentProcessId)
$existingAdmins = @($allProcesses | Where-Object {
    $_.Name -eq 'LocalAdmin.exe' -and
    $_.ExecutablePath -eq $localAdmin -and
    ($_.CommandLine -match '(^|\s)8888(\s|$)' -or $_.ProcessId -in $existingGameParents)
})
$existing = @($existingGames) + @($existingAdmins)
if ($existing) {
    throw 'Port 8888 already has a LocalAdmin/SCPSL process. Stop that exact port before starting another copy.'
}

if (-not (Test-Path -LiteralPath $compatKeybinds -PathType Leaf)) {
    throw "The ServerKeybinds.Compat release build was not found at '$compatKeybinds'. Build SCPSLBotAddon.sln for x64 Release before starting 8888."
}

# Keep the compatibility fork isolated to the dedicated bot-test lane. A sibling upstream build can
# otherwise replace it while still reporting assembly version 4.0.0.
New-Item -ItemType Directory -Path $portDependencyRoot -Force | Out-Null
$compatHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $compatKeybinds).Hash
$deployedHash = if (Test-Path -LiteralPath $deployedKeybinds -PathType Leaf) {
    (Get-FileHash -Algorithm SHA256 -LiteralPath $deployedKeybinds).Hash
} else {
    $null
}

if ($deployedHash -ne $compatHash) {
    Copy-Item -LiteralPath $compatKeybinds -Destination $deployedKeybinds -Force
    $verifiedHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $deployedKeybinds).Hash
    if ($verifiedHash -ne $compatHash) {
        throw "ServerKeybinds.Compat deployment verification failed. Expected $compatHash but found $verifiedHash."
    }

    Write-Host "Restored ServerKeybinds.Compat ($compatHash)."
}

New-Item -ItemType Directory -Path $stateRoot -Force | Out-Null
$env:SCPSL_OPS_STATE_ROOT = $stateRoot

$process = Start-Process -FilePath $localAdmin -ArgumentList $port -WorkingDirectory $serverRoot -PassThru
Write-Host "Started visible dedicated bot test server on port $port (LocalAdmin PID $($process.Id))."
Write-Host "Stats state root: $stateRoot"
