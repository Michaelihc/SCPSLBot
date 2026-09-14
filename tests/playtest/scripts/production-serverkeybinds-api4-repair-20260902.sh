#!/usr/bin/env bash
set -Eeuo pipefail

service="scpsl-warmup.service"
target="/home/scpsl/.config/SCP Secret Laboratory/LabAPI/dependencies/7777/ServerKeybinds.dll"
stage="/tmp/serverkeybinds-api4-20260902/ServerKeybinds.dll"
stamp="$(date +%Y%m%d-%H%M%S)"
backup="/home/scpsl/.config/SCP Secret Laboratory/LabAPI/backups/7777/ServerKeybinds.pre-api4-$stamp.dll"
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
test "$(sha256sum "$stage" | awk '{print $1}')" = "3230b9e579c5e236920d39a14942d0beb93f9f6a36d51a7d496d88b46379ae28"
install -d -o scpsl -g scpsl -m 0750 "$(dirname "$backup")"
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
