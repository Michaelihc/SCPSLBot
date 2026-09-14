[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $InputPath,

    [Parameter(Mandatory = $true)]
    [string] $OutputPath,

    [string] $CecilPath,

    [string] $ReferenceDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$expectedInputSha256 = '08baa0ee8f11b42c542bee2a7a9c6ed5104388058f31f4eaabfcda7cc3d3c491'
$warmupManagerTypeName = 'SCPSLBot.Warmup.WarmupManager'
$playerTypeName = 'LabApi.Features.Wrappers.Player'

function Resolve-FullPath {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,

        [switch] $AllowMissing
    )

    if ($AllowMissing) {
        $parent = Split-Path -Parent $Path
        $leaf = Split-Path -Leaf $Path
        if ([string]::IsNullOrWhiteSpace($parent)) {
            $parent = (Get-Location).Path
        }

        $resolvedParent = (Resolve-Path -LiteralPath $parent).Path
        return [System.IO.Path]::Combine($resolvedParent, $leaf)
    }

    return (Resolve-Path -LiteralPath $Path).Path
}

function Resolve-CecilAssembly {
    param([string] $RequestedPath)

    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        return Resolve-FullPath -Path $RequestedPath
    }

    $userProfilePath = [Environment]::GetFolderPath('UserProfile')
    $ilSpyStore = [System.IO.Path]::Combine($userProfilePath, '.dotnet', 'tools', '.store', 'ilspycmd')
    if (-not (Test-Path -LiteralPath $ilSpyStore)) {
        throw 'Mono.Cecil.dll was not specified and the ilspycmd tool store was not found.'
    }

    $candidate = Get-ChildItem -LiteralPath $ilSpyStore -Recurse -Filter Mono.Cecil.dll |
        Sort-Object -Property FullName -Descending |
        Select-Object -First 1
    if ($null -eq $candidate) {
        throw 'Mono.Cecil.dll was not found under the ilspycmd tool store.'
    }

    return $candidate.FullName
}

function Get-SingleMethod {
    param(
        [Parameter(Mandatory = $true)]
        [Mono.Cecil.TypeDefinition] $Type,

        [Parameter(Mandatory = $true)]
        [string] $Name,

        [Parameter(Mandatory = $true)]
        [int] $ParameterCount
    )

    $matches = @($Type.Methods | Where-Object {
        $_.Name -eq $Name -and $_.Parameters.Count -eq $ParameterCount
    })
    if ($matches.Count -ne 1) {
        throw "Expected one $Name/$ParameterCount method, found $($matches.Count)."
    }

    return $matches[0]
}

function Get-CalledMethodReference {
    param(
        [Parameter(Mandatory = $true)]
        [Mono.Cecil.AssemblyDefinition] $Assembly,

        [Parameter(Mandatory = $true)]
        [string] $DeclaringType,

        [Parameter(Mandatory = $true)]
        [string] $MethodName
    )

    $matches = [System.Collections.Generic.List[Mono.Cecil.MethodReference]]::new()
    foreach ($type in $Assembly.MainModule.Types) {
        foreach ($method in $type.Methods) {
            if (-not $method.HasBody) {
                continue
            }

            foreach ($instruction in $method.Body.Instructions) {
                $reference = $instruction.Operand -as [Mono.Cecil.MethodReference]
                if ($null -ne $reference -and
                    $reference.DeclaringType.FullName -eq $DeclaringType -and
                    $reference.Name -eq $MethodName) {
                    $matches.Add($reference)
                }
            }
        }
    }

    if ($matches.Count -eq 0) {
        throw "Could not find a call to $DeclaringType::$MethodName."
    }

    return $matches[0]
}

function Test-IsCallTo {
    param(
        [Parameter(Mandatory = $true)]
        [Mono.Cecil.Cil.Instruction] $Instruction,

        [Parameter(Mandatory = $true)]
        [string] $DeclaringType,

        [Parameter(Mandatory = $true)]
        [string] $MethodName
    )

    $reference = $Instruction.Operand -as [Mono.Cecil.MethodReference]
    return $null -ne $reference -and
        $reference.DeclaringType.FullName -eq $DeclaringType -and
        $reference.Name -eq $MethodName
}

function Get-RemoteAdminCallCount {
    param([Parameter(Mandatory = $true)][Mono.Cecil.MethodDefinition] $Method)

    return @($Method.Body.Instructions | Where-Object {
        Test-IsCallTo -Instruction $_ -DeclaringType $playerTypeName -MethodName 'get_RemoteAdminAccess'
    }).Count
}

