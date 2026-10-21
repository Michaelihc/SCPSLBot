param($Context)
$Context.Options.takeName = 'pursuit-door-remaining'
$Context.Options.cases = @('surface-106-pursuit','939-pursuit','militant')
$Context.Options.followScp = $true
& "$PSScriptRoot/pursuit-door-defense.ps1" -Context $Context
