"""Run normal-round integration or autonomous escape on an isolated offline host slot."""
import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import time

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--escape", action="store_true")
parser.add_argument("--scenario", help="run one named scenario on a bots-only fixture instead")
parser.add_argument("--backend", choices=["Runtime", "Authored"], default="Runtime")
parser.add_argument("--level", default="standard")
parser.add_argument("--limit", type=int, default=900, help="seconds the named scenario may run")
parser.add_argument("--management", action="store_true",
    help="load BotRoundControl (bots_only: false, Standard warmup) for --scenario runs that need bot_warmup")
parser.add_argument("--companions", action="store_true", help="with --management, also load WarmupSafezone and StatsBots")
parser.add_argument("--management-config", metavar="FILE",
    help="with --management, replace BotRoundControl's config with this fixture (e.g. halloween-moon-management.yml)")
parser.add_argument("--harness-config", metavar="FILE", help="replace the generated PlaytestHarness config with this fixture")
parser.add_argument("--extensions", action="store_true",
    help="also load the test-only SCPSLPluginExtensions.dll (plugin_direload) for reload scenarios")
parser.add_argument("--map-seed", type=int, help="map seed for a named scenario; the host otherwise fixes one seed")
parser.add_argument("--round-bots", type=int, default=0, help="with --scenario, independent round bots to create and record with round_trace")
parser.add_argument("--max-bots", type=int, help="with --scenario, override SCPSLBot's max_bot_count")
parser.add_argument("--auto-run-delay", type=int, default=20, help="seconds before the harness runs a named scenario; a full lobby starts its round sooner than the default")
parser.add_argument("--reconcile-interval", type=int, help="with --scenario, navigation.reconcile_interval_seconds")
parser.add_argument("--native-team-queue", help="with --scenario, the native team queue that allocates those bots' roles")
args = parser.parse_args()
repo = Path(__file__).resolve().parents[3]
# A worktree outside the metarepo names it explicitly.
workspace = Path(os.environ["SCPSL_METAREPO"]) if "SCPSL_METAREPO" in os.environ else next(
    parent for parent in repo.parents if (parent / ".tests/offline-clients/tools/host.py").is_file())
host_repo = workspace / ".tests/offline-clients"
sys.path.insert(0, str(host_repo / "tools"))
from package import package
from production_mirror import resolve_baseline, members

fixture = args.scenario or ("escape" if args.escape else "population")
scenario = args.scenario or ("scpslbot-autonomous-escape" if args.escape else "scpslbot-normal-round")
artifacts = workspace / "scpsl-bot-plugin/tests/playtest/artifacts" / time.strftime(f"normal-round-{fixture}-%Y%m%d-%H%M%S")
artifacts.mkdir(parents=True)
# Runtime navigation needs readable server meshes; Authored does not consume these assets.
# Verify every prepared slot without mutating shared media. The runner chooses the free slot.
patcher = workspace / "scpsl-bot-plugin/tools/NavMeshAssetPatcher/bin/Release/net8.0/NavMeshAssetPatcher.dll"
for slot in sorted((host_repo / "runtime/slots").glob("[1-6]")):
    if args.backend == "Runtime" and (slot / "host.json").is_file():
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
if args.management:
    if not args.scenario:
        parser.error("--management needs --scenario")
    # BotRoundControl builds AnyCPU; the x64 folder is checked first in case that changes.
    management = next((repo / "BotRoundControl/bin" / folder / "BotRoundControl.dll" for folder in ("x64/Release/net48", "Release/net48")
        if (repo / "BotRoundControl/bin" / folder / "BotRoundControl.dll").is_file()), None)
    if management is None:
        raise RuntimeError("Build BotRoundControl before using --management")
    plugins.append(management)
    if args.companions:
        plugins += [repo / "WarmupSafezone/bin/x64/Release/net48/WarmupSafezone.dll", repo / "StatsBots/bin/x64/Release/net48/StatsBots.dll"]
elif args.companions or args.management_config:
    parser.error("--companions and --management-config need --management")
if args.extensions:
    extensions = next((repo / "SCPSLPluginExtensions/bin" / folder / "SCPSLPluginExtensions.dll"
        for folder in ("x64/Release/net48", "Release/net48") if (repo / "SCPSLPluginExtensions/bin" / folder / "SCPSLPluginExtensions.dll").is_file()), None)
    if extensions is None:
        raise RuntimeError("Build SCPSLPluginExtensions before using --extensions")
    plugins.append(extensions)
