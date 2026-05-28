#!/usr/bin/env bash
set -euo pipefail

if [ "${EUID:-$(id -u)}" -ne 0 ]; then
  echo "Run with sudo: sudo $0" >&2
  exit 1
fi

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
lan_cidr="${LAN_CIDR:-192.168.10.0/24}"

apt update
apt install -y nginx postgresql postgresql-contrib rsync jq

if ! id swedestracker >/dev/null 2>&1; then
  useradd --system --home /opt/swedesclantracker --shell /usr/sbin/nologin swedestracker
fi

mkdir -p /opt/swedesclantracker/api /opt/swedesclantracker/worker /opt/swedesclantracker/frontend /etc/swedesclantracker
chown -R swedestracker:swedestracker /opt/swedesclantracker
chmod 750 /etc/swedesclantracker

install -m 0644 "$repo_root/deploy/systemd/swedesclantracker-api.service" /etc/systemd/system/swedesclantracker-api.service
install -m 0644 "$repo_root/deploy/systemd/swedesclantracker-worker.service" /etc/systemd/system/swedesclantracker-worker.service
install -m 0644 "$repo_root/deploy/nginx/swedesclantracker.conf" /etc/nginx/sites-available/swedesclantracker.conf
ln -sfn /etc/nginx/sites-available/swedesclantracker.conf /etc/nginx/sites-enabled/swedesclantracker.conf
rm -f /etc/nginx/sites-enabled/default

if command -v ufw >/dev/null 2>&1; then
  ufw allow from "$lan_cidr" to any port 80 proto tcp comment 'Swedes dashboard from home LAN'
fi

systemctl daemon-reload
systemctl enable swedesclantracker-api swedesclantracker-worker nginx
nginx -t
systemctl reload nginx

echo "Pi service prerequisites installed. Create /etc/swedesclantracker/*.env before starting app services."
