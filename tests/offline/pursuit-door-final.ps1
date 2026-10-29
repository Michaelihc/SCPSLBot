param($Context)
$Context.Options.takeName = 'pursuit-door-final'
$Context.Options.cases = @('phasing-pursuit','939-pursuit','militant')
$Context.Options.followScp = $true
& "$PSScriptRoot/pursuit-door-defense.ps1" -Context $Context
