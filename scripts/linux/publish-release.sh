#!/usr/bin/env bash
set -euo pipefail

configuration="${CONFIGURATION:-Release}"
runtime="${RUNTIME:-linux-arm64}"
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
output_root="${OUTPUT_ROOT:-$repo_root/deploy/pi}"

api_project="$repo_root/SwedesClanTracker.Api/SwedesClanTracker.Api.csproj"
worker_project="$repo_root/SwedesClanTracker.Worker/SwedesClanTracker.Worker.csproj"
frontend_dir="$repo_root/swedesclantracker-frontend"

publish_project() {
  local project_path="$1"
  local output_path="$2"
  echo "Publishing $project_path -> $output_path"
  rm -rf "$output_path"
  mkdir -p "$output_path"
  dotnet publish "$project_path" \
    --configuration "$configuration" \
    --runtime "$runtime" \
    --self-contained false \
    --output "$output_path"
}

publish_project "$api_project" "$output_root/api"
publish_project "$worker_project" "$output_root/worker"

echo "Building frontend -> $output_root/frontend"
(
  cd "$frontend_dir"
  if [ -f package-lock.json ]; then
    npm ci
  else
    npm install
  fi
  npm run build
)
rm -rf "$output_root/frontend"
mkdir -p "$output_root/frontend"
cp -a "$frontend_dir/dist/." "$output_root/frontend/"

echo
echo "Publish complete:"
echo "  API:      $output_root/api"
echo "  Worker:   $output_root/worker"
echo "  Frontend: $output_root/frontend"
