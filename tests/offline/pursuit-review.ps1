param($Context)
if (!$Context.Options.takeName) { $Context.Options.takeName = 'pursuit-review' }
$Context.Options.pursuitCases = @(
    @{name='nearby-stationary';role='ClassD';seconds=15;anchor='GR18';scpRole='Scp0492';stationary=$true;closures='forbidden'},
    @{name='keycard-pursuit';role='ClassD';seconds=40;anchor='GR18';scpRole='Scp0492';keycard=$true;closures='required'},
    @{name='doctor-pursuit';role='Scientist';seconds=40;anchor='LCZ_CAFE';scpRole='Scp049';invulnerable=$true;abilities=@('049.Sense','049.DoctorsCall')},
    @{name='rage-pursuit';role='ClassD';seconds=45;anchor='GR18';scpRole='Scp096';abilities=@('096.Enraged','096.Charged')},
    @{name='hurt-106-pursuit';role='ClassD';seconds=45;anchor='LCZ_CAFE';scpRole='Scp106';scpHealth=100;invulnerable=$true;closures='forbidden';abilities=@('106.StalkMode')}
)
& "$PSScriptRoot/human-escape.ps1" -Context $Context
