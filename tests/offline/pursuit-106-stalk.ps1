param($Context)
$Context.Options.takeName = 'pursuit-106-stalk'
$Context.Options.cases = @('hurt-106-pursuit')
$Context.Options.followScp = $true
$Context.Options.pursuitCases = @(
    @{name='hurt-106-pursuit';role='ClassD';seconds=45;anchor='LCZ_CAFE';scpRole='Scp106';scpHealth=100;invulnerable=$true}
)
& "$PSScriptRoot/human-escape.ps1" -Context $Context