keep = {"plugins/8910/HintServiceMeow.dll", *("plugins/8910/" + path.name for path in plugins)}
keep.update(name for name in baseline if Path(name).name.startswith(("System.", "Microsoft.")))
# Only SCPSLBot's subjects and dependencies load; gameplay-altering production plugins are absent.
remove = [name for name in baseline if name.startswith("plugins/") and name.endswith(".dll") and name not in keep]
if args.scenario:
    # Named scenarios start from an empty server: they create every bot they observe.
    bot_fixture = artifacts / "bot.yml"
    bot_fixture.write_text(chr(10).join(["language: cn", "bots_only: true", f"normal_round_bot_count: {args.round_bots}",
        *([f"max_bot_count: {args.max_bots}"] if args.max_bots is not None else []), "navigation:",
        f"  backend: {args.backend}", *([f"  reconcile_interval_seconds: {args.reconcile_interval}"] if args.reconcile_interval else []), *(["round_trace:", "  enabled: true", "  sample_interval_seconds: 1"] if args.round_bots else []), ""]), encoding="utf-8")
    harness_fixture = artifacts / "harness.yml"
    harness_fixture.write_text(chr(10).join([f"auto_run_scenario: {scenario}", f"auto_run_level: {args.level}",
        f"auto_run_delay_seconds: {args.auto_run_delay}", ""]), encoding="utf-8")
    if args.harness_config:
        harness_fixture = Path(args.harness_config).resolve()
    files = [f"configs/8910/SCPSLBot/config.yml={bot_fixture}", f"configs/8910/PlaytestHarness/config.yml={harness_fixture}"]
    if args.management:
        management_fixture = artifacts / "management.yml"
        management_fixture.write_text(chr(10).join(["language: cn", "bots_only: false", "warmup_mode: Standard", ""]), encoding="utf-8")
        if args.management_config:
            management_fixture = Path(args.management_config).resolve()
        files.append(f"configs/8910/BotRoundControl/config.yml={management_fixture}")
    cap_fixture = scenario in {"scpslbot-raised-bot-cap", "scpslbot-clamped-bot-cap", "scpslbot-disabled-bot-cap"}
    if args.map_seed or args.native_team_queue or cap_fixture:
        library_fixture = artifacts / "library.yml"
        library_fixture.write_text("".join([f"map_seed: {args.map_seed}\n" if args.map_seed else "",
            f"native_team_queue: '{args.native_team_queue}'\n" if args.native_team_queue else "",
            "lock_lobby: true\n" if cap_fixture else ""]), encoding="utf-8")
        files.append(f"configs/8910/SCPSLBot.PlaytestScenarios/config.yml={library_fixture}")
else:
    files = [f"configs/8910/SCPSLBot/config.yml=tests/playtest/fixtures/normal-round-{'escape' if args.escape else 'population'}.yml",
        f"configs/8910/PlaytestHarness/config.yml=tests/playtest/fixtures/{fixture}-harness.yml",
        f"configs/8910/SCPSLBot.PlaytestScenarios/config.yml=tests/playtest/fixtures/{fixture}-library.yml"]
bundle = package(repo, artifacts / "candidate", plugins, [], files, "/bot_status", "ai_runner_running=True", remove)
print(json.dumps(bundle), flush=True)
limit = args.limit if args.scenario else 690 if args.escape else 120
process = subprocess.Popen([sys.executable, str(host_repo / "tools/host.py"), "run", "--package", bundle["package"],
    "--server-only", "--hold", str(limit // 60 + 1)], cwd=host_repo,
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
            if message.get("phase") == "server-ready" and message.get("errors"):
                raise RuntimeError(f"Candidate setup has loader errors; inspect {artifacts / 'host.log'}")
            if message.get("state") != "held":
                continue
            # The job record identifies this run's exact evidence, never the newest shared run.
            record = json.loads((host_repo / "runtime/slots/jobs" / (job + ".json")).read_text(encoding="utf-8-sig"))
            evidence = Path(record["evidence"])
            deadline = time.monotonic() + limit
            while time.monotonic() < deadline:
                if process.poll() is not None:
                    break
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
# The driver reports one scenario; a skipped case is never an acceptance.
if not summary or summary["passed"] != 1 or summary["failed"] or summary["skipped"]:
    raise SystemExit(1)
