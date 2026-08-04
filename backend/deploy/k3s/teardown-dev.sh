#!/usr/bin/env bash
# Reverse setup-dev.sh.
#
#   ./teardown-dev.sh              # fleets + rpg-* namespaces, Agones stays
#   ./teardown-dev.sh --all        # + uninstall Agones itself
#   ./teardown-dev.sh --fleets-only
#
# Idempotent: everything uses --ignore-not-found.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DEPLOY_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
# shellcheck source=lib.sh
source "$SCRIPT_DIR/lib.sh"

AGONES_VERSION="${AGONES_VERSION:-1.59.0}"
AGONES_INSTALL_URL="https://raw.githubusercontent.com/googleforgames/agones/release-${AGONES_VERSION}/install/yaml/install.yaml"

NAMESPACE="rpg-realtime"
REMOVE_AGONES=0
FLEETS_ONLY=0

while [[ $# -gt 0 ]]; do
  case "$1" in
    --all)         REMOVE_AGONES=1 ;;
    --fleets-only) FLEETS_ONLY=1 ;;
    -h|--help)     sed -n '2,10p' "$0"; exit 0 ;;
    *)             die "unknown flag: $1" ;;
  esac
  shift
done

resolve_kubectl
require_cluster

# ---------------------------------------------------------------------------
# 1. Fleets / autoscalers
# ---------------------------------------------------------------------------
# Delete autoscalers first: a live FleetAutoscaler will happily recreate
# replicas on a fleet that is on its way out.
log "deleting FleetAutoscalers in $NAMESPACE"
kube delete fleetautoscalers --all -n "$NAMESPACE" --ignore-not-found || true

log "deleting Fleets in $NAMESPACE"
kube delete fleets --all -n "$NAMESPACE" --ignore-not-found || true

# Agones garbage-collects the GameServers behind a deleted Fleet; sweep any
# strays (e.g. a hand-created GameServer).
kube delete gameservers --all -n "$NAMESPACE" --ignore-not-found || true

if (( FLEETS_ONLY )); then
  ok "fleets removed (namespaces and Agones left in place)"
  exit 0
fi

# ---------------------------------------------------------------------------
# 2. Config objects + namespaces
# ---------------------------------------------------------------------------
log "deleting dev Secret/ConfigMap"
kube delete secret rpg-realtime-secrets -n "$NAMESPACE" --ignore-not-found || true
kube delete configmap gameserver-config -n "$NAMESPACE" --ignore-not-found || true

log "deleting namespaces (rpg-realtime / rpg-meta / rpg-data)"
kube_delete_file "$SCRIPT_DIR/namespaces.yaml" || true

# ---------------------------------------------------------------------------
# 3. Agones
# ---------------------------------------------------------------------------
if (( REMOVE_AGONES )); then
  log "uninstalling Agones $AGONES_VERSION"
  # Namespaces must be gone first, otherwise the deleted validating webhook
  # leaves orphaned CRs that block namespace termination.
  kube delete -f "$AGONES_INSTALL_URL" --ignore-not-found || true
  ok "Agones removed"
else
  log "Agones left installed (pass --all to remove it)"
fi

ok "teardown complete"
