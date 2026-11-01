"""Package the committed candidate for offline-special-weapons.ps1 and print the host.py command."""
from pathlib import Path
import json
import sys
import time

repo = Path(__file__).resolve().parents[2]
workspace = next(parent for parent in repo.parents if (parent / ".tests/offline-clients/tools/host.py").is_file())
sys.path.insert(0, str(workspace / ".tests/offline-clients/tools"))
from package import package
from production_mirror import members, resolve_baseline

_, baseline = members(resolve_baseline())
plugins = [
    repo / "BotRoundControl/bin/x64/Release/net48/BotRoundControl.dll",
    repo / "SCPSLBot/bin/x64/Release/net48/SCPSLBot.dll",
    repo / "SCPSLBot.Components/bin/x64/Release/net48/SCPSLBot.Components.dll",
    workspace / "HsmAdapter/bin/x64/Release/net48/HsmAdapter.dll",
    workspace / "ServerKeybinds/bin/x64/Release/net48/ServerKeybinds.dll",
]
keep = {"plugins/8910/HintServiceMeow.dll", *("plugins/8910/" + path.name for path in plugins)}
keep.update(name for name in baseline if Path(name).name.startswith(("System.", "Microsoft.")))
# The Bot host runs these plugins only; gameplay-altering production plugins stay out of the mirror.
remove = [name for name in baseline if name.startswith("plugins/") and name.endswith(".dll") and name not in keep]
out = workspace / ".tests/offline-clients/runtime/candidates" / time.strftime("special-weapons-%Y%m%d-%H%M%S")
bundle = package(repo, out, plugins, [], [], "/special_weapon list", "guided-rocket", remove)
print(json.dumps(bundle))
print(
    "python tools/host.py run --package", bundle["package"],
    "--scenario", repo / "BotRoundControl/tests/offline-special-weapons.ps1",
    "--bindings", repo / "BotRoundControl/tests/special-weapons-bindings.json",
    "--audio",
)
