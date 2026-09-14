"""Verified single-DLL fallback for bot host without scpsl-ops; does not restart the game."""
import hashlib
import os
from pathlib import Path
import socket
import sys
import time
import zipfile


def digest(data):
    return hashlib.sha256(data).hexdigest()


assert socket.gethostname() == "scpsl-warmup-hk", "Unexpected host"
source = Path(sys.argv[1]).resolve(strict=True)
assert source.parent == Path("/tmp") and source.name.startswith("scpsl-destroyed-recovery-"), source
expected = "8b4163539d8342c4d817ed4af9d835ebe7d0908c338bf2fe01b7183023cd8d26"
previous = "821d5978bd70ed3533b21a182c601aff5571628b5354a831e8c8f937251a3790"
payload = source.read_bytes()
assert digest(payload) == expected, "Upload differs from accepted artifact"
lab = Path("/home/scpsl/.config/SCP Secret Laboratory/LabAPI")
target = lab / "plugins/7777/SCPSLBot.dll"
original = target.read_bytes()
assert digest(original) == previous, "Production changed since preflight"
metadata = target.stat()
stamp = time.strftime("%Y%m%d-%H%M%S", time.gmtime())
backup = lab / "backups/7777" / ("destroyed-bot-recovery-" + stamp + ".zip.bak")
backup.parent.mkdir(parents=True, exist_ok=True)
entry = "plugins/7777/SCPSLBot.dll"
with zipfile.ZipFile(backup, "x", zipfile.ZIP_DEFLATED) as archive:
    archive.writestr(entry, original)
    archive.writestr("rollback.sha256", f"{previous}  {entry}\n")
with zipfile.ZipFile(backup) as archive:
    assert archive.testzip() is None and archive.read(entry) == original, "Backup verification failed"
os.chown(backup, metadata.st_uid, metadata.st_gid)
os.chmod(backup, 0o640)
staged = target.with_name("SCPSLBot.dll.next-destroyed-recovery-" + stamp)
try:
    with staged.open("xb") as output:
        output.write(payload)
        output.flush()
        os.fsync(output.fileno())
    os.chown(staged, metadata.st_uid, metadata.st_gid)
    os.chmod(staged, 0o644)
    assert digest(staged.read_bytes()) == expected
    assert target.read_bytes() == original, "Production changed during staging"
    os.replace(staged, target)
finally:
    staged.unlink(missing_ok=True)
assert digest(target.read_bytes()) == expected
print(f"backup={backup}\nprevious_sha256={previous}\ndeployed_sha256={expected}\ntarget={target}")
print("activation=pending game-process restart")
