param($Context)
$Context.Options.cases = @('militant','blocked-civilian')
$Context.Options.takeName = 'human-escape-recovery'
& "$PSScriptRoot/human-escape.ps1" -Context $Context
