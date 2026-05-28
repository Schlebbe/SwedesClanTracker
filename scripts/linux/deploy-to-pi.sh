#!/usr/bin/env bash
set -euo pipefail

if [ "$#" -lt 1 ]; then
  echo "Usage: $0 <user@host> [ssh-key-path]" >&2
  exit 1
fi

target="$1"
ssh_key="${2:-}"
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
output_root="${OUTPUT_ROOT:-$repo_root/deploy/pi}"
remote_upload="/tmp/swedesclantracker-upload"

ssh_args=()
rsync_ssh="ssh"
if [ -n "$ssh_key" ]; then
  ssh_args=(-i "$ssh_key")
  rsync_ssh="ssh -i $ssh_key"
fi

for path in "$output_root/api" "$output_root/worker" "$output_root/frontend"; do
  if [ ! -d "$path" ]; then
    echo "Missing publish output: $path. Run scripts/linux/publish-release.sh first." >&2
    exit 1
  fi
done

ssh "${ssh_args[@]}" "$target" "rm -rf '$remote_upload' && mkdir -p '$remote_upload/api' '$remote_upload/worker' '$remote_upload/frontend'"
rsync -az --delete -e "$rsync_ssh" "$output_root/api/" "$target:$remote_upload/api/"
rsync -az --delete -e "$rsync_ssh" "$output_root/worker/" "$target:$remote_upload/worker/"
rsync -az --delete -e "$rsync_ssh" "$output_root/frontend/" "$target:$remote_upload/frontend/"

ssh "${ssh_args[@]}" "$target" "sudo systemctl stop swedesclantracker-api swedesclantracker-worker || true && \
  sudo mkdir -p /opt/swedesclantracker/api /opt/swedesclantracker/worker /opt/swedesclantracker/frontend && \
  sudo rsync -a --delete '$remote_upload/api/' /opt/swedesclantracker/api/ && \
  sudo rsync -a --delete '$remote_upload/worker/' /opt/swedesclantracker/worker/ && \
  sudo rsync -a --delete '$remote_upload/frontend/' /opt/swedesclantracker/frontend/ && \
  sudo chown -R swedestracker:swedestracker /opt/swedesclantracker && \
  sudo systemctl start swedesclantracker-api swedesclantracker-worker && \
  sudo systemctl reload nginx"

echo "Deployment copied to $target and services restarted."
