#!/usr/bin/env bash
set -Eeuo pipefail

service="scpsl-warmup.service"
labapi_root="/home/scpsl/.config/SCP Secret Laboratory/LabAPI"
rating_data="$labapi_root/configs/global/RatingTags/rating-tags.json"
rating_config="$labapi_root/configs/7777/RatingTags/config.yml"
xp_plugin="$labapi_root/plugins/7777/XPSystem.dll"
xp_dependency="$labapi_root/dependencies/7777/Newtonsoft.Json.dll"
xp_config_dir="$labapi_root/configs/7777/XPSystem"
xp_data="$xp_config_dir/playerdata.json"
stage="/tmp/xpsystem-migration-20260902"
stamp="$(date +%Y%m%d-%H%M%S)"
backup_root="$labapi_root/backups/7777/xpsystem-migration-$stamp"
archive="$labapi_root/backups/7777/xpsystem-migration-$stamp.tar.gz"
committed=0

rollback() {
  exit_code=$?
  if [[ $committed -eq 0 ]]; then
    if [[ -f "$stage/rating-config.pre.yml" ]]; then
      install -o scpsl -g scpsl -m 0644 "$stage/rating-config.pre.yml" "$rating_config"
    fi
    if [[ -f "$stage/XPSystem.pre.dll" ]]; then
      install -o scpsl -g scpsl -m 0644 "$stage/XPSystem.pre.dll" "$xp_plugin"
    else
      rm -f -- "$xp_plugin"
    fi
    if [[ -f "$stage/Newtonsoft.Json.pre.dll" ]]; then
      install -o scpsl -g scpsl -m 0644 "$stage/Newtonsoft.Json.pre.dll" "$xp_dependency"
    else
      rm -f -- "$xp_dependency"
    fi
    if [[ -f "$stage/playerdata.pre.json" ]]; then
      install -o scpsl -g scpsl -m 0600 "$stage/playerdata.pre.json" "$xp_data"
    else
      rm -f -- "$xp_data"
    fi
    systemctl start "$service" || true
  fi
  exit "$exit_code"
}
trap rollback ERR

test -f "$rating_data"
test -f "$rating_config"
test -f "$stage/XPSystem.dll"
test -f "$stage/Newtonsoft.Json.dll"
test -f "$stage/migrate_rating_tags.py"

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

install -d -o scpsl -g scpsl -m 0750 "$backup_root"
cp -a -- "$rating_data" "$backup_root/rating-tags.json"
cp -a -- "$rating_config" "$backup_root/rating-tags-config.yml"
cp -a -- "$rating_config" "$stage/rating-config.pre.yml"
[[ ! -f "$xp_plugin" ]] || cp -a -- "$xp_plugin" "$stage/XPSystem.pre.dll"
[[ ! -f "$xp_dependency" ]] || cp -a -- "$xp_dependency" "$stage/Newtonsoft.Json.pre.dll"
[[ ! -f "$xp_data" ]] || cp -a -- "$xp_data" "$stage/playerdata.pre.json"

python3 "$stage/migrate_rating_tags.py" \
  "$rating_data" \
  "$stage/playerdata.json" \
  --rating-base-xp 100 \
  --rating-growth 1.16 \
  --xp-curve-base 35 \
  --xp-curve-root-scale 15 \
  > "$stage/migration-report.json"

python3 - "$rating_config" <<'PY'
import re
import sys

path = sys.argv[1]
with open(path, "r", encoding="utf-8") as handle:
    lines = handle.readlines()

section = None
changes = {"progression.enabled": 0, "tags.custom_info_format": 0,
           "hud.format": 0, "hud.progress_bar_enabled": 0}
for index, line in enumerate(lines):
    top = re.match(r"^([A-Za-z_][A-Za-z0-9_]*):(?:\s|$)", line)
    if top:
        section = top.group(1)
        continue
    if section == "progression" and re.match(r"^  enabled:\s*true\s*$", line):
        lines[index] = "  enabled: false\n"
        changes["progression.enabled"] += 1
    elif section == "tags" and re.match(r"^  custom_info_format:", line):
        lines[index] = "  custom_info_format: R{rating} | {tier}\n"
        changes["tags.custom_info_format"] += 1
    elif section == "hud" and re.match(r"^  format:", line):
        lines[index] = "  format: R{rating} | {tier}\n"
        changes["hud.format"] += 1
    elif section == "hud" and re.match(r"^  progress_bar_enabled:\s*true\s*$", line):
        lines[index] = "  progress_bar_enabled: false\n"
        changes["hud.progress_bar_enabled"] += 1

if any(value != 1 for value in changes.values()):
    raise SystemExit("unexpected RatingTags config shape: " + repr(changes))

temporary = path + ".xpsystem.tmp"
with open(temporary, "w", encoding="utf-8", newline="") as handle:
    handle.writelines(lines)
    handle.flush()
    import os
    os.fsync(handle.fileno())
os.replace(temporary, path)
PY

install -d -o scpsl -g scpsl -m 0755 "$(dirname "$xp_plugin")"
install -d -o scpsl -g scpsl -m 0755 "$(dirname "$xp_dependency")"
install -d -o scpsl -g scpsl -m 0750 "$xp_config_dir"
install -o scpsl -g scpsl -m 0644 "$stage/XPSystem.dll" "$xp_plugin"
install -o scpsl -g scpsl -m 0644 "$stage/Newtonsoft.Json.dll" "$xp_dependency"
install -o scpsl -g scpsl -m 0600 "$stage/playerdata.json" "$xp_data"
chown scpsl:scpsl "$rating_config"
chmod 0644 "$rating_config"

cp -a -- "$stage/playerdata.json" "$backup_root/playerdata.converted.json"
cp -a -- "$stage/migration-report.json" "$backup_root/migration-report.json"
sha256sum "$backup_root"/* > "$backup_root/manifest.sha256"
tar -C "$(dirname "$backup_root")" -czf "$archive" "$(basename "$backup_root")"
sha256sum "$archive" > "$archive.sha256"
chown -R scpsl:scpsl "$backup_root" "$archive" "$archive.sha256"

systemctl start "$service"
committed=1
trap - ERR

echo "BACKUP_ARCHIVE=$archive"
echo "BACKUP_SHA256=$(sha256sum "$archive" | awk '{print $1}')"
echo "XP_DLL_SHA256=$(sha256sum "$xp_plugin" | awk '{print $1}')"
echo "XP_DATA_SHA256=$(sha256sum "$xp_data" | awk '{print $1}')"
echo "MIGRATION_REPORT=$(tr -d '\n' < "$stage/migration-report.json")"
systemctl show "$service" -p ActiveState -p SubState -p MainPID -p NRestarts
