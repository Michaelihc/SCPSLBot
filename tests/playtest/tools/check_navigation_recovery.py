"""Windows-only isolated 8891 startup fault probe; restores the port files after stopping it."""
import ctypes
from ctypes import wintypes
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


repo = Path(__file__).resolve().parents[3]
lab = Path(os.environ["APPDATA"]) / "SCP Secret Laboratory/LabAPI"
server = Path(r"C:\Program Files (x86)\Steam\steamapps\common\SCP Secret Laboratory Dedicated Server")
port = 8891
assert not ps("Get-CimInstance Win32_Process | Where-Object { $_.Name -in 'SCPSL.exe','LocalAdmin.exe' -and $_.CommandLine -match '(?:^|\\s)(?:-port)?8891(?:\\s|$)' } | Select-Object -ExpandProperty ProcessId"), "8891 is already running"
assert not ps("Get-NetUDPEndpoint | Where-Object LocalPort -eq 8891 | Select-Object -ExpandProperty OwningProcess"), "8891 is bound"
artifacts = repo / "tests/playtest/artifacts" / time.strftime("navigation-recovery-%Y%m%d-%H%M%S")
artifacts.mkdir(parents=True)
plugins = lab / "plugins/8891"
config = lab / "configs/8891/PlaytestHarness/config.yml"
loader = lab / "LabApi-8891.yml"
runs = config.parent / "runs"
originals = {}
lock = None
process = None
kernel = ctypes.WinDLL("kernel32", use_last_error=True)
kernel.CreateFileW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, ctypes.c_void_p, wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE]
kernel.CreateFileW.restype = wintypes.HANDLE
kernel.CloseHandle.argtypes = [wintypes.HANDLE]


def replace(path, data):
    originals[path] = path.read_bytes() if path.exists() else None
    (artifacts / (str(len(originals)) + "-" + path.name + ".bak")).write_bytes(originals[path] or b"")
    path.write_bytes(data)


try:
    for name, source in {
        "SCPSLBot.dll": repo / "SCPSLBot/bin/x64/Release/net48/SCPSLBot.dll",
        "SCPSLBot.PlaytestScenarios.dll": repo / "tests/playtest/bin/Release/SCPSLBot.PlaytestScenarios.dll",
    }.items():
        replace(plugins / name, source.read_bytes())
    settings = config.read_text(encoding="utf-8-sig")
    settings = re.sub(r"(?m)^auto_run_scenario:.*$", "auto_run_scenario: scpslbot-navigation-load-recovery", settings)
    settings = re.sub(r"(?m)^auto_run_level:.*$", "auto_run_level: standard", settings)
    settings = re.sub(r"(?m)^auto_run_delay_seconds:.*$", "auto_run_delay_seconds: 5", settings)
    replace(config, settings.encode("utf-8"))
    replace(loader, b"dependency_paths:\n- $port\nplugin_paths:\n- $port\nload_unsupported_plugins: false\n")
    # The lock only bites the authored backend; the runtime backend never reads navmesh.slnmf.
    bot_config = lab / "configs/8891/SCPSLBot/config.yml"
    bot_settings = bot_config.read_text(encoding="utf-8-sig")
    if re.search(r"(?m)^navigation:", bot_settings):
        bot_settings, count = re.subn(r"(?m)^([ \t]+backend:).*$", r"\1 Authored", bot_settings, count=1)
        assert count == 1, "navigation.backend not found"
    else:
        bot_settings += "\nnavigation:\n  backend: Authored\n"
    replace(bot_config, bot_settings.encode("utf-8"))
    nav = plugins / "SCPSLBot/navmesh.slnmf"
    assert nav.is_file(), "Existing test navigation file required"
    lock = kernel.CreateFileW(str(nav), 0x80000000, 0, None, 3, 0x80, None)
    assert lock != ctypes.c_void_p(-1).value, ctypes.get_last_error()
    previous = set(runs.glob("*.summary.json"))
    log_path = artifacts / "la8891.log"
    with log_path.open("wb") as log:
        process = subprocess.Popen([str(server / "LocalAdmin.exe"), str(port)], cwd=server,
            stdin=subprocess.PIPE, stdout=log, stderr=subprocess.STDOUT,
            creationflags=subprocess.CREATE_NO_WINDOW)
        print(f"Started isolated LocalAdmin pid={process.pid}; evidence={artifacts}", flush=True)
        deadline = time.monotonic() + 180
        released = False
        while time.monotonic() < deadline:
            text = log_path.read_text(encoding="utf-8", errors="replace")
            if not released and "NAV_LOAD_RETRY" in text and "failures=6 retry_seconds=15" in text:
                kernel.CloseHandle(lock)
                lock = None
                released = True
                print("Released exclusive navigation lock after six failed attempts (backoff reached 15s).", flush=True)
            summaries = set(runs.glob("*.summary.json")) - previous
            if summaries:
                summary = max(summaries, key=lambda p: p.stat().st_mtime)
                data = json.loads(summary.read_text(encoding="utf-8-sig"))
                shutil.copy2(summary, artifacts / summary.name)
                details = Path(data["artifacts"]["jsonl"])
                shutil.copy2(details, artifacts / details.name)
                assert released and "NAV_LOAD_RECOVERED" in text, "No recovery after injected fault"
                assert data["passed"] == 1 and data["failed"] == 0 and data["skipped"] == 0, data
                print(json.dumps(data), flush=True)
                break
            if process.poll() is not None:
                raise RuntimeError(f"LocalAdmin exited early: {process.returncode}")
            time.sleep(0.25)
        else:
            raise TimeoutError("Navigation recovery scenario did not complete")
finally:
    if lock is not None:
        kernel.CloseHandle(lock)
    if process is not None:
        # Stop LocalAdmin before its exact child so it cannot auto-respawn the game.
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
