#!/usr/bin/env python3
"""Create the exact pre-deployment rollback archive for the bot server on 7777."""

from __future__ import annotations

import datetime as dt
import hashlib
import os
import socket
import sys
import zipfile


EXPECTED_HOST = "scpsl-warmup-hk"
LAB_ROOT = "/home/scpsl/.config/SCP Secret Laboratory/LabAPI"
BACKUP_ROOT = os.path.join(LAB_ROOT, "backups", "7777")

SNAPSHOT_FILES = (
    "plugins/7777/SCPSLBot.dll",
    "plugins/7777/SCPSLBot.Components.dll",
    "plugins/7777/WarmupSafezone.dll",
    "plugins/7777/WarmupPlayerPanel.dll",
    "configs/7777/SCPSLBot/config.yml",
    "configs/7777/WarmupSafezone/config.yml",
)

EXPECTED_ABSENT = (
    "plugins/7777/HintServiceMeow.dll",
    "plugins/7777/StatsBots.dll",
    "plugins/7777/StatsSystem.dll",
    "dependencies/7777/Microsoft.Bcl.AsyncInterfaces.dll",
    "dependencies/7777/MySqlConnector.dll",
    "dependencies/7777/ServerKeybinds.dll",
    "dependencies/7777/System.Buffers.dll",
    "dependencies/7777/System.Diagnostics.DiagnosticSource.dll",
    "dependencies/7777/System.IO.Pipelines.dll",
    "dependencies/7777/System.Memory.dll",
    "dependencies/7777/System.Numerics.Vectors.dll",
    "dependencies/7777/System.Runtime.CompilerServices.Unsafe.dll",
    "dependencies/7777/System.Text.Encodings.Web.dll",
    "dependencies/7777/System.Text.Json.dll",
    "dependencies/7777/System.Threading.Tasks.Extensions.dll",
    "dependencies/7777/System.ValueTuple.dll",
    "configs/7777/StatsBots/config.yml",
    "configs/7777/StatsSystem/config.yml",
)


def sha256(path: str) -> str:
    digest = hashlib.sha256()
    with open(path, "rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def fail(message: str) -> None:
    raise SystemExit(message)


def main() -> None:
    if len(sys.argv) != 2:
        fail(f"Usage: {sys.argv[0]} <snapshot.zip.bak>")
    if socket.gethostname() != EXPECTED_HOST:
        fail(f"Refusing backup on unexpected host {socket.gethostname()}")

    output = os.path.realpath(sys.argv[1])
    backup_root = os.path.realpath(BACKUP_ROOT)
    if os.path.dirname(output) != backup_root or not output.endswith(".zip.bak"):
        fail(f"Output must be a .zip.bak directly under {backup_root}")
    if os.path.exists(output):
        fail(f"Refusing to overwrite existing snapshot: {output}")

    missing = [path for path in SNAPSHOT_FILES if not os.path.isfile(os.path.join(LAB_ROOT, path))]
    if missing:
        fail("Required live files are missing:\n" + "\n".join(missing))
    unexpected = [path for path in EXPECTED_ABSENT if os.path.exists(os.path.join(LAB_ROOT, path))]
    if unexpected:
        fail("Expected pre-deployment paths are already present:\n" + "\n".join(unexpected))

    checksums = [(sha256(os.path.join(LAB_ROOT, path)), path) for path in SNAPSHOT_FILES]
    checksum_text = "".join(f"{digest}  {path}\n" for digest, path in checksums)
    created = dt.datetime.now(dt.timezone.utc).replace(microsecond=0).isoformat()
    manifest = "\n".join(
        (
            "SCPSLBot production rollback snapshot",
            f"created_utc={created}",
            f"host={EXPECTED_HOST}",
            "service=scpsl-warmup.service",
            "port=7777/udp",
            "scope=exact pre-deployment files only",
            "legacy_panel_present=plugins/7777/WarmupPlayerPanel.dll",
            "introduced_paths_were_absent=true",
            "rollback=run the adjacent Rollback-BotProduction7777.sh with this archive path",
            "",
        )
    )

    os.makedirs(backup_root, mode=0o750, exist_ok=True)
    with zipfile.ZipFile(output, "x", compression=zipfile.ZIP_DEFLATED) as archive:
        for relative_path in SNAPSHOT_FILES:
            archive.write(os.path.join(LAB_ROOT, relative_path), relative_path)
        archive.writestr("rollback.sha256", checksum_text)
        archive.writestr("manifest.txt", manifest)
    os.chmod(output, 0o640)
    print(output)
    print(sha256(output))


if __name__ == "__main__":
    main()
