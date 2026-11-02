#!/usr/bin/env bash
# Installs a published SCPSLBot release on the bot server and queues its one-shot restart.
# Files are swapped while the game runs, which keeps the old build in memory until the armed
# restart fires. Create the snapshot with Create-BotProductionBackup.py first.
set -euo pipefail

lab_root=${BOT_LAB_ROOT:-'/home/scpsl/.config/SCP Secret Laboratory/LabAPI'}
server_root=${BOT_SERVER_ROOT:-/home/scpsl/scpsl-server}
package_root=${BOT_PACKAGE_ROOT:-/tmp}
backup_root="$lab_root/backups/7777"
expected_host='scpsl-warmup-hk'

# Release path -> live path. Harmony ships under dependencies/ but this host loads it from plugins.
release_files=(
  'plugins/SCPSLBot.dll:plugins/7777/SCPSLBot.dll'
  'plugins/SCPSLBot.Components.dll:plugins/7777/SCPSLBot.Components.dll'
  'plugins/HsmAdapter.dll:plugins/7777/HsmAdapter.dll'
  'dependencies/0Harmony.dll:plugins/7777/0Harmony.dll'
  'optional/plugins/BotRoundControl.dll:plugins/7777/BotRoundControl.dll'
  'optional/plugins/ServerKeybinds.dll:plugins/7777/ServerKeybinds.dll'
  'optional/plugins/WarmupSafezone.dll:plugins/7777/WarmupSafezone.dll'
  'optional/plugins/StatsBots.dll:plugins/7777/StatsBots.dll'
)

usage() {
  printf 'Usage: %s <SCPSLBot-release.zip> <zip-sha256> <snapshot.zip.bak> [--check]\n' "$0" >&2
  exit 64
}

fail() {
  printf '%s\n' "$1" >&2
  exit 1
}

test "$#" -ge 3 && test "$#" -le 4 || usage
package="$1"
expected_hash="$2"
snapshot="$3"
check_only='false'
if test "$#" -eq 4; then
  test "$4" = '--check' || usage
  check_only='true'
fi

test "$(hostname)" = "$expected_host" || fail "Refusing deployment on unexpected host $(hostname)."
[[ "$expected_hash" =~ ^[0-9a-f]{64}$ ]] || fail 'Expected package hash must be a lowercase SHA-256 digest.'

package_real=$(readlink -f -- "$package")
case "$package_real" in
  "$(readlink -f -- "$package_root")"/SCPSLBot-*.zip) ;;
  *) fail "Package must be an SCPSLBot-*.zip file directly under $package_root." ;;
esac
test -f "$package_real" || fail "Package not found: $package_real"
printf '%s  %s\n' "$expected_hash" "$package_real" | sha256sum -c -
unzip -tqq "$package_real"

while IFS= read -r entry; do
  case "$entry" in
    /*|*'..'*|*'\'*) fail "Unsafe package entry: $entry" ;;
  esac
done < <(unzip -Z1 "$package_real")

snapshot_real=$(readlink -f -- "$snapshot")
case "$snapshot_real" in
  "$(readlink -f -- "$backup_root")"/*.zip.bak) ;;
  *) fail "Snapshot must be a .zip.bak file under $backup_root." ;;
esac
test -f "$snapshot_real" || fail "Snapshot not found: $snapshot_real"
unzip -tqq "$snapshot_real"

stage=$(mktemp -d "${TMPDIR:-/tmp}/bot-prod-deploy.XXXXXX")
trap 'rm -rf -- "$stage"' EXIT
unzip -oq "$package_real" -d "$stage/release"
(
  cd "$stage/release"
  sha256sum -c --quiet SHA256SUMS.txt
)

# A snapshot only protects this deployment if it still describes the live files.
unzip -p "$snapshot_real" rollback.sha256 >"$stage/rollback.sha256"
(
  cd "$lab_root"
  sha256sum -c --quiet "$stage/rollback.sha256"
) || fail 'Live files no longer match the snapshot; create a fresh one.'

for mapping in "${release_files[@]}"; do
  live_path=${mapping#*:}
  test -f "$stage/release/${mapping%%:*}" || fail "Package is missing ${mapping%%:*}."
  grep -Fq "  $live_path" "$stage/rollback.sha256" || fail "Snapshot does not cover $live_path."
done

# Only Runtime navigation depends on the patched assets; a game update silently restores them.
backend=$(awk '/^[[:space:]]*#/ { next } /^navigation:/ { inside = 1; next } inside && /^[^ ]/ { inside = 0 } inside && $1 == "backend:" { print tolower($2) }' \
  "$lab_root/configs/7777/SCPSLBot/config.yml")
test -n "$backend" || fail 'Could not read navigation.backend from the SCPSLBot config.'
if test "$backend" = 'runtime' && ! test -f "$server_root/SCPSL_Data/navmesh-asset-patch.json"; then
  fail "Refusing deployment: navigation.backend is Runtime but $server_root has no asset patch manifest."
fi

restart_state=$(scpsl-ctl localadmin restartwhen status)
grep -Fq 'No restart intent is armed.' <<<"$restart_state" || {
  printf '%s\n' "$restart_state" >&2
  fail 'Another restart intent is already armed; resolve it before deploying.'
}

printf 'Backend: %s\n' "$backend"
scpsl-ctl console players | tail -n 1
if test "$check_only" = 'true'; then
  printf 'Check passed; nothing was changed.\n'
  exit 0
fi

deployment_id=$(date +%Y%m%d-%H%M%S)
cleanup_staged() {
  local mapping
  for mapping in "${release_files[@]}"; do
    rm -f -- "$lab_root/${mapping#*:}.next-$deployment_id"
  done
}
trap 'cleanup_staged; rm -rf -- "$stage"' EXIT

for mapping in "${release_files[@]}"; do
  source_path="$stage/release/${mapping%%:*}"
  staged_path="$lab_root/${mapping#*:}.next-$deployment_id"
  install -m 0644 "$source_path" "$staged_path"
  chown --reference="$lab_root/${mapping#*:}" "$staged_path"
  cmp -s "$source_path" "$staged_path" || fail "Staged copy differs for ${mapping#*:}."
done

for mapping in "${release_files[@]}"; do
  mv -f -- "$lab_root/${mapping#*:}.next-$deployment_id" "$lab_root/${mapping#*:}"
done
for mapping in "${release_files[@]}"; do
  cmp -s "$stage/release/${mapping%%:*}" "$lab_root/${mapping#*:}" || fail "Installed copy differs for ${mapping#*:}."
done

scpsl-ctl localadmin restartwhen empty-now
scpsl-ctl localadmin restartwhen status | grep -q 'empty-now' || fail 'Files are installed but the restart intent did not arm.'

printf 'Queued: files are installed and the restart fires once %s is empty.\n' "$expected_host"
printf 'The old build keeps running until then. Rollback snapshot: %s\n' "$snapshot_real"
