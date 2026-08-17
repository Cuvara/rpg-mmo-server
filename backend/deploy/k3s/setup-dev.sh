#!/usr/bin/env bash
# Bootstrap a local dev cluster with Agones + the C# map-server fleet.
#
#   ./setup-dev.sh                # agones + rpg-realtime + map-servers-dotnet-dev
#   ./setup-dev.sh --skip-agones  # fleet only (agones already installed)
#   ./setup-dev.sh --skip-image-check   # do not verify the image's git revision
#   AGONES_VERSION=1.58.0 ./setup-dev.sh
#
# Idempotent: safe to re-run. Every step is `kubectl apply`, so a second run is
# a no-op except for waits. This script installs NO cluster — despite living in
# a directory called k3s/, it only applies manifests into the current context.
#
# The --with-dungeon / --with-autoscaler / --prod-fleets flags are GONE, with
# the manifests behind them: the dungeon and prod fleets ran the Go game server
# deleted in 670a803, and the autoscalers targeted those fleets. See
# docs/K3S.md.
#
# Secrets come from ../.env, never from literals in this script — they must
# match the compose-run gateway's or every join fails signature verification.
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
MAP_FLEET_NAME="map-servers-dotnet-dev"
GAMESERVER_IMAGE="${GAMESERVER_IMAGE:-rpg-mmo/gameserver-dotnet:dev}"
SKIP_AGONES=0
SKIP_IMAGE_CHECK=0

while [[ $# -gt 0 ]]; do
  case "$1" in
    --skip-agones)      SKIP_AGONES=1 ;;
    --skip-image-check) SKIP_IMAGE_CHECK=1 ;;
    -h|--help)          sed -n '2,25p' "$0"; exit 0 ;;
    *)                  die "unknown flag: $1" ;;
  esac
  shift
done

# ---------------------------------------------------------------------------
# 0. Preflight
# ---------------------------------------------------------------------------
resolve_kubectl
log "using kubectl: $KUBECTL_BIN$(kubectl_is_exe && echo ' (windows binary — manifests piped via stdin)')"
require_cluster

# Which kind of cluster is this? Two things depend on the answer and both fail
# silently when it is guessed wrong: whether a local image tag resolves without
# an import, and what hostname a pod uses to reach the host's docker compose
# stack. Detect it from the context name rather than assuming.
KUBE_CONTEXT="$(kube config current-context)"
case "$KUBE_CONTEXT" in
  docker-desktop) CLUSTER_KIND="docker-desktop" ;;
  k3d-*)          CLUSTER_KIND="k3d" ;;
  *)              CLUSTER_KIND="unknown" ;;
esac
log "context: $KUBE_CONTEXT (kind: $CLUSTER_KIND)"

# ---------------------------------------------------------------------------
# 0b. The image is a claim. Verify it, then make sure the cluster can see it.
# ---------------------------------------------------------------------------
# A mutable :dev tag says nothing about its contents. The image in the store on
# 2026-08-17 was built 5.4 hours before the commit that added the real Agones
# SDK, so a fleet from it would have come up green running the no-op SDK — a
# green deploy proving nothing. Rebuild + verify + import is ONE sequence, not
# three optional steps.
if (( ! SKIP_IMAGE_CHECK )); then
  EXPECT_REV="$(git -C "$DEPLOY_DIR" rev-parse HEAD 2>/dev/null || true)"
  if [[ -n "$EXPECT_REV" ]]; then
    log "verifying $GAMESERVER_IMAGE was built from $EXPECT_REV"
    python3 "$SCRIPT_DIR/validate-manifests.py" \
      --check-image "$GAMESERVER_IMAGE" --expect-revision "$EXPECT_REV" \
      "$DEPLOY_DIR/agones/fleet-map-dotnet-dev.yaml" || die \
      "image check failed. Rebuild it from the branch under test:
   cd $DEPLOY_DIR/.. && docker build -f deploy/docker/Dockerfile.gameserver-dotnet \\
       --build-arg GIT_REVISION=\"\$(git rev-parse HEAD)\" \\
       -t $GAMESERVER_IMAGE .
   Re-run with --skip-image-check only if you know why the tag differs."
  fi
