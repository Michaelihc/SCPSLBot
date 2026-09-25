[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$port = 8888
$serverRoot = 'C:\Program Files (x86)\Steam\steamapps\common\SCP Secret Laboratory Dedicated Server'
$localAdmin = Join-Path $serverRoot 'LocalAdmin.exe'
$stateRoot = Join-Path $env:APPDATA 'SCP Secret Laboratory\LabAPI\state\8888'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
# ServerKeybinds is built from source through SCPSLBot's ProjectReference. Default: the sibling
# metarepo checkout (<metarepo>\ServerKeybinds); override with $env:ServerKeybindsProject, the same
# path passed to MSBuild as -p:ServerKeybindsProject. The library is AnyCPU: a solution or x64 build
# emits it under bin\x64\Release\net48, a bare project build under bin\Release\net48. Use the newest.
$keybindsProject = if ($env:ServerKeybindsProject) { $env:ServerKeybindsProject } else { Join-Path (Split-Path -Parent $repositoryRoot) 'ServerKeybinds\ServerKeybinds.csproj' }
$keybindsBuild = @('bin\x64\Release\net48\ServerKeybinds.dll', 'bin\Release\net48\ServerKeybinds.dll') |
    ForEach-Object { Join-Path (Split-Path -Parent $keybindsProject) $_ } |
    Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
    Sort-Object { (Get-Item -LiteralPath $_).LastWriteTimeUtc } -Descending |
    Select-Object -First 1
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

if (-not $keybindsBuild) {
    throw "No ServerKeybinds release build was found beside '$keybindsProject'. Build SCPSLBotAddon.sln for x64 Release (it builds ServerKeybinds from '$keybindsProject') before starting 8888."
}

# Exactly one ServerKeybinds.dll per port, in the folder this port's LabAPI loader reads. Reinstall
# whenever the deployed copy differs from the current build so the lane never runs a stale library.
New-Item -ItemType Directory -Path $portDependencyRoot -Force | Out-Null
$keybindsHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $keybindsBuild).Hash
$deployedHash = if (Test-Path -LiteralPath $deployedKeybinds -PathType Leaf) {
    (Get-FileHash -Algorithm SHA256 -LiteralPath $deployedKeybinds).Hash
} else {
    $null
}

if ($deployedHash -ne $keybindsHash) {
    Copy-Item -LiteralPath $keybindsBuild -Destination $deployedKeybinds -Force
    $verifiedHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $deployedKeybinds).Hash
    if ($verifiedHash -ne $keybindsHash) {
        throw "ServerKeybinds deployment verification failed. Expected $keybindsHash but found $verifiedHash."
    }

    Write-Host "Installed ServerKeybinds ($keybindsHash)."
}

New-Item -ItemType Directory -Path $stateRoot -Force | Out-Null
$env:SCPSL_OPS_STATE_ROOT = $stateRoot

$process = Start-Process -FilePath $localAdmin -ArgumentList $port -WorkingDirectory $serverRoot -PassThru
Write-Host "Started visible dedicated bot test server on port $port (LocalAdmin PID $($process.Id))."
Write-Host "Stats state root: $stateRoot"
