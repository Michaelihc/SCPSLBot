#!/usr/bin/env bash
# Restores a Create-BotProductionBackup.py snapshot and queues the restart that loads it.
# --force-occupied restarts straight away and disconnects anyone playing; use it only for an emergency.
set -euo pipefail

lab_root=${BOT_LAB_ROOT:-'/home/scpsl/.config/SCP Secret Laboratory/LabAPI'}
backup_root="$lab_root/backups/7777"
expected_host='scpsl-warmup-hk'

usage() {
  printf 'Usage: %s <snapshot.zip.bak> [--check | --force-occupied]\n' "$0" >&2
  exit 64
}

fail() {
  printf '%s\n' "$1" >&2
  exit 1
}

test "$#" -ge 1 && test "$#" -le 2 || usage
snapshot="$1"
mode='queue'
if test "$#" -eq 2; then
  case "$2" in
    --check) mode='check' ;;
    --force-occupied) mode='force' ;;
    *) usage ;;
  esac
fi

test "$(hostname)" = "$expected_host" || fail "Refusing rollback on unexpected host $(hostname)."

snapshot_real=$(readlink -f -- "$snapshot")
case "$snapshot_real" in
  "$(readlink -f -- "$backup_root")"/*.zip.bak) ;;
  *) fail "Snapshot must be a .zip.bak file under $backup_root." ;;
esac
test -f "$snapshot_real" || fail "Snapshot not found: $snapshot_real"
unzip -tqq "$snapshot_real"

stage=$(mktemp -d "${TMPDIR:-/tmp}/bot-prod-rollback.XXXXXX")
trap 'rm -rf -- "$stage"' EXIT
unzip -oq "$snapshot_real" -d "$stage"
test -f "$stage/rollback.sha256" || fail 'Snapshot has no rollback.sha256.'
(
  cd "$stage"
  sha256sum -c --quiet rollback.sha256
)

mapfile -t restored_files < <(sed 's/^[0-9a-f]\{64\}  //' "$stage/rollback.sha256")
test "${#restored_files[@]}" -gt 0 || fail 'Snapshot lists no files.'
for relative_path in "${restored_files[@]}"; do
  case "$relative_path" in
    plugins/7777/*|configs/7777/*) ;;
    *) fail "Snapshot entry is outside this port: $relative_path" ;;
  esac
  case "$relative_path" in
    *'..'*) fail "Unsafe snapshot entry: $relative_path" ;;
  esac
done

printf 'Restoring %s file(s).\n' "${#restored_files[@]}"
scpsl-ctl console players | tail -n 1
if test "$mode" = 'check'; then
  printf 'Check passed; nothing was changed.\n'
  exit 0
fi

rollback_id=$(date +%Y%m%d-%H%M%S)
cleanup_staged() {
  local relative_path
  for relative_path in "${restored_files[@]}"; do
    rm -f -- "$lab_root/$relative_path.next-$rollback_id"
  done
}
trap 'cleanup_staged; rm -rf -- "$stage"' EXIT

for relative_path in "${restored_files[@]}"; do
  staged_path="$lab_root/$relative_path.next-$rollback_id"
  install -m 0644 "$stage/$relative_path" "$staged_path"
  chown --reference="$lab_root/$relative_path" "$staged_path"
done
for relative_path in "${restored_files[@]}"; do
  mv -f -- "$lab_root/$relative_path.next-$rollback_id" "$lab_root/$relative_path"
done
(
  cd "$lab_root"
  sha256sum -c --quiet "$stage/rollback.sha256"
)

if test "$mode" = 'force'; then
  scpsl-ctl console restart
  printf 'Restored; an immediate restart was requested.\n'
  exit 0
fi

# A deployment's pending restart loads the restored files just as well.
if ! scpsl-ctl localadmin restartwhen status | grep -q 'empty-now'; then
  scpsl-ctl localadmin restartwhen empty-now
fi
scpsl-ctl localadmin restartwhen status | grep -q 'empty-now' || fail 'Files are restored but the restart intent did not arm.'
printf 'Queued: files are restored and the restart fires once %s is empty.\n' "$expected_host"