fi

# Getting the image INTO the cluster is a per-cluster-kind question, and the
# failure mode is identical either way: imagePullPolicy: IfNotPresent falls
# through to a registry pull that fails, and the GameServer sits in
# ImagePullBackOff.
case "$CLUSTER_KIND" in
  docker-desktop)
    log "docker-desktop shares the host image store — no import needed" ;;
  k3d)
    K3D_CLUSTER="${KUBE_CONTEXT#k3d-}"
    log "k3d has its OWN containerd store — importing $GAMESERVER_IMAGE into $K3D_CLUSTER"
    command -v k3d >/dev/null 2>&1 || die "k3d not on PATH but the context is $KUBE_CONTEXT"
    k3d image import "$GAMESERVER_IMAGE" -c "$K3D_CLUSTER" ;;
  *)
    log "WARNING: unknown cluster kind. If its nodes do not share the host's"
    log "         docker image store, import $GAMESERVER_IMAGE yourself, e.g."
    log "         docker save $GAMESERVER_IMAGE | sudo k3s ctr images import -" ;;
esac

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

# Secrets are SOURCED FROM ../.env, which is the same file the compose-run
# gateway reads. That is not a convenience — the gateway signs join tokens with
# JOIN_TOKEN_SECRET and the game server verifies them with the value from this
# Secret, so if the two drift apart every join fails signature verification and
# nothing logs the cause at the point the mistake was made. One file, both
# consumers, and re-running this script is how they are re-synchronised after
# any .env change.
if [[ -f "$DEPLOY_DIR/.env" ]]; then
  log "sourcing secrets from $DEPLOY_DIR/.env"
  set -a
  # shellcheck disable=SC1091
  source "$DEPLOY_DIR/.env"
  set +a
else
  log "WARNING: $DEPLOY_DIR/.env not found — falling back to the published dev"
  log "         placeholders. These only work if the gateway is also using them."
fi

log "applying dev Secret/ConfigMap in $NAMESPACE"
kube create secret generic rpg-realtime-secrets \
  --namespace "$NAMESPACE" \
  --from-literal=jwt-secret="${JWT_SECRET:-dev-secret-change-me}" \
  --from-literal=join-token-secret="${JOIN_TOKEN_SECRET:-dev-join-secret-change-me}" \
  --from-literal=redis-password="${REDIS_PASSWORD:-}" \
  --from-literal=transport-key="${TRANSPORT_KEY:-}" \
  --dry-run=client -o yaml | kube apply -f -

# REDIS_ADDR here is what the POD uses, and a pod is not on the compose network.
# .env's REDIS_ADDR is the value for HOST-run processes and is deliberately not
# reused. The host string is CLUSTER-SPECIFIC and not portable:
#   docker-desktop : host.docker.internal   (injected by Docker Desktop)
#   k3d            : host.k3d.internal      (injected by k3d)  — MEASURED from a
#                    pod: redis-cli -h host.k3d.internal -p 6379 ping -> PONG.
#                    host.docker.internal and the bridge address 172.17.0.1 also
#                    answer from a k3d pod, and neither is used here: the first
#                    is a Docker Desktop convention that happens to be inherited
#                    (so it quietly implies a Docker Desktop that may not be
#                    there), the second is an unstable bridge IP. host.k3d.internal
#                    is the one k3d itself injects.
#   anything else  : none of them exist. The data tier stays in docker compose on
#                    the host (ADR-15 decision 4 accepts this split for exactly
#                    this stage), so a cluster with no host alias needs a real
#                    routable address here.
# Override with POD_REDIS_ADDR=<host>:6379 and record the working value in
# docs/K3S.md rather than guessing twice.
case "$CLUSTER_KIND" in
  docker-desktop) DEFAULT_POD_REDIS="host.docker.internal:6379" ;;
  k3d)            DEFAULT_POD_REDIS="host.k3d.internal:6379" ;;
  *)              DEFAULT_POD_REDIS="" ;;
