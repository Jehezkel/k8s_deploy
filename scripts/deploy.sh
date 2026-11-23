#!/usr/bin/env bash
set -euo pipefail
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
tag="${1:?Usage: bash scripts/deploy.sh TAG [helm options...]}"
shift
chart="${CHART:-$repo_root/helm/target-app}"
context="${KUBE_CONTEXT:-rancher-desktop}"
namespace="${KUBE_NAMESPACE:-kds-test}"
release="${HELM_RELEASE:-demo}"
helm lint "$chart" --strict --set-string "image.tag=$tag"
printf 'Deploy started: %s\n' "$(date -u +%FT%TZ)"
helm upgrade --install "$release" "$chart" \
  --kube-context "$context" --namespace "$namespace" --create-namespace \
  --set-string "image.tag=$tag" --wait --timeout 5m "$@"
printf 'Deploy ready: %s\n' "$(date -u +%FT%TZ)"
