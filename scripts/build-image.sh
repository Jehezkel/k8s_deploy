#!/usr/bin/env bash
set -euo pipefail
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
tag="${1:?Usage: bash scripts/build-image.sh TAG [moby|containerd]}"
engine="${2:-moby}"
case "$engine" in
  moby)
    docker --context rancher-desktop build -t "kds-target:$tag" \
      -f "$repo_root/src/KDS.TargetApp/Dockerfile" "$repo_root/src"
    ;;
  containerd)
    nerdctl --namespace k8s.io build -t "kds-target:$tag" \
      -f "$repo_root/src/KDS.TargetApp/Dockerfile" "$repo_root/src"
    ;;
  *) echo "Engine must be moby or containerd" >&2; exit 2 ;;
esac
