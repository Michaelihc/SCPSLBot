#!/usr/bin/env bash
set -euo pipefail

lab_root='/home/scpsl/.config/SCP Secret Laboratory/LabAPI'
service_name='scpsl-warmup.service'
expected_host='scpsl-warmup-hk'
backup_root="$lab_root/backups/7777"

usage() {
  printf 'Usage: %s <package.zip> <package-sha256> <snapshot.zip.bak> <rollback-script>\n' "$0" >&2
  exit 64
}

test "$#" -eq 4 || usage
package="$1"
expected_package_hash="$2"
snapshot="$3"
rollback_script="$4"

test "$(hostname)" = "$expected_host" || {
  printf 'Refusing deployment on unexpected host %s.\n' "$(hostname)" >&2
  exit 1
}
[[ "$expected_package_hash" =~ ^[0-9a-f]{64}$ ]] || {
  printf 'Expected package hash must be a lowercase SHA-256 digest.\n' >&2
  exit 1
}

package_real=$(readlink -f -- "$package")
case "$package_real" in
  /tmp/bot-prod-*.zip) ;;
  *)
    printf 'Package must be a bot-prod-*.zip file directly under /tmp.\n' >&2
    exit 1
    ;;
esac
test -f "$package_real" || {
  printf 'Package not found: %s\n' "$package_real" >&2
  exit 1
}
printf '%s  %s\n' "$expected_package_hash" "$package_real" | sha256sum -c -
unzip -tqq "$package_real"

backup_real=$(readlink -f -- "$snapshot")
backup_root_real=$(readlink -f -- "$backup_root")
case "$backup_real" in
  "$backup_root_real"/*.zip.bak) ;;
  *)
    printf 'Snapshot must be a .zip.bak file under %s.\n' "$backup_root_real" >&2
    exit 1
    ;;
esac
test -f "$backup_real" && unzip -tqq "$backup_real"
test -x "$rollback_script" || {
  printf 'Rollback script is missing or not executable: %s\n' "$rollback_script" >&2
  exit 1
}
bash -n "$rollback_script"

archive_entries=$(unzip -Z1 "$package_real")
while IFS= read -r entry; do
  case "$entry" in
    /*|*'..'*|*'\\'*)
      printf 'Unsafe package entry: %s\n' "$entry" >&2
      exit 1
      ;;
  esac
done <<<"$archive_entries"

stage=$(mktemp -d /tmp/bot-prod-deploy.XXXXXX)
trap 'rm -rf -- "$stage"' EXIT
unzip -oq "$package_real" -d "$stage"
test -f "$stage/deploy.sha256" && test -f "$stage/manifest.txt"
grep -Fqx 'host=scpsl-warmup-hk' "$stage/manifest.txt"
grep -Fqx 'port=7777/udp' "$stage/manifest.txt"
(
  cd "$stage"
  sha256sum -c deploy.sha256
)

expected_entries=$(mktemp)
actual_entries=$(mktemp)
awk '{print $2}' "$stage/deploy.sha256" >"$expected_entries"
printf '%s\n' deploy.sha256 manifest.txt >>"$expected_entries"
find "$stage" -type f -printf '%P\n' | sort >"$actual_entries"
sort -o "$expected_entries" "$expected_entries"
diff -u "$expected_entries" "$actual_entries"
rm -f -- "$expected_entries" "$actual_entries"

snapshot_checksums=$(mktemp)
unzip -p "$backup_real" rollback.sha256 >"$snapshot_checksums"
(
  cd "$lab_root"
  sha256sum -c "$snapshot_checksums"
)
rm -f -- "$snapshot_checksums"

systemctl is-active --quiet "$service_name"
ss -lunp | grep -q ':7777[[:space:]]'
players_output=$(scpsl-ctl console players)
printf '%s\n' "$players_output"
player_count=$(sed -n 's/^List of players (\([0-9][0-9]*\)):.*/\1/p' <<<"$players_output" | head -n 1)
test "$player_count" = 0 || {
  printf 'Refusing deployment while %s player(s) are connected.\n' "$player_count" >&2
  exit 1
}

deployment_id=${package_real##*/}
deployment_id=${deployment_id%.zip}
declare -a staged_targets=()
while IFS= read -r relative_path; do
  source_path="$stage/$relative_path"
  target_path="$lab_root/$relative_path"
  staged_path="$target_path.next-$deployment_id"
  install -d -o scpsl -g scpsl -m 0755 "$(dirname "$target_path")"
  install -o scpsl -g scpsl -m 0644 "$source_path" "$staged_path"
  staged_targets+=("$relative_path")
done < <(awk '{print $2}' "$stage/deploy.sha256")

cleanup_staged_targets() {
  local relative_path
  for relative_path in "${staged_targets[@]}"; do
    rm -f -- "$lab_root/$relative_path.next-$deployment_id"
  done
}

players_output=$(scpsl-ctl console players)
player_count=$(sed -n 's/^List of players (\([0-9][0-9]*\)):.*/\1/p' <<<"$players_output" | head -n 1)
if test "$player_count" != 0; then
  cleanup_staged_targets
  printf 'A player connected during staging; deployment was not activated.\n' >&2
  exit 1
fi

systemctl stop "$service_name"
for relative_path in "${staged_targets[@]}"; do
  mv -f -- "$lab_root/$relative_path.next-$deployment_id" "$lab_root/$relative_path"
done
rm -f -- "$lab_root/plugins/7777/WarmupPlayerPanel.dll"

(
  cd "$lab_root"
  sha256sum -c "$stage/deploy.sha256"
)
test ! -e "$lab_root/plugins/7777/WarmupPlayerPanel.dll"

systemctl start "$service_name"
for _ in $(seq 1 75); do
  if systemctl is-active --quiet "$service_name" && ss -lunp | grep -q ':7777[[:space:]]'; then
    break
  fi
  sleep 2
done
systemctl is-active --quiet "$service_name"
ss -lunp | grep -q ':7777[[:space:]]'
# LabAPI normalizes YAML on load, so only immutable runtime artifacts retain byte-for-byte package
# hashes after startup. Configs are checked for existence, ownership, and non-empty content here and
# their critical parsed values are checked by the caller's post-deployment health gate.
awk '$2 !~ /^configs\// { print }' "$stage/deploy.sha256" >"$stage/runtime.sha256"
(
  cd "$lab_root"
  sha256sum -c "$stage/runtime.sha256"
)
while IFS= read -r relative_path; do
  config_path="$lab_root/$relative_path"
  test -s "$config_path"
  test "$(stat -c '%U:%G:%a' "$config_path")" = 'scpsl:scpsl:644'
done < <(awk '$2 ~ /^configs\// { print $2 }' "$stage/deploy.sha256")
test ! -e "$lab_root/plugins/7777/WarmupPlayerPanel.dll"
rm -f -- "$package_real"

printf 'Deployment activated on %s.\n' "$expected_host"
systemctl show "$service_name" -p MainPID -p NRestarts --no-pager
ss -lunp | grep ':7777[[:space:]]'
