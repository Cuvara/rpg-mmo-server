#!/usr/bin/env bash
# Bootstrap a local dev cluster with Agones + the rpg-mmo realtime fleets.
#
#   ./setup-dev.sh                     # agones + rpg-realtime + map fleet (dev)
#   ./setup-dev.sh --with-dungeon      # + dungeon fleet (replicas 0)
#   ./setup-dev.sh --with-autoscaler   # + dev FleetAutoscaler
#   ./setup-dev.sh --prod-fleets       # apply the ghcr.io fleets instead
#   ./setup-dev.sh --skip-agones       # fleets only (agones already installed)
#   AGONES_VERSION=1.58.0 ./setup-dev.sh
#
# Idempotent: safe to re-run. Every step is `kubectl apply`, so a second run is
# a no-op except for waits.
#
# Requires a reachable cluster. On this WSL2 box that means Docker Desktop's
# Kubernetes must be enabled once by hand — see docs/K3S.md.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DEPLOY_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
# shellcheck source=lib.sh
source "$SCRIPT_DIR/lib.sh"

# Pinned to match the SDK in backend/gameserver/go.mod (agones.dev/agones).
# The sidecar and the SDK are version-tolerant but keeping them equal removes a
# whole class of "works on my cluster" bugs.
AGONES_VERSION="${AGONES_VERSION:-1.59.0}"
AGONES_INSTALL_URL="https://raw.githubusercontent.com/googleforgames/agones/release-${AGONES_VERSION}/install/yaml/install.yaml"

NAMESPACE="rpg-realtime"
WITH_DUNGEON=0
WITH_AUTOSCALER=0
PROD_FLEETS=0
SKIP_AGONES=0

while [[ $# -gt 0 ]]; do
  case "$1" in
    --with-dungeon)    WITH_DUNGEON=1 ;;
    --with-autoscaler) WITH_AUTOSCALER=1 ;;
    --prod-fleets)     PROD_FLEETS=1 ;;
    --skip-agones)     SKIP_AGONES=1 ;;
    -h|--help)         sed -n '2,20p' "$0"; exit 0 ;;
    *)                 die "unknown flag: $1" ;;
  esac
  shift
done

# ---------------------------------------------------------------------------
# 0. Preflight
# ---------------------------------------------------------------------------
resolve_kubectl
log "using kubectl: $KUBECTL_BIN$(kubectl_is_exe && echo ' (windows binary — manifests piped via stdin)')"
require_cluster

# ---------------------------------------------------------------------------
# 1. Agones
# ---------------------------------------------------------------------------
if (( SKIP_AGONES )); then
  log "skipping Agones install (--skip-agones)"
else
  log "installing Agones $AGONES_VERSION"
  # --server-side: the CRDs blow past the 262kB last-applied-configuration
  # annotation limit that client-side apply uses.
  # The pinned install.yaml does not reliably create the namespace before the
# namespaced resources hit the API server — create it first (idempotent).
kube get namespace agones-system >/dev/null 2>&1 || kube create namespace agones-system
kube apply --server-side --force-conflicts -f "$AGONES_INSTALL_URL"

  wait_for 300 "agones-system deployments Available" \
    kube wait --for=condition=Available --timeout=10s deployment --all -n agones-system
fi

# The mutating/validating webhook backing agones.dev/v1 comes up a beat after
# the deployment reports Available. Applying a Fleet too early fails with
# "no endpoints available for service agones-controller-service", so poll the
# API surface itself instead of sleeping a magic number of seconds.
wait_for 180 "agones.dev/v1 API to serve Fleets" \
  kube get fleets.agones.dev -A

# ---------------------------------------------------------------------------
# 2. Namespaces
# ---------------------------------------------------------------------------
log "applying namespaces (rpg-realtime / rpg-meta / rpg-data)"
kube_apply_file "$SCRIPT_DIR/namespaces.yaml"

# ---------------------------------------------------------------------------
# 3. Dev config objects
# ---------------------------------------------------------------------------
# The dev fleets carry literal env, but the PROD fleets read these (optional)
# refs — create them so --prod-fleets works on a local cluster too.
# Agones only pre-creates the sidecar service account in the `default`
# namespace. Any other namespace hosting GameServers needs its own agones-sdk
# SA + rolebinding, or pods are rejected with "serviceaccount agones-sdk not
# found" and the fleet churns Error-state gameservers forever.
log "ensuring agones-sdk service account in $NAMESPACE"
kube get serviceaccount agones-sdk -n "$NAMESPACE" >/dev/null 2>&1 ||
  kube create serviceaccount agones-sdk -n "$NAMESPACE"