function Add-EventHandlerGuard {
    param(
        [Parameter(Mandatory = $true)]
        [Mono.Cecil.MethodDefinition] $Method,

        [Parameter(Mandatory = $true)]
        [Mono.Cecil.MethodReference] $GetPlayer,

        [Parameter(Mandatory = $true)]
        [Mono.Cecil.MethodReference] $GetRemoteAdminAccess
    )

    $processor = $Method.Body.GetILProcessor()
    $originalFirst = $Method.Body.Instructions[0]
    $callAdminGetter = [Mono.Cecil.Cil.Instruction]::Create(
        [Mono.Cecil.Cil.OpCodes]::Callvirt,
        $GetRemoteAdminAccess)

    $instructions = @(
        [Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ldarg_1),
        [Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Callvirt, $GetPlayer),
        [Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Dup),
        [Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Brtrue, $callAdminGetter),
        [Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Pop),
        [Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Br, $originalFirst),
        $callAdminGetter,
        [Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Brfalse, $originalFirst),
        [Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ret)
    )

    foreach ($instruction in $instructions) {
        $processor.InsertBefore($originalFirst, $instruction)
    }
}

function Add-PlayerArgumentGuard {
    param(
        [Parameter(Mandatory = $true)]
        [Mono.Cecil.MethodDefinition] $Method,

        [Parameter(Mandatory = $true)]
        [Mono.Cecil.MethodReference] $GetRemoteAdminAccess
    )

    $processor = $Method.Body.GetILProcessor()
    $originalFirst = $Method.Body.Instructions[0]
    $callAdminGetter = [Mono.Cecil.Cil.Instruction]::Create(
        [Mono.Cecil.Cil.OpCodes]::Callvirt,
        $GetRemoteAdminAccess)

    $instructions = @(
        [Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ldarg_1),
        [Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Dup),
        [Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Brtrue, $callAdminGetter),
        [Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Pop),
        [Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Br, $originalFirst),
        $callAdminGetter,
        [Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Brfalse, $originalFirst),
        [Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ret)
    )

    foreach ($instruction in $instructions) {
        $processor.InsertBefore($originalFirst, $instruction)
    }
}

function Add-BoundaryLoopGuard {
    param(
        [Parameter(Mandatory = $true)]
        [Mono.Cecil.MethodDefinition] $Method,

        [Parameter(Mandatory = $true)]
        [Mono.Cecil.MethodReference] $GetRemoteAdminAccess
    )

    $canRespawnCall = @($Method.Body.Instructions | Where-Object {
        Test-IsCallTo -Instruction $_ -DeclaringType $warmupManagerTypeName -MethodName 'CanWarmupRespawn'
    })
    if ($canRespawnCall.Count -ne 1) {
        throw "Expected one CanWarmupRespawn call, found $($canRespawnCall.Count)."
    }

    $insertionTarget = $canRespawnCall[0].Previous
    $failureBranch = $canRespawnCall[0].Next
    $continueTarget = $failureBranch.Operand -as [Mono.Cecil.Cil.Instruction]
    if ($null -eq $insertionTarget -or $null -eq $continueTarget) {
        throw 'Could not identify the arena-boundary loop guard and continue target.'
    }

    $playerVariable = $Method.Body.Variables[1]
    $processor = $Method.Body.GetILProcessor()
    $callAdminGetter = [Mono.Cecil.Cil.Instruction]::Create(
        [Mono.Cecil.Cil.OpCodes]::Callvirt,
        $GetRemoteAdminAccess)

    $instructions = @(
        [Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ldloc, $playerVariable),
        [Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Dup),
        [Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Brtrue, $callAdminGetter),
        [Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Pop),
        [Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Br, $continueTarget),
        $callAdminGetter,
        [Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Brtrue, $continueTarget)
    )

    foreach ($instruction in $instructions) {
        $processor.InsertBefore($insertionTarget, $instruction)
    }
}

$resolvedInputPath = Resolve-FullPath -Path $InputPath
$resolvedOutputPath = Resolve-FullPath -Path $OutputPath -AllowMissing
if ($resolvedInputPath -eq $resolvedOutputPath) {
    throw 'InputPath and OutputPath must be different.'
}

if (Test-Path -LiteralPath $resolvedOutputPath) {
    throw "Refusing to overwrite existing output: $resolvedOutputPath"
}

$actualInputSha256 = (Get-FileHash -LiteralPath $resolvedInputPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualInputSha256 -ne $expectedInputSha256) {
    throw "Input hash mismatch. Expected $expectedInputSha256, got $actualInputSha256."
}

$resolvedCecilPath = Resolve-CecilAssembly -RequestedPath $CecilPath
Add-Type -Path $resolvedCecilPath

$resolver = [Mono.Cecil.DefaultAssemblyResolver]::new()
$resolver.AddSearchDirectory((Split-Path -Parent $resolvedInputPath))
if (-not [string]::IsNullOrWhiteSpace($ReferenceDirectory)) {
    $resolver.AddSearchDirectory((Resolve-FullPath -Path $ReferenceDirectory))
}

