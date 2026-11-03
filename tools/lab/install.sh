#!/usr/bin/env bash
# Installs the IRIS hub (the server of the classroom learning) on Debian or Ubuntu with systemd. Run as root from the
# folder with the files of the hub (iris-hub-linux-x64 or iris-hub-linux-arm64, the units, this script):
#
#   sudo bash install.sh [--backup DIR] [--port N] [--start FILE] [--fonts] [--options "…"]
#
#   --backup DIR    a second place for the model after every round (e.g. a folder of a Samba share); created when
#                   missing, otherwise the hub only gets the right to write into it (an ACL), nothing else changes
#   --port N        the port the computers and the console connect to (47620)
#   --start FILE    the model to begin with (an iris-ocr-train file), when the hub has none yet
#   --fonts         installs free fonts of Debian with which the hub measures how well the model reads
#   --options "…"   further options of "iris-hub run" (e.g. "--round 10 --from 15:00 --until 06:00")
#
# What it does: a system user iris-hub without login, the program in /opt/iris-hub, the data in /var/lib/iris-hub
# (model, keys, history), the options in /etc/default/iris-hub, the services iris-hub and the daily updater. Nothing
# else of the system is changed – no Samba, no other service; a firewall only when ufw is on (one rule for the port).
# Running it again updates the program and keeps the data. Removal: sudo bash /opt/iris-hub/uninstall.sh
set -euo pipefail

say() { printf '%s\n' "$*"; }
fail() { printf 'iris-hub install: %s\n' "$*" >&2; exit 1; }

[ "$(id -u)" -eq 0 ] || fail "run it as root (sudo bash install.sh)"
command -v systemctl >/dev/null || fail "systemd is needed"
here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)

backup="" port="" start="" fonts=no options=""
while [ $# -gt 0 ]; do
  case "$1" in
    --backup) backup=${2:?--backup needs a folder}; shift 2 ;;
    --port) port=${2:?--port needs a number}; shift 2 ;;
    --start) start=${2:?--start needs a file}; shift 2 ;;
    --fonts) fonts=yes; shift ;;
    --options) options=${2:?--options needs text}; shift 2 ;;
    *) fail "unknown option $1" ;;
  esac
done
[ -z "$port" ] || [[ "$port" =~ ^[0-9]+$ && "$port" -ge 1024 && "$port" -le 65535 ]] || fail "--port must be 1024–65535"

case "$(uname -m)" in
  x86_64) arch=x64 ;;
  aarch64 | arm64) arch=arm64 ;;
  *) fail "this processor ($(uname -m)) has no build of the hub" ;;
esac
program="$here/iris-hub-linux-$arch"
[ -f "$program" ] || program="$here/iris-hub"
[ -f "$program" ] || fail "iris-hub-linux-$arch is not beside this script"
for unit in iris-hub.service iris-hub-update.service iris-hub-update.timer iris-hub-update.path; do
  [ -f "$here/$unit" ] || fail "$unit is not beside this script"
done

say "IRIS hub: installing ($arch)"
if ! id iris-hub >/dev/null 2>&1; then
  useradd --system --user-group --home-dir /var/lib/iris-hub --no-create-home --shell /usr/sbin/nologin --comment "IRIS hub" iris-hub
  say "  user iris-hub (system, no login)"
fi

# the program (a running hub keeps its old file until it starts again)
install -d -m 0755 -o root -g root /opt/iris-hub
install -m 0755 -o root -g root "$program" /opt/iris-hub/iris-hub.new
mv -f /opt/iris-hub/iris-hub.new /opt/iris-hub/iris-hub
install -m 0755 -o root -g root "$here/uninstall.sh" /opt/iris-hub/uninstall.sh
/opt/iris-hub/iris-hub version >/dev/null || fail "the program does not start on this system"
say "  program: /opt/iris-hub/iris-hub ($(/opt/iris-hub/iris-hub version))"

# the data: model, keys, history – only the hub reads them
install -d -m 0750 -o iris-hub -g iris-hub /var/lib/iris-hub
if [ -n "$start" ] && [ ! -f /var/lib/iris-hub/lab-model.iris ]; then
  [ -f "$start" ] || fail "--start: $start does not exist"
  install -m 0640 -o iris-hub -g iris-hub "$start" /var/lib/iris-hub/lab-model.iris
  say "  starting model: $start"
