#!/usr/bin/env bash
set -euo pipefail
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
run_dir="$(mktemp -d "${TMPDIR:-/tmp}/kds-k6.XXXXXX")"
mkdir -p "$repo_root/artifacts"
output_dir="$repo_root/artifacts/$(basename "$run_dir")"
mkdir -p "$output_dir"
rmdir "$run_dir"
printf 'Test output: %s\n' "$output_dir"
k6 run --out "json=$output_dir/metrics.json" \
  "$repo_root/tests/k6/availability.js" 2>&1 | tee "$output_dir/k6.log"