$readerParameters = [Mono.Cecil.ReaderParameters]::new()
$readerParameters.InMemory = $true
$readerParameters.AssemblyResolver = $resolver
$temporaryOutputPath = [System.IO.Path]::Combine(
    (Split-Path -Parent $resolvedOutputPath),
    ".$(Split-Path -Leaf $resolvedOutputPath).$([Guid]::NewGuid().ToString('N')).tmp")
$assembly = $null
$patchedAssembly = $null
try {
    $assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($resolvedInputPath, $readerParameters)
    try {
        $warmupManager = $assembly.MainModule.GetType($warmupManagerTypeName)
        if ($null -eq $warmupManager) {
            throw "Type not found: $warmupManagerTypeName"
        }

        $changingRole = Get-SingleMethod -Type $warmupManager -Name 'OnPlayerChangingRole' -ParameterCount 1
        $enforceRole = Get-SingleMethod -Type $warmupManager -Name 'EnforcePlayerArenaRoleIfNeeded' -ParameterCount 1
        $enforceBoundaries = Get-SingleMethod -Type $warmupManager -Name 'EnforcePlayerArenaBoundaries' -ParameterCount 0

        foreach ($method in @($changingRole, $enforceRole, $enforceBoundaries)) {
            if ((Get-RemoteAdminCallCount -Method $method) -ne 0) {
                throw "$($method.Name) already contains a RemoteAdminAccess check."
            }
        }

        $getPlayer = Get-CalledMethodReference -Assembly $assembly -DeclaringType 'LabApi.Events.Arguments.PlayerEvents.PlayerChangingRoleEventArgs' -MethodName 'get_Player'
        $getRemoteAdminAccess = [Mono.Cecil.MethodReference]::new(
            'get_RemoteAdminAccess',
            $assembly.MainModule.TypeSystem.Boolean,
            $getPlayer.ReturnType)
        $getRemoteAdminAccess.HasThis = $true

        Add-EventHandlerGuard -Method $changingRole -GetPlayer $getPlayer -GetRemoteAdminAccess $getRemoteAdminAccess
        Add-PlayerArgumentGuard -Method $enforceRole -GetRemoteAdminAccess $getRemoteAdminAccess
        Add-BoundaryLoopGuard -Method $enforceBoundaries -GetRemoteAdminAccess $getRemoteAdminAccess

        foreach ($method in @($changingRole, $enforceRole, $enforceBoundaries)) {
            if ((Get-RemoteAdminCallCount -Method $method) -ne 1) {
                throw "$($method.Name) did not receive exactly one RemoteAdminAccess check."
            }
        }

        $assembly.Write($temporaryOutputPath)
    }
    finally {
        $assembly.Dispose()
        $assembly = $null
    }

    $patchedAssembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($temporaryOutputPath)
    try {
        $patchedWarmupManager = $patchedAssembly.MainModule.GetType($warmupManagerTypeName)
        foreach ($signature in @(
            @{ Name = 'OnPlayerChangingRole'; ParameterCount = 1 },
            @{ Name = 'EnforcePlayerArenaRoleIfNeeded'; ParameterCount = 1 },
            @{ Name = 'EnforcePlayerArenaBoundaries'; ParameterCount = 0 }
        )) {
            $method = Get-SingleMethod -Type $patchedWarmupManager -Name $signature.Name -ParameterCount $signature.ParameterCount
            if ((Get-RemoteAdminCallCount -Method $method) -ne 1) {
                throw "Written output failed verification for $($signature.Name)."
            }
        }
    }
    finally {
        $patchedAssembly.Dispose()
        $patchedAssembly = $null
    }

    [System.IO.File]::Move($temporaryOutputPath, $resolvedOutputPath)
}
finally {
    if ($null -ne $assembly) {
        $assembly.Dispose()
    }
    if ($null -ne $patchedAssembly) {
        $patchedAssembly.Dispose()
    }
    $resolver.Dispose()
    if (Test-Path -LiteralPath $temporaryOutputPath) {
        Remove-Item -LiteralPath $temporaryOutputPath
    }
}

$outputSha256 = (Get-FileHash -LiteralPath $resolvedOutputPath -Algorithm SHA256).Hash.ToLowerInvariant()
[pscustomobject]@{
    Input = $resolvedInputPath
    InputSha256 = $actualInputSha256
    Output = $resolvedOutputPath
    OutputSha256 = $outputSha256
    AdminExemptions = @(
        'role-change rewrite',
        'delayed arena-role rewrite',
        'recurring arena role and position enforcement'
    )
}
