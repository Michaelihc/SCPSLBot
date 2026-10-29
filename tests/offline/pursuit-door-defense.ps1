param($Context)
# The negative case has a nearby immobile SCP; all other cases let native AI pursue freely.
# No door-close commands are issued: human closures and SCP responses must come from gameplay.
if (!$Context.Options.takeName) { $Context.Options.takeName = 'pursuit-door-defense' }
$Context.Options.pursuitCases = @(
    @{name='nearby-stationary';role='ClassD';seconds=15;anchor='GR18';scpRole='Scp0492';stationary=$true;closures='forbidden'},
    @{name='zombie-pursuit';role='ClassD';seconds=40;anchor='GR18';scpRole='Scp0492';closures='required'},
    @{name='doctor-pursuit';role='Scientist';seconds=40;anchor='LCZ_CAFE';scpRole='Scp049';invulnerable=$true;abilities=@('049.Sense','049.DoctorsCall')},
    @{name='rage-pursuit';role='ClassD';seconds=45;anchor='GR18';scpRole='Scp096';abilities=@('096.Enraged','096.Charged')},
    @{name='phasing-pursuit';role='ClassD';seconds=50;anchor='LCZ_CAFE';scpRole='Scp106';invulnerable=$true;closures='forbidden'},
    @{name='surface-106-pursuit';role='ClassD';seconds=45;anchor='SURFACE_NUKE';scpAnchor='SURFACE_GATE';scpRole='Scp106';invulnerable=$true;abilities=@('106.HunterAtlas')},
    @{name='939-pursuit';role='ClassD';seconds=40;anchor='GR18';scpRole='Scp939'},
    @{name='militant';role='FacilityGuard';seconds=55;anchor='GR18';scpRole='Scp0492'}
)
& "$PSScriptRoot/human-escape.ps1" -Context $Context
