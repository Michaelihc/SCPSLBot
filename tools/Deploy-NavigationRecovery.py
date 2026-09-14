"""Deploy one verified SCPSLBot assembly on bot production 7777 without restarting it."""
import hashlib
import os
from pathlib import Path
import re
import socket
import sys
import time
import zipfile


def digest(data):
    return hashlib.sha256(data).hexdigest()


assert socket.gethostname() == "scpsl-warmup-hk", "Unexpected host"
source = Path(sys.argv[1]).resolve(strict=True)
expected = sys.argv[2].lower()
assert source.parent == Path("/tmp") and source.name.startswith("scpsl-nav-recovery-"), source
assert re.fullmatch(r"[a-f0-9]{64}", expected), "Expected SHA-256 required"
payload = source.read_bytes()
assert digest(payload) == expected, "Upload hash mismatch"
lab = Path("/home/scpsl/.config/SCP Secret Laboratory/LabAPI")
target = lab / "plugins/7777/SCPSLBot.dll"
original = target.read_bytes()
metadata = target.stat()
stamp = time.strftime("%Y%m%d-%H%M%S", time.gmtime())
backup = lab / "backups/7777" / ("navigation-recovery-" + stamp + ".zip.bak")
backup.parent.mkdir(parents=True, exist_ok=True)
entry = "plugins/7777/SCPSLBot.dll"
with zipfile.ZipFile(backup, "x", zipfile.ZIP_DEFLATED) as archive:
    archive.writestr(entry, original)
    archive.writestr("rollback.sha256", f"{digest(original)}  {entry}\n")
with zipfile.ZipFile(backup) as archive:
    assert archive.testzip() is None and archive.read(entry) == original, "Backup verification failed"
os.chown(backup, metadata.st_uid, metadata.st_gid)
os.chmod(backup, 0o640)
staged = target.with_name("SCPSLBot.dll.next-navigation-recovery-" + stamp)
try:
    with staged.open("xb") as output:
        output.write(payload)
        output.flush()
        os.fsync(output.fileno())
    os.chown(staged, metadata.st_uid, metadata.st_gid)
    os.chmod(staged, 0o644)
    assert digest(staged.read_bytes()) == expected
    assert target.read_bytes() == original, "Target changed during deployment"
    os.replace(staged, target)
finally:
    staged.unlink(missing_ok=True)
assert digest(target.read_bytes()) == expected, "Deployed hash mismatch"
print(f"backup={backup}")
print(f"previous_sha256={digest(original)}")
print(f"deployed_sha256={expected}")
print(f"target={target}")
print("activation=pending game-process restart")
