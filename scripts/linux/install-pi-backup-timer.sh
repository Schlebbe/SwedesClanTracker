#!/usr/bin/env bash
set -euo pipefail

if [ "${EUID:-$(id -u)}" -ne 0 ]; then
  echo "Run with sudo: sudo $0" >&2
  exit 1
fi

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
source_script="$repo_root/scripts/linux/run-pi-backup.sh"
target_script="/usr/local/sbin/swedesclantracker-db-backup"
service_file="/etc/systemd/system/swedesclantracker-db-backup.service"
timer_file="/etc/systemd/system/swedesclantracker-db-backup.timer"

if [ ! -f "$source_script" ]; then
  echo "Missing backup script: $source_script" >&2
  exit 1
fi

install -m 0750 "$source_script" "$target_script"

cat > "$service_file" <<'EOF'
[Unit]
Description=Swedes Clan Tracker PostgreSQL backup

[Service]
Type=oneshot
Environment=BACKUP_DIR=/var/backups/swedesclantracker
Environment=RETENTION_DAYS=7
ExecStart=/usr/local/sbin/swedesclantracker-db-backup
EOF

cat > "$timer_file" <<'EOF'
[Unit]
Description=Daily Swedes Clan Tracker PostgreSQL backup

[Timer]
OnCalendar=*-*-* 03:30:00
Persistent=true

[Install]
WantedBy=timers.target
EOF

systemctl daemon-reload
systemctl enable --now swedesclantracker-db-backup.timer

echo "Backup timer installed and started."
systemctl --no-pager --full status swedesclantracker-db-backup.timer | sed -n '1,10p'
