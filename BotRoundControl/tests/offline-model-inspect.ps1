param($Context)
$ErrorActionPreference = 'Stop'
@{status=(Invoke-LabServer '/duel_status');actors=@(Observe -ClientsOnly)} |
    ConvertTo-Json -Depth 12 | Set-Content "$($Context.Evidence)/duel-model-inspect.json" -Encoding utf8
