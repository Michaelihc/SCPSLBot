param($Context)
$Context.Options.takeName = 'pursuit-door-recovery'
$Context.Options.cases = @('doctor-pursuit','rage-pursuit','phasing-pursuit','939-pursuit','militant')
$Context.Options.followScp = $true
& "$PSScriptRoot/pursuit-door-defense.ps1" -Context $Context
