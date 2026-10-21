param($Context)
$Context.Options.cases = @('unarmed-classd')
$Context.Options.takeName = 'human-escape-chase-view'
$Context.Options.followScp = $true
& "$PSScriptRoot/human-escape.ps1" -Context $Context
