#!/usr/bin/env bash
set -euo pipefail

lab_root='/home/scpsl/.config/SCP Secret Laboratory/LabAPI'
service_name='scpsl-warmup.service'
expected_host='scpsl-warmup-hk'
backup_root="$lab_root/backups/7777"
state_root_dropin='/etc/systemd/system/scpsl-warmup.service.d/ops-state-root.conf'

usage() {
  printf 'Usage: %s <snapshot.zip.bak> [--force-occupied]\n' "$0" >&2
  exit 64
}

test "$#" -ge 1 && test "$#" -le 2 || usage
archive="$1"
force_occupied='false'
if test "$#" -eq 2; then
  test "$2" = '--force-occupied' || usage
  force_occupied='true'
fi

test "$(hostname)" = "$expected_host" || {
  printf 'Refusing rollback on unexpected host %s.\n' "$(hostname)" >&2
  exit 1
}

archive_real=$(readlink -f -- "$archive")
backup_real=$(readlink -f -- "$backup_root")
case "$archive_real" in
  "$backup_real"/*.zip.bak) ;;
  *)
    printf 'Snapshot must be a .zip.bak file under %s.\n' "$backup_real" >&2
    exit 1
    ;;
esac

test -f "$archive_real" || {
  printf 'Snapshot not found: %s\n' "$archive_real" >&2
  exit 1
}
unzip -tqq "$archive_real"

required_entries=(
  'plugins/7777/SCPSLBot.dll'
  'plugins/7777/SCPSLBot.Components.dll'
  'plugins/7777/WarmupSafezone.dll'
  'plugins/7777/WarmupPlayerPanel.dll'
  'configs/7777/SCPSLBot/config.yml'
  'configs/7777/WarmupSafezone/config.yml'
  'rollback.sha256'
  'manifest.txt'
)
archive_entries=$(unzip -Z1 "$archive_real")
for entry in "${required_entries[@]}"; do
  grep -Fqx -- "$entry" <<<"$archive_entries" || {
    printf 'Snapshot is missing required entry: %s\n' "$entry" >&2
    exit 1
  }
done

restore_stage=$(mktemp -d)
trap 'rm -rf -- "$restore_stage"' EXIT
unzip -oq "$archive_real" -d "$restore_stage"
(
  cd "$restore_stage"
  sha256sum -c rollback.sha256
)

player_count=0
if systemctl is-active --quiet "$service_name"; then
  players_output=$(scpsl-ctl console players)
  player_count=$(sed -n 's/^List of players (\([0-9][0-9]*\)):.*/\1/p' <<<"$players_output" | head -n 1)
  test -n "$player_count" || {
    printf 'Could not establish the current player count.\n' >&2
    exit 1
  }
fi
if test "$player_count" -gt 0 && test "$force_occupied" != 'true'; then
  printf 'Refusing occupied rollback: %s player(s) are connected. Re-run with --force-occupied only for an emergency.\n' "$player_count" >&2
  exit 1
fi

systemctl stop "$service_name"

# The production change adds this exact lane-owned environment assignment. Player stats are mutable
# state, so rollback removes the service wiring but deliberately parks the state directory.
rm -f -- "$state_root_dropin"
rmdir --ignore-fail-on-non-empty -- "$(dirname "$state_root_dropin")" 2>/dev/null || true
systemctl daemon-reload

introduced_files=(
  'plugins/7777/HintServiceMeow.dll'
  'plugins/7777/StatsBots.dll'
  'plugins/7777/StatsSystem.dll'
  'dependencies/7777/Microsoft.Bcl.AsyncInterfaces.dll'
  'dependencies/7777/MySqlConnector.dll'
  'dependencies/7777/ServerKeybinds.dll'
  'dependencies/7777/System.Buffers.dll'
  'dependencies/7777/System.Diagnostics.DiagnosticSource.dll'
  'dependencies/7777/System.IO.Pipelines.dll'
  'dependencies/7777/System.Memory.dll'
  'dependencies/7777/System.Numerics.Vectors.dll'
  'dependencies/7777/System.Runtime.CompilerServices.Unsafe.dll'
  'dependencies/7777/System.Text.Encodings.Web.dll'
  'dependencies/7777/System.Text.Json.dll'
  'dependencies/7777/System.Threading.Tasks.Extensions.dll'
  'dependencies/7777/System.ValueTuple.dll'
  'configs/7777/StatsBots/config.yml'
  'configs/7777/StatsSystem/config.yml'
)
for relative_path in "${introduced_files[@]}"; do
  rm -f -- "$lab_root/$relative_path"
done

restored_files=(
  'plugins/7777/SCPSLBot.dll'
  'plugins/7777/SCPSLBot.Components.dll'
  'plugins/7777/WarmupSafezone.dll'
  'plugins/7777/WarmupPlayerPanel.dll'
  'configs/7777/SCPSLBot/config.yml'
  'configs/7777/WarmupSafezone/config.yml'
)
for relative_path in "${restored_files[@]}"; do
  install -d -o scpsl -g scpsl -m 0755 "$(dirname "$lab_root/$relative_path")"
  install -o scpsl -g scpsl -m 0644 "$restore_stage/$relative_path" "$lab_root/$relative_path"
  chown scpsl:scpsl "$lab_root/$relative_path"
  chmod 0644 "$lab_root/$relative_path"
done

(
  cd "$lab_root"
  sha256sum -c "$restore_stage/rollback.sha256"
)

systemctl start "$service_name"
for _ in $(seq 1 45); do
  if systemctl is-active --quiet "$service_name" && ss -lunp | grep -q ':7777[[:space:]]'; then
    printf 'Rollback restored and %s is active with UDP 7777 bound.\n' "$service_name"
    exit 0
  fi
  sleep 2
done

printf 'Rollback files were restored, but the service did not pass its 90-second health gate.\n' >&2
exit 1
