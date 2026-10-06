"""Run normal-round integration or autonomous escape on an isolated offline host slot."""
import argparse
import json
from pathlib import Path
import shutil
import subprocess
import sys
import time

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--escape", action="store_true")
args = parser.parse_args()
repo = Path(__file__).resolve().parents[3]
workspace = next(parent for parent in repo.parents if (parent / ".tests/offline-clients/tools/host.py").is_file())
host_repo = workspace / ".tests/offline-clients"
sys.path.insert(0, str(host_repo / "tools"))
from package import package
from production_mirror import resolve_baseline, members

fixture = "escape" if args.escape else "population"
scenario = "scpslbot-autonomous-escape" if args.escape else "scpslbot-normal-round"
artifacts = workspace / "scpsl-bot-plugin/tests/playtest/artifacts" / time.strftime(f"normal-round-{fixture}-%Y%m%d-%H%M%S")
artifacts.mkdir(parents=True)
# Verify every prepared slot without mutating shared media. The runner chooses the free slot.
patcher = workspace / "scpsl-bot-plugin/tools/NavMeshAssetPatcher/bin/Release/net8.0/NavMeshAssetPatcher.dll"
for slot in sorted((host_repo / "runtime/slots").glob("[1-6]")):
    if (slot / "host.json").is_file():
        verify = subprocess.run(["dotnet", str(patcher), "verify", "--server", str(slot / "Server")],
            capture_output=True, text=True, creationflags=subprocess.CREATE_NO_WINDOW)
        (artifacts / f"asset-verify-{slot.name}.log").write_text(verify.stdout + verify.stderr, encoding="utf-8")
        if verify.returncode:
            raise RuntimeError(f"Prepared slot {slot.name} failed navmesh asset verification; {artifacts}")

_, baseline = members(resolve_baseline())
plugins = [repo / "SCPSLBot/bin/x64/Release/net48/SCPSLBot.dll",
    repo / "SCPSLBot.Components/bin/x64/Release/net48/SCPSLBot.Components.dll",
    repo / "tests/playtest/bin/Release/SCPSLBot.PlaytestScenarios.dll",
    workspace / ".tests/Playtest/bin/Release/PlaytestHarness.dll",
    workspace / "HsmAdapter/bin/x64/Release/net48/HsmAdapter.dll",
    workspace / "ServerKeybinds/bin/x64/Release/net48/ServerKeybinds.dll"]
keep = {"plugins/8910/HintServiceMeow.dll", *("plugins/8910/" + path.name for path in plugins)}
# Only SCPSLBot's subjects and dependencies load; gameplay-altering production plugins are absent.
remove = [name for name in baseline if name.startswith("plugins/") and name.endswith(".dll") and name not in keep]
files = [f"configs/8910/SCPSLBot/config.yml=tests/playtest/fixtures/normal-round-{'escape' if args.escape else 'population'}.yml",
    f"configs/8910/PlaytestHarness/config.yml=tests/playtest/fixtures/{fixture}-harness.yml",
    f"configs/8910/SCPSLBot.PlaytestScenarios/config.yml=tests/playtest/fixtures/{fixture}-library.yml"]
bundle = package(repo, artifacts / "candidate", plugins, [], files, "/bot_status", "ai_runner_running=True", remove)
print(json.dumps(bundle), flush=True)
process = subprocess.Popen([sys.executable, str(host_repo / "tools/host.py"), "run", "--package", bundle["package"],
    "--server-only", "--hold", "12" if args.escape else "3"], cwd=host_repo,
    stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, encoding="utf-8", errors="replace")
job = None
evidence = None
summary = None
try:
    with (artifacts / "host.log").open("w", encoding="utf-8") as log:
        for line in process.stdout:
            log.write(line); log.flush()
            print(line.rstrip(), flush=True)
            try:
                message = json.loads(line)
            except ValueError:
                continue
            if "job" in message:
                job = message["job"]
            if message.get("state") != "held":
                continue
            # The job record identifies this run's exact evidence, never the newest shared run.
            record = json.loads((host_repo / "runtime/slots/jobs" / (job + ".json")).read_text(encoding="utf-8-sig"))
            evidence = Path(record["evidence"])
            deadline = time.monotonic() + (690 if args.escape else 120)
            while time.monotonic() < deadline:
                matches = list((evidence / "playtest-runs").glob("*.summary.json"))
                # While held, harness outputs remain inside this job's private server AppData.
                if not matches:
                    lab = evidence.parent.parent / "Server/AppData/SCP Secret Laboratory/LabAPI"
                    matches = list((lab / "configs" / str(message["port"]) / "PlaytestHarness/runs").glob("*.summary.json"))
                if matches:
                    candidate = matches[0]
                    data = json.loads(candidate.read_text(encoding="utf-8-sig"))
                    if any(case.get("name") == scenario for case in data.get("results", [])):
                        summary = data
                        shutil.copy2(candidate, artifacts / candidate.name)
                        shutil.copy2(Path(data["artifacts"]["jsonl"]), artifacts / Path(data["artifacts"]["jsonl"]).name)
                        break
                time.sleep(1)
            subprocess.run([sys.executable, str(host_repo / "tools/host.py"), "cancel", job], cwd=host_repo, check=True)
        process.wait(timeout=45)
finally:
    if process.poll() is None and job:
        subprocess.run([sys.executable, str(host_repo / "tools/host.py"), "cancel", job], cwd=host_repo)
        process.wait(timeout=45)
print(json.dumps(dict(scenario=scenario, summary=summary, evidence=str(evidence), artifacts=str(artifacts))), flush=True)
if not summary or summary["passed"] != 1 or summary["failed"] or summary["skipped"]:
    raise SystemExit(1)
