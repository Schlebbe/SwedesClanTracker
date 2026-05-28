#!/usr/bin/env bash
set -euo pipefail

if [ "${EUID:-$(id -u)}" -ne 0 ]; then
  echo "Run with sudo: sudo $0" >&2
  exit 1
fi

db_name="${SWEDES_DB_NAME:-swedesclantracker}"
backup_dir="${BACKUP_DIR:-/var/backups/swedesclantracker}"
retention_days="${RETENTION_DAYS:-7}"

mkdir -p "$backup_dir"
chmod 750 "$backup_dir"

timestamp="$(date -u '+%Y%m%dT%H%M%SZ')"
backup_file="$backup_dir/${db_name}-${timestamp}.dump"

sudo -u postgres pg_dump -Fc "$db_name" > "$backup_file"
chmod 640 "$backup_file"

find "$backup_dir" -maxdepth 1 -type f -name "${db_name}-*.dump" -mtime +"$retention_days" -delete

echo "Backup created: $backup_file"
