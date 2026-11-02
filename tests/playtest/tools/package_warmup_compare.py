"""Package one side of the production-vs-release warmup comparison as a host.py candidate bundle.

Both sides load the same plugin set, the same production configs and the same native probe. Only the
six product DLLs differ, so any measured difference comes from those DLLs.
"""
import argparse
import json
import os
from pathlib import Path
import sys
import tempfile
import time

PRODUCT_DLLS = ["SCPSLBot.dll", "SCPSLBot.Components.dll", "BotRoundControl.dll", "ServerKeybinds.dll",
    "HsmAdapter.dll", "WarmupSafezone.dll"]
RELEASE_FOLDERS = {"SCPSLBot.dll": "plugins", "SCPSLBot.Components.dll": "plugins", "HsmAdapter.dll": "plugins",
    "BotRoundControl.dll": "optional/plugins", "ServerKeybinds.dll": "optional/plugins", "WarmupSafezone.dll": "optional/plugins"}

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("build", choices=["baseline", "candidate"])
parser.add_argument("--production", type=Path, default=Path(os.environ["LOCALAPPDATA"]) / "Temp/t3-prod")
parser.add_argument("--release", type=Path, default=Path(os.environ["LOCALAPPDATA"]) / "Temp/t3-release/stage")
parser.add_argument("--map-seed", type=int, default=0, help="probe-applied map seed; zero keeps the host seed")
parser.add_argument("--lcz-scp-bots", type=int, help="stress override of light_containment_scp_bot_count")
args = parser.parse_args()

repo = Path(__file__).resolve().parents[3]
workspace = Path(os.environ["SCPSL_METAREPO"]) if "SCPSL_METAREPO" in os.environ else next(
    parent for parent in repo.parents if (parent / ".tests/offline-clients/tools/host.py").is_file())
host_repo = workspace / ".tests/offline-clients"
sys.path.insert(0, str(host_repo / "tools"))
from package import package
from production_mirror import resolve_baseline, members

source = {name: (args.production / name if args.build == "baseline" else args.release / RELEASE_FOLDERS[name] / name)
    for name in PRODUCT_DLLS}
probe = repo / "tests/warmup-soak-probe/bin/Release/WarmupSoakProbe.dll"
plugins = [*source.values(), probe]

work = Path(tempfile.mkdtemp(prefix="warmup-compare-"))
management = (args.production / "BotRoundControl-config.yml").read_text(encoding="utf-8-sig")
if args.lcz_scp_bots is not None:
    management = management.replace("light_containment_scp_bot_count: 1", f"light_containment_scp_bot_count: {args.lcz_scp_bots}")
    if f"light_containment_scp_bot_count: {args.lcz_scp_bots}" not in management:
        raise RuntimeError("Could not apply the LCZ SCP stress override")
management_file = work / "management.yml"
management_file.write_text(management, encoding="utf-8")
probe_file = work / "probe.yml"
probe_file.write_text(f"map_seed: {args.map_seed}\n", encoding="utf-8")
files = [f"configs/8910/SCPSLBot/config.yml={args.production / 'SCPSLBot-config.yml'}",
    f"configs/8910/BotRoundControl/config.yml={management_file}",
    f"configs/8910/WarmupSoakProbe/config.yml={probe_file}"]

_, mirror = members(resolve_baseline())
keep = {"plugins/8910/HintServiceMeow.dll", *("plugins/8910/" + path.name for path in plugins)}
keep.update(name for name in mirror if Path(name).name.startswith(("System.", "Microsoft.")))
remove = [name for name in mirror if name.startswith("plugins/") and name.endswith(".dll") and name not in keep]

label = "-".join(str(part) for part in (args.build, f"seed{args.map_seed}" if args.map_seed else None,
    f"scp{args.lcz_scp_bots}" if args.lcz_scp_bots is not None else None, time.strftime("%Y%m%d-%H%M%S")) if part)
out = host_repo / "runtime/warmup-compare" / label
out.parent.mkdir(parents=True, exist_ok=True)
bundle = package(repo, out, plugins, [], files, "/bot_status", "ai_runner_running=True", remove)
print(json.dumps(dict(bundle, build=args.build, dlls={name: str(path) for name, path in source.items()})))
