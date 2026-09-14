#!/usr/bin/env bash
set -Eeuo pipefail

service="scpsl-warmup.service"
target="/home/scpsl/.config/SCP Secret Laboratory/LabAPI/plugins/7777/StatsBots.dll"
stage="/tmp/statsbots-api4-20260902/StatsBots.dll"
stamp="$(date +%Y%m%d-%H%M%S)"
backup="/home/scpsl/.config/SCP Secret Laboratory/LabAPI/backups/7777/StatsBots.pre-api4-fix-$stamp.dll"
committed=0

rollback() {
  exit_code=$?
  if [[ $committed -eq 0 && -f "$backup" ]]; then
    install -o scpsl -g scpsl -m 0644 "$backup" "$target"
    systemctl start "$service" || true
  fi
  exit "$exit_code"
}
trap rollback ERR

test -f "$stage"
test "$(sha256sum "$stage" | awk '{print $1}')" = "aaffaaa5150370c53f03fb97ddb96eae3a28ebc3f9a5658e07d67cf089f758aa"
cp -a -- "$target" "$backup"
sha256sum "$backup" > "$backup.sha256"
chown scpsl:scpsl "$backup" "$backup.sha256"

systemctl stop "$service"
service_state="$(systemctl is-active "$service" || true)"
case "$service_state" in
  active|activating|deactivating)
    echo "service did not stop cleanly: $service_state" >&2
    false
    ;;
esac
if pgrep -u scpsl -f '/opt/scpsl-localadmin/current/LocalAdmin 7777' >/dev/null; then
  echo "production LocalAdmin process is still running after service stop" >&2
  false
fi

install -o scpsl -g scpsl -m 0644 "$stage" "$target"
systemctl start "$service"
committed=1
trap - ERR

echo "BACKUP=$backup"
echo "TARGET_SHA256=$(sha256sum "$target" | awk '{print $1}')"
systemctl show "$service" -p ActiveState -p SubState -p MainPID -p NRestarts