fi
runuser -u iris-hub -- /opt/iris-hub/iris-hub keys --folder /var/lib/iris-hub >/dev/null
say "  keys: /var/lib/iris-hub/agent.key (computers), /var/lib/iris-hub/console.key (console)"

# the backup folder: created for the hub, or the hub gets the right to write into an existing one (ACL)
if [ -n "$backup" ]; then
  backup=$(realpath -m "$backup")
  if [ ! -d "$backup" ]; then
    install -d -m 0755 -o iris-hub -g iris-hub "$backup"
    say "  backup folder created: $backup"
  elif ! runuser -u iris-hub -- test -w "$backup"; then
    if command -v setfacl >/dev/null; then
      setfacl -m u:iris-hub:rwx "$backup"
      setfacl -d -m u:iris-hub:rwx "$backup"
      say "  backup folder: the hub may write into $backup (ACL; owner and other rights unchanged)"
    else
      say "  WARNING: the hub cannot write into $backup and setfacl is missing (apt install acl, then run this again)"
    fi
  fi
fi

# options: kept from an earlier install unless new ones are given
if [ ! -f /etc/default/iris-hub ] || [ -n "$backup$port$options" ]; then
  current=""
  [ -z "$backup" ] || current="$current --backup $backup"
  [ -z "$port" ] || current="$current --port $port"
  [ -z "$options" ] || current="$current $options"
  cat > /etc/default/iris-hub <<CONF
# Options of the IRIS hub ("iris-hub run"); after a change: sudo systemctl restart iris-hub
# e.g. --backup <folder> --port 47620 --round 10 --from 15:00 --until 06:00 --weekends yes --duty 0.5 --cores 0.5
#      --eco yes --wake yes --auto-update yes --sentences <folder> --fonts /usr/share/fonts
IRIS_HUB_OPTIONS="${current# }"
CONF
  chmod 0644 /etc/default/iris-hub
fi
# shellcheck disable=SC1091
. /etc/default/iris-hub
backup_now=$(printf '%s\n' "$IRIS_HUB_OPTIONS" | sed -nE 's/.*--backup[ =]+([^ ]+).*/\1/p')
port_now=$(printf '%s\n' "$IRIS_HUB_OPTIONS" | sed -nE 's/.*--port[ =]+([0-9]+).*/\1/p')
port_now=${port_now:-47620}

# the services: the hub, the daily updater and the updater the console can start
install -m 0644 "$here/iris-hub.service" "$here/iris-hub-update.service" "$here/iris-hub-update.timer" "$here/iris-hub-update.path" /etc/systemd/system/
rm -rf /etc/systemd/system/iris-hub.service.d
if [ -n "$backup_now" ]; then
  install -d -m 0755 /etc/systemd/system/iris-hub.service.d
  printf '[Service]\nReadWritePaths=%s\n' "$backup_now" > /etc/systemd/system/iris-hub.service.d/backup.conf
fi
systemctl daemon-reload
systemctl enable --quiet iris-hub.service iris-hub-update.timer iris-hub-update.path
systemctl restart iris-hub.service
systemctl start iris-hub-update.timer iris-hub-update.path

if [ "$fonts" = yes ]; then
  say "  fonts: installing free fonts of Debian"
  DEBIAN_FRONTEND=noninteractive apt-get install -y -q fonts-dejavu-core fonts-liberation2 fonts-crosextra-carlito fonts-crosextra-caladea >/dev/null
fi

# a firewall only when ufw is on: one rule for the port of the hub
if command -v ufw >/dev/null && ufw status 2>/dev/null | grep -q "^Status: active"; then
  ufw allow "$port_now/tcp" comment "IRIS hub" >/dev/null
  say "  ufw: port $port_now/tcp allowed"
fi

sleep 2
if systemctl is-active --quiet iris-hub.service; then
  say "IRIS hub runs on port $port_now. Log: journalctl -u iris-hub -f"
else
  systemctl status --no-pager iris-hub.service || true
  fail "the hub did not start (see above, or journalctl -u iris-hub)"
fi
say "Updates: once a day (systemctl list-timers iris-hub-update.timer), at once: sudo systemctl start iris-hub-update"
say "The keys: sudo cat /var/lib/iris-hub/agent.key – to the computers; console.key – only to the console."
