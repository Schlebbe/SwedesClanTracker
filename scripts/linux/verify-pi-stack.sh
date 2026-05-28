#!/usr/bin/env bash
set -euo pipefail

if [ "$#" -lt 1 ]; then
  echo "Usage: $0 <user@host> [ssh-key-path]" >&2
  exit 1
fi

target="$1"
ssh_key="${2:-}"

ssh_args=(-o BatchMode=yes -o ConnectTimeout=8)
if [ -n "$ssh_key" ]; then
  ssh_args+=(-i "$ssh_key")
fi

run_remote() {
  ssh "${ssh_args[@]}" "$target" "$1"
}

echo "== Host =="
run_remote "hostname && whoami"

echo
echo "== Required files =="
run_remote "ls -l /etc/swedesclantracker/api.env /etc/swedesclantracker/worker.env"

echo
echo "== Service states =="
run_remote "if sudo -n true 2>/dev/null; then \
  sudo systemctl is-enabled swedesclantracker-api swedesclantracker-worker nginx; \
  sudo systemctl is-active swedesclantracker-api swedesclantracker-worker nginx; \
else \
  systemctl is-enabled swedesclantracker-api swedesclantracker-worker nginx; \
  systemctl is-active swedesclantracker-api swedesclantracker-worker nginx; \
fi"

echo
echo "== API localhost probe =="
run_remote "code=\$(curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:5166/api/dashboard || true); echo \"api_status=\$code\""

echo
echo "Verification complete."
