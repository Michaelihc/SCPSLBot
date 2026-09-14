"""Run a native dummy-destruction acceptance on existing isolated port 8891; restore files."""
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
parser.add_argument("--scenario", default="scpslbot-destroyed-bot-recovery")
parser.add_argument("--expect-reproduction", action="store_true")
parser.add_argument("--timeout", type=int, default=240)
args = parser.parse_args()
repo = Path(__file__).resolve().parents[3]
lab = Path(os.environ["APPDATA"]) / "SCP Secret Laboratory/LabAPI"
server = Path(r"C:\Program Files (x86)\Steam\steamapps\common\SCP Secret Laboratory Dedicated Server")
launcher = repo.parents[1] / "LocalAdmin-V2/bin/Release/net8.0-windows/win-x64/publish/LocalAdmin.exe"
port = 8891
assert launcher.is_file(), launcher
assert not ps("Get-CimInstance Win32_Process | Where-Object { $_.Name -in 'SCPSL.exe','LocalAdmin.exe' -and $_.CommandLine -match '(?:^|\\s)(?:-port)?8891(?:\\s|$)' } | Select-Object -ExpandProperty ProcessId"), "8891 is running"
assert not ps("Get-NetUDPEndpoint | Where-Object LocalPort -eq 8891 | Select-Object -ExpandProperty OwningProcess"), "8891 is bound"
artifacts = repo / "tests/playtest/artifacts" / time.strftime("destroyed-bot-%Y%m%d-%H%M%S")
artifacts.mkdir(parents=True)
plugins = lab / "plugins/8891"
config = lab / "configs/8891/PlaytestHarness/config.yml"
bot_config = lab / "configs/8891/SCPSLBot/config.yml"
loader = lab / "LabApi-8891.yml"
runs = config.parent / "runs"
originals = {}
process = None


def preserve(path):
    if path not in originals:
        originals[path] = path.read_bytes() if path.exists() else None
        (artifacts / (str(len(originals)) + "-" + path.name + ".bak")).write_bytes(originals[path] or b"")


def replace(path, data):
    preserve(path)
    path.write_bytes(data)


try:
    for name, source in {
        "SCPSLBot.dll": repo / "SCPSLBot/bin/x64/Release/net48/SCPSLBot.dll",
        "SCPSLBot.PlaytestScenarios.dll": repo / "tests/playtest/bin/Release/SCPSLBot.PlaytestScenarios.dll",
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
    preserve(bot_config)  # Scenario commands and loader normalization can write configuration.
    replace(loader, b"dependency_paths:\n- $port\nplugin_paths:\n- $port\nload_unsupported_plugins: false\n")
    previous = set(runs.glob("*.summary.json"))
    log_path = artifacts / "la8891.log"
    with log_path.open("wb") as log:
        environment = dict(os.environ, SCPSL_OPS_STATE_ROOT=str(lab / "state/8891"))
        process = subprocess.Popen([str(launcher), str(port)], cwd=server, env=environment,
            stdin=subprocess.PIPE, stdout=log, stderr=subprocess.STDOUT,
            creationflags=subprocess.CREATE_NO_WINDOW)
        print(f"Started isolated LocalAdmin pid={process.pid}; evidence={artifacts}", flush=True)
        deadline = time.monotonic() + args.timeout
        while time.monotonic() < deadline:
            summaries = set(runs.glob("*.summary.json")) - previous
            if summaries:
                summary = max(summaries, key=lambda p: p.stat().st_mtime)
                data = json.loads(summary.read_text(encoding="utf-8-sig"))
                shutil.copy2(summary, artifacts / summary.name)
                details = Path(data["artifacts"]["jsonl"])
                shutil.copy2(details, artifacts / details.name)
                evidence = details.read_text(encoding="utf-8-sig") + log_path.read_text(encoding="utf-8", errors="replace")
                print(json.dumps(data), flush=True)
                if args.expect_reproduction:
                    assert data["failed"] == 1 and data["passed"] == 0, data
                    assert "PruneMissingEntries" in evidence and "GetHashCode" in evidence, "Failure is not the production signature"
                    print("REPRODUCED: native destruction poisons pruning with the production GetHashCode stack.", flush=True)
                else:
                    assert data["passed"] > 0 and data["failed"] == 0 and data["skipped"] == 0, data
                break
            if process.poll() is not None:
                raise RuntimeError(f"LocalAdmin exited early: {process.returncode}")
            time.sleep(0.25)
        else:
            raise TimeoutError("Scenario did not complete")
finally:
    if process is not None:
        children = ps(f"Get-CimInstance Win32_Process | Where-Object {{ $_.ParentProcessId -eq {process.pid} -and $_.Name -eq 'SCPSL.exe' }} | Select-Object -ExpandProperty ProcessId")
        if process.poll() is None:
            process.terminate()
            process.wait(timeout=15)
        for child in children.splitlines():
            ps(f"Stop-Process -Id {int(child)} -Force -ErrorAction SilentlyContinue")
    for path, data in originals.items():
        if data is None:
            path.unlink(missing_ok=True)
        else:
            path.write_bytes(data)
    print("Stopped isolated server and restored its plugin/config files.", flush=True)
