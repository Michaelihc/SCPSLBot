"""Run the native connector traversal survey on isolated port 8891 across one or more map seeds.

Each round boots a fresh headless server (new map seed), auto-runs the requested survey scenario,
collects every [BotSurvey] / [BotOrders] STALL line into the evidence folder, and stops the exact
LocalAdmin/SCPSL pair before restoring the port's plugin/config files.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import time


def ps(command):
    return subprocess.check_output(
        ["powershell", "-NoProfile", "-Command", command], text=True,
        creationflags=subprocess.CREATE_NO_WINDOW,
    ).strip()


parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--scenario", default="scpslbot-connector-survey")
parser.add_argument("--rounds", type=int, default=1, help="number of fresh map seeds to survey")
parser.add_argument("--expect-reproduction", action="store_true",
                    help="baseline mode: require at least one failed traversal instead of zero")
parser.add_argument("--timeout", type=int, default=1700)
parser.add_argument("--label", default="connector-survey")
args = parser.parse_args()
repo = Path(__file__).resolve().parents[3]
lab = Path(os.environ["APPDATA"]) / "SCP Secret Laboratory/LabAPI"
server = Path(r"C:\Program Files (x86)\Steam\steamapps\common\SCP Secret Laboratory Dedicated Server")
launcher = repo.parents[1] / "LocalAdmin-V2/bin/Release/net8.0-windows/win-x64/publish/LocalAdmin.exe"
port = 8891
assert launcher.is_file(), launcher
patcher = repo / "tools/NavMeshAssetPatcher/bin/Release/net8.0/NavMeshAssetPatcher.dll"
if not patcher.is_file():
    subprocess.check_call(["dotnet", "build", str(repo / "tools/NavMeshAssetPatcher/NavMeshAssetPatcher.csproj"), "-c", "Release"],
                          creationflags=subprocess.CREATE_NO_WINDOW)
verify = subprocess.run(["dotnet", str(patcher), "verify", "--server", str(server)], text=True, capture_output=True,
                        creationflags=subprocess.CREATE_NO_WINDOW)
print(verify.stdout.strip(), flush=True)
assert verify.returncode == 0, "The dedicated server assets are not patched for the runtime navmesh; run NavMeshAssetPatcher patch first"
assert not ps("Get-CimInstance Win32_Process | Where-Object { $_.Name -in 'SCPSL.exe','LocalAdmin.exe' -and $_.CommandLine -match '(?:^|\\s)(?:-port)?8891(?:\\s|$)' } | Select-Object -ExpandProperty ProcessId"), "8891 is running"
assert not ps("Get-NetUDPEndpoint | Where-Object LocalPort -eq 8891 | Select-Object -ExpandProperty OwningProcess"), "8891 is bound"
artifacts = repo / "tests/playtest/artifacts" / time.strftime(f"{args.label}-%Y%m%d-%H%M%S")
artifacts.mkdir(parents=True)
plugins = lab / "plugins/8891"
config = lab / "configs/8891/PlaytestHarness/config.yml"
bot_config = lab / "configs/8891/SCPSLBot/config.yml"
loader = lab / "LabApi-8891.yml"
runs = config.parent / "runs"
originals = {}
survey_pattern = re.compile(r"\[BotSurvey\]|\[BotOrders\] (STALL|NO_PATH|OFF_MESH|TELEPORT|GROUND_MISS)|\[BotNav\]|NAV_")


def preserve(path):
    if path not in originals:
        originals[path] = path.read_bytes() if path.exists() else None
        (artifacts / (str(len(originals)) + "-" + path.name + ".bak")).write_bytes(originals[path] or b"")


def replace(path, data):
    preserve(path)
    path.write_bytes(data)


def stop(process):
    children = ps(f"Get-CimInstance Win32_Process | Where-Object {{ $_.ParentProcessId -eq {process.pid} -and $_.Name -eq 'SCPSL.exe' }} | Select-Object -ExpandProperty ProcessId")
    if process.poll() is None:
        process.terminate()
        process.wait(timeout=15)
    for child in children.splitlines():
        ps(f"Stop-Process -Id {int(child)} -Force -ErrorAction SilentlyContinue")
    for _ in range(40):
        if not ps("Get-NetUDPEndpoint | Where-Object LocalPort -eq 8891 | Select-Object -ExpandProperty OwningProcess"):
            break
        time.sleep(0.5)


def run_round(index):
    previous = set(runs.glob("*.summary.json"))
    log_path = artifacts / f"la8891-round{index}.log"
    process = None
    try:
        with log_path.open("wb") as log:
            environment = dict(os.environ, SCPSL_OPS_STATE_ROOT=str(lab / "state/8891"))
            process = subprocess.Popen([str(launcher), str(port)], cwd=server, env=environment,
                stdin=subprocess.PIPE, stdout=log, stderr=subprocess.STDOUT,
                creationflags=subprocess.CREATE_NO_WINDOW)
            print(f"[round {index}] started isolated LocalAdmin pid={process.pid}", flush=True)
            deadline = time.monotonic() + args.timeout
            while time.monotonic() < deadline:
                summaries = set(runs.glob("*.summary.json")) - previous
                if summaries:
                    summary = max(summaries, key=lambda p: p.stat().st_mtime)
                    data = json.loads(summary.read_text(encoding="utf-8-sig"))
                    shutil.copy2(summary, artifacts / f"round{index}-{summary.name}")
                    details = Path(data["artifacts"]["jsonl"])
                    shutil.copy2(details, artifacts / f"round{index}-{details.name}")
                    return data, log_path
                if process.poll() is not None:
                    raise RuntimeError(f"LocalAdmin exited early: {process.returncode}")
                time.sleep(0.5)
            raise TimeoutError("Survey scenario did not complete")
    finally:
        if process is not None:
            stop(process)


results = []
try:
    for name, source in {
        "SCPSLBot.dll": repo / "SCPSLBot/bin/x64/Release/net48/SCPSLBot.dll",
        "SCPSLBot.PlaytestScenarios.dll": repo / "tests/playtest/bin/Release/SCPSLBot.PlaytestScenarios.dll",
        "PlaytestHarness.dll": repo.parent / ".tests/Playtest/bin/Release/PlaytestHarness.dll",
    }.items():
        payload = source.read_bytes()
        replace(plugins / name, payload)
        assert (plugins / name).read_bytes() == payload
        shutil.copy2(source, artifacts / name)
        print(f"{name} sha256={hashlib.sha256(payload).hexdigest()}", flush=True)
    settings = config.read_text(encoding="utf-8-sig")
    for key, value in {"auto_run_scenario": args.scenario, "auto_run_level": "standard", "auto_run_delay_seconds": "8"}.items():
        settings, count = re.subn(rf"(?m)^{key}:.*$", f"{key}: {value}", settings)
        assert count == 1, key
    replace(config, settings.encode("utf-8"))
    preserve(bot_config)
    replace(loader, b"dependency_paths:\n- $port\nplugin_paths:\n- $port\nload_unsupported_plugins: false\n")

    for index in range(1, args.rounds + 1):
        data, log_path = run_round(index)
        text = log_path.read_text(encoding="utf-8", errors="replace")
        survey_lines = [line for line in text.splitlines() if survey_pattern.search(line)]
        (artifacts / f"survey-round{index}.log").write_text("\n".join(survey_lines) + "\n", encoding="utf-8")
        summary_lines = [line for line in survey_lines if "[BotSurvey] SUMMARY" in line]
        print(f"[round {index}] scenario passed={data['passed']} failed={data['failed']} skipped={data['skipped']}", flush=True)
        for line in summary_lines:
            print(f"[round {index}] {line.strip()}", flush=True)
        results.append((data, summary_lines))

    total_failed_cases = 0
    for data, summary_lines in results:
        for line in summary_lines:
            match = re.search(r"failed=(\d+)", line)
            if match:
                total_failed_cases += int(match.group(1))
    if args.expect_reproduction:
        assert any(data["failed"] == 1 for data, _ in results) or total_failed_cases > 0, "No stuck case was reproduced"
        print(f"REPRODUCED: {total_failed_cases} failed traversal case(s) across {len(results)} map seed(s).", flush=True)
    else:
        # Suites may contain scenarios that legitimately skip (for example ones that need a real
        # client); a skip is never a failure, but it is reported.
        assert all(data["passed"] > 0 and data["failed"] == 0 for data, _ in results), results
        assert total_failed_cases == 0, total_failed_cases
        skipped = sum(data["skipped"] for data, _ in results)
        print(f"PASS: zero failed traversal cases across {len(results)} map seed(s); skipped scenarios={skipped}.", flush=True)
finally:
    for path, data in originals.items():
        if data is None:
            path.unlink(missing_ok=True)
        else:
            path.write_bytes(data)
    print(f"Restored port files. Evidence: {artifacts}", flush=True)
