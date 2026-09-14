[CmdletBinding()]
param(
    # Runtime identifiers to publish. win-x64 serves the local test ports; linux-x64 ships in the
    # production deployment package so the deploy script can verify/apply the patch on the host.
    [string[]]$Runtimes = @('win-x64', 'linux-x64')
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $PSScriptRoot 'NavMeshAssetPatcher\NavMeshAssetPatcher.csproj'
$outputRoot = Join-Path $PSScriptRoot 'NavMeshAssetPatcher\publish'

foreach ($runtime in $Runtimes) {
    $output = Join-Path $outputRoot $runtime
    dotnet publish $project -c Release -r $runtime --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $output
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $runtime."
    }

    $classData = Join-Path $output 'classdata.tpk'
    if (-not (Test-Path -LiteralPath $classData -PathType Leaf)) {
        throw "classdata.tpk was not published beside the $runtime binary."
    }

    Write-Host "Published NavMeshAssetPatcher ($runtime) to $output"
}