esac
POD_REDIS_ADDR="${POD_REDIS_ADDR:-$DEFAULT_POD_REDIS}"
[[ -n "$POD_REDIS_ADDR" ]] || die \
  "cannot guess how a pod on context '$KUBE_CONTEXT' reaches the compose-run redis;
   set POD_REDIS_ADDR=<host>:6379 explicitly"
log "pods will reach redis at $POD_REDIS_ADDR (cluster kind: $CLUSTER_KIND)"

# advertise-host: the HOST part of the address handed to the client, while the
# PORT still comes from the Agones status read (ADR-15 decision 2 option A).
#
# Why a host override is needed at all, and why only the host: on k3d, Agones'
# status.address is the node CONTAINER's Docker-network address (measured:
# 172.20.0.3). That is correct inside the cluster and NOT DIALABLE BY A CLIENT.
# The address that works is 127.0.0.1:<agones-assigned-port>, published by the
# k3d serverlb — measured reachable from both Windows (where the Unity client
# runs) and WSL2. So the port must keep coming from Agones (it is assigned per
# pod at scheduling time) and only the host is substituted. That is why this is
# not GAMESERVER_PUBLIC_ADDR, which replaces the whole address and cannot know
# the port.
#
# PLACEHOLDER — nothing reads this key yet. The game-server side is in flight
# and the env var name is not final (likely GAMESERVER_ADVERTISE_HOST); the key
# is created now so the fleet manifest only has to uncomment one env block.
# An unused ConfigMap key is inert.
case "$CLUSTER_KIND" in
  k3d)            DEFAULT_ADVERTISE_HOST="127.0.0.1" ;;
  # Docker Desktop does NOT publish Kubernetes hostPort to the host, so no host
  # string makes an Agones-assigned port reachable there. Empty on purpose:
  # there is no correct value, and inventing one would hide that.
  docker-desktop) DEFAULT_ADVERTISE_HOST="" ;;
  *)              DEFAULT_ADVERTISE_HOST="" ;;
esac
ADVERTISE_HOST="${GAMESERVER_ADVERTISE_HOST:-$DEFAULT_ADVERTISE_HOST}"
log "clients will be told to dial host '${ADVERTISE_HOST:-<unset — status.address as-is>}'"

kube create configmap gameserver-config \
  --namespace "$NAMESPACE" \
  --from-literal=redis-addr="$POD_REDIS_ADDR" \
  --from-literal=advertise-host="$ADVERTISE_HOST" \
  --dry-run=client -o yaml | kube apply -f -

# ---------------------------------------------------------------------------
# 4. Fleets
# ---------------------------------------------------------------------------
MAP_FLEET_FILE="$DEPLOY_DIR/agones/fleet-map-dotnet-dev.yaml"

# There is ONE fleet. No dungeon fleet (dungeon instancing is ADR-14 stage 6 and
# does not exist in the C# server) and NO AUTOSCALER — a buffer autoscaler on
# the map fleet would scale it above one replica, and every replica registers
# itself under the same map id, so it would manufacture the split-world hazard
# ADR-2 forbids. See docs/K3S.md, "Why there is no autoscaler".
log "applying fleet: $(basename "$MAP_FLEET_FILE")"
# One retry: the webhook can still be flapping right after install.
retry 6 10 kube_apply_file "$MAP_FLEET_FILE" || die "failed to apply $MAP_FLEET_FILE"

# ---------------------------------------------------------------------------
# 5. Wait for a Ready GameServer
# ---------------------------------------------------------------------------
# "Ready" means the pod started, the binary connected to the SDK sidecar and
# called sdk.Ready() — which the C# server does only now that HttpAgonesSdk
# exists (62131f5). It is the end-to-end proof the image + env are right, and it
# is what ADR-14 stage 4 is asking for.
#
# It does NOT prove a client can reach the server. Ready is reported over the
# sidecar from inside the pod; the client dials the node address and host port
# from outside. Those are different claims and only the first is checked here.
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