kube get rolebinding agones-sdk-access -n "$NAMESPACE" >/dev/null 2>&1 ||
  kube create rolebinding agones-sdk-access --clusterrole=agones-sdk \
    --serviceaccount="$NAMESPACE:agones-sdk" -n "$NAMESPACE"

log "applying dev Secret/ConfigMap in $NAMESPACE"
kube create secret generic rpg-realtime-secrets \
  --namespace "$NAMESPACE" \
  --from-literal=jwt-secret="${JWT_SECRET:-dev-secret-change-me}" \
  --from-literal=join-token-secret="${JOIN_TOKEN_SECRET:-dev-join-secret-change-me}" \
  --dry-run=client -o yaml | kube apply -f -

kube create configmap gameserver-config \
  --namespace "$NAMESPACE" \
  --from-literal=redis-addr="${REDIS_ADDR:-host.docker.internal:6379}" \
  --from-literal=game-db-url="${GAME_DB_URL:-}" \
  --dry-run=client -o yaml | kube apply -f -

# ---------------------------------------------------------------------------
# 4. Fleets
# ---------------------------------------------------------------------------
if (( PROD_FLEETS )); then
  MAP_FLEET_FILE="$DEPLOY_DIR/agones/fleet-map.yaml"
  DUNGEON_FLEET_FILE="$DEPLOY_DIR/agones/fleet-dungeon.yaml"
  AUTOSCALER_FILE="$DEPLOY_DIR/agones/autoscaler.yaml"
  MAP_FLEET_NAME="map-servers"
else
  MAP_FLEET_FILE="$DEPLOY_DIR/agones/fleet-map-dev.yaml"
  DUNGEON_FLEET_FILE="$DEPLOY_DIR/agones/fleet-dungeon-dev.yaml"
  AUTOSCALER_FILE="$DEPLOY_DIR/agones/autoscaler-dev.yaml"
  MAP_FLEET_NAME="map-servers-dev"
  log "dev fleets use the local image rpg-mmo/gameserver:dev (imagePullPolicy: IfNotPresent)"
  log "build it with: cd $DEPLOY_DIR && docker build -f docker/Dockerfile.gameserver -t rpg-mmo/gameserver:dev .."
fi

log "applying fleet: $(basename "$MAP_FLEET_FILE")"
# One retry: the webhook can still be flapping right after install.
retry 6 10 kube_apply_file "$MAP_FLEET_FILE" || die "failed to apply $MAP_FLEET_FILE"

if (( WITH_DUNGEON )); then
  log "applying fleet: $(basename "$DUNGEON_FLEET_FILE")"
  kube_apply_file "$DUNGEON_FLEET_FILE"
fi

if (( WITH_AUTOSCALER )); then
  log "applying autoscaler: $(basename "$AUTOSCALER_FILE")"
  kube_apply_file "$AUTOSCALER_FILE"
fi

# ---------------------------------------------------------------------------
# 5. Wait for a Ready GameServer
# ---------------------------------------------------------------------------
# "Ready" means the pod started, the binary connected to the SDK sidecar and
# called sdk.Ready(). That is the end-to-end proof the image + args are right.
gameserver_ready() {
  local states
  states="$(kube get gameservers -n "$NAMESPACE" \
    -l "agones.dev/fleet=$MAP_FLEET_NAME" \
    -o jsonpath='{.items[*].status.state}' 2>/dev/null || true)"
  [[ "$states" == *Ready* ]]
}

wait_for 300 "a Ready GameServer in fleet $MAP_FLEET_NAME" gameserver_ready

# ---------------------------------------------------------------------------
# 6. Status
# ---------------------------------------------------------------------------
echo
log "agones-system"
kube get pods -n agones-system
echo
log "fleets"
kube get fleets -n "$NAMESPACE"
echo
log "gameservers"
kube get gameservers -n "$NAMESPACE" -o wide
echo
ok "dev cluster ready"
cat <<EOF

Next:
  # connect to a game server from the host (Dynamic port):
  $KUBECTL_BIN get gameservers -n $NAMESPACE \\
    -o jsonpath='{.items[0].status.address}:{.items[0].status.ports[0].port}{"\\n"}'

  # allocate a dungeon instance:
  $KUBECTL_BIN create -f $DEPLOY_DIR/agones/allocation-dev.yaml

  # logs (gameserver container, not the SDK sidecar):
  $KUBECTL_BIN logs -n $NAMESPACE -l agones.dev/fleet=$MAP_FLEET_NAME -c gameserver

  # tear it all down:
  $SCRIPT_DIR/teardown-dev.sh
EOF
