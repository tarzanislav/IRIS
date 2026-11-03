#!/usr/bin/env bash
# Removes the IRIS hub: sudo bash /opt/iris-hub/uninstall.sh [--purge]
#
# Removes the services, the program and the options. The data of the hub (/var/lib/iris-hub: model, keys, history)
# and its user stay, so that a new install goes on learning – unless --purge is given. The backup folder (e.g. in a
# Samba share) is never touched, and nothing else of the system either.
set -euo pipefail

say() { printf '%s\n' "$*"; }
[ "$(id -u)" -eq 0 ] || { echo "iris-hub uninstall: run it as root" >&2; exit 1; }
purge=no
[ "${1:-}" = "--purge" ] && purge=yes

port=47620
if [ -f /etc/default/iris-hub ]; then
  # shellcheck disable=SC1091
  . /etc/default/iris-hub
  found=$(printf '%s\n' "${IRIS_HUB_OPTIONS:-}" | sed -nE 's/.*--port[ =]+([0-9]+).*/\1/p')
  port=${found:-47620}
fi

for unit in iris-hub-update.path iris-hub-update.timer iris-hub-update.service iris-hub.service; do
  systemctl disable --now --quiet "$unit" 2>/dev/null || true
  rm -f "/etc/systemd/system/$unit"
done
rm -rf /etc/systemd/system/iris-hub.service.d
systemctl daemon-reload
say "services removed"

if command -v ufw >/dev/null && ufw status 2>/dev/null | grep -q "^$port/tcp .*IRIS hub"; then
  ufw delete allow "$port/tcp" >/dev/null || true
  say "ufw rule for $port/tcp removed"
fi

rm -f /etc/default/iris-hub
rm -rf /opt/iris-hub
say "program and options removed"

if [ "$purge" = yes ]; then
  rm -rf /var/lib/iris-hub
  if id iris-hub >/dev/null 2>&1; then
    userdel iris-hub 2>/dev/null || true
    getent group iris-hub >/dev/null && groupdel iris-hub 2>/dev/null || true
  fi
  say "data (/var/lib/iris-hub) and the user iris-hub removed"
else
  say "the data stays in /var/lib/iris-hub (model, keys, history); everything: uninstall.sh --purge"
fi
