#!/usr/bin/env bash
set -euo pipefail

if [ -z "${SWEDES_DB_PASSWORD:-}" ]; then
  echo "Set SWEDES_DB_PASSWORD before running, for example:" >&2
  echo "  SWEDES_DB_PASSWORD='a-long-random-password' sudo -E $0" >&2
  exit 1
fi

if [ "${EUID:-$(id -u)}" -ne 0 ]; then
  echo "Run with sudo -E so SWEDES_DB_PASSWORD is preserved." >&2
  exit 1
fi

db_name="${SWEDES_DB_NAME:-swedesclantracker}"
db_user="${SWEDES_DB_USER:-swedes}"

escaped_password="${SWEDES_DB_PASSWORD//\'/\'\'}"
escaped_user="${db_user//\"/\"\"}"
escaped_db="${db_name//\"/\"\"}"

if sudo -u postgres psql -tAc "SELECT 1 FROM pg_roles WHERE rolname = '$db_user'" | grep -q 1; then
  sudo -u postgres psql -c "ALTER ROLE \"$escaped_user\" WITH LOGIN PASSWORD '$escaped_password';"
else
  sudo -u postgres psql -c "CREATE ROLE \"$escaped_user\" LOGIN PASSWORD '$escaped_password';"
fi

if ! sudo -u postgres psql -tAc "SELECT 1 FROM pg_database WHERE datname = '$db_name'" | grep -q 1; then
  sudo -u postgres createdb -O "$db_user" "$db_name"
fi

sudo -u postgres psql -d "$db_name" -c "ALTER DATABASE \"$escaped_db\" OWNER TO \"$escaped_user\";"

echo "PostgreSQL database ready: $db_name owned by $db_user"
