#!/usr/bin/env bash
# connect-loki.sh - let the cluster's log collector reach Loki, without opening a port.
#
# Loki runs inside the compose `lgtm` container on the host and is published only
# on 127.0.0.1 (docs/MONITORING.md). A pod cannot reach the host's loopback, and
# that bind must not be widened. This script instead
#
#   1. connects the `lgtm` container to the cluster's Docker network
#      (`docker network connect`) when it is not connected yet,
#   2. reads the container's address ON THAT NETWORK, and
#   3. writes it into the EndpointSlice `loki-lgtm` of the selector-less Service
#      `loki` (60-loki-service.yaml) in namespace rpg-k8s-logging.
#
# Alloy pushes to http://loki.rpg-k8s-logging.svc.cluster.local:3100.
#
# What it does NOT do: publish a port, change a port binding, start or stop any
# container, read or print a container's Config or Env. The only `docker inspect`
# it runs has a --format over .NetworkSettings.Networks and prints one IPv4
# address; there is no plain `docker inspect` anywhere in this file.
#
# Residual risk, stated plainly: Loki stays unauthenticated, and once attached
# every container on the cluster's Docker network (every pod of that cluster
# included) can reach it. See docs/MONITORING.md.
#
# Idempotent: run it as often as you like. Re-run it whenever `lgtm` is recreated
# (a recreated container loses the attachment and gets a new address).
#
# When `lgtm` is absent or not running it prints a WARN on stderr, DELETES the
# EndpointSlice (so nothing points at a stale address) and exits 0: logs are not
# in the client path, so this never fails a deploy. It exits 1 only when the
# cluster API refuses the EndpointSlice it wanted to write.
#
# Docker under WSL is a slow shim that can return non-zero after succeeding, so
# nothing here asserts on a docker exit status: it asserts on state (is the
# container listed, does it have an address on the network now).
#
# Inputs (environment):
#   KUBE_CONTEXT         default k3d-rpg-dev
#   DEPLOY_ENVIRONMENT   environments.tsv row (dev, staging, ...); when unset it is
#                        derived: k3d-rpg-dev -> dev, k3d-rpg-stg -> staging
#   LOKI_CONTAINER       default <name_prefix>-lgtm, name_prefix from that row
#   LOKI_NETWORK         default the context name for a k3d-* context
#                        (k3d-<cluster>); any other context must set it
# Any other context with the variables unset: WARN, exit 0, nothing attached.
#
# Usage: connect-loki.sh [--dry-run]
#   --dry-run  read what is needed, print what it would do, change nothing

set -u -o pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SELF="connect-loki.sh"
ENVIRONMENTS_TSV="${ENVIRONMENTS_TSV:-$HERE/../../environments.tsv}"
NAMESPACE="rpg-k8s-logging"
SLICE="loki-lgtm"
SERVICE="loki"
LOKI_PORT_IN_CLUSTER=3100

DRY_RUN=0
for arg in "$@"; do
  case "$arg" in
    --dry-run) DRY_RUN=1 ;;
    -h|--help) sed -n '2,/^set -u/p' "$0" | sed '$d' | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "$SELF: unknown argument: $arg" >&2; exit 2 ;;
  esac
done

KUBE_CONTEXT="${KUBE_CONTEXT:-k3d-rpg-dev}"
RERUN="KUBE_CONTEXT=$KUBE_CONTEXT backend/deploy/k8s/logging/$SELF"

warn() { echo "WARN $SELF: $*" >&2; }
say()  { if [ "$DRY_RUN" -eq 1 ]; then echo "[dry-run] $*"; else echo "$SELF: $*"; fi; }

kube() { kubectl --context "$KUBE_CONTEXT" "$@"; }

# ---- resolve the inputs ------------------------------------------------------

env_name="${DEPLOY_ENVIRONMENT:-}"
if [ -z "$env_name" ]; then
  case "$KUBE_CONTEXT" in
    k3d-rpg-dev) env_name="dev" ;;
    k3d-rpg-stg) env_name="staging" ;;
  esac
fi

network="${LOKI_NETWORK:-}"
if [ -z "$network" ]; then
  case "$KUBE_CONTEXT" in
    k3d-*) network="$KUBE_CONTEXT" ;;
  esac
fi

container="${LOKI_CONTAINER:-}"
if [ -z "$container" ] && [ -n "$env_name" ] && [ -r "$ENVIRONMENTS_TSV" ]; then
  prefix="$(awk -F'\t' -v e="$env_name" '$0 !~ /^#/ && $1 == e { print $4; exit }' "$ENVIRONMENTS_TSV" | tr -d '\r')"
  if [ -n "$prefix" ]; then
    container="${prefix}-lgtm"
  fi
fi

if [ -z "$network" ] || [ -z "$container" ]; then
  warn "cannot tell which Docker network or Loki container belongs to context '$KUBE_CONTEXT'"
  warn "(environment '${env_name:-unknown}'); nothing attached, logs will not reach Loki."
  warn "Set LOKI_NETWORK and LOKI_CONTAINER (or DEPLOY_ENVIRONMENT) and re-run: $RERUN"
  exit 0
fi

# These end up in a Go template and in docker arguments: refuse anything odd.
case "$network$container" in
  *[!A-Za-z0-9_.-]*)
    warn "network '$network' or container '$container' has characters outside [A-Za-z0-9_.-]; nothing attached."
    exit 0 ;;
esac

# ---- helpers (state, not exit status) -----------------------------------------

lgtm_running() {
  local names
  names="$(docker ps --format '{{.Names}}' 2>/dev/null || true)"
  printf '%s\n' "$names" | grep -Fqx -- "$container"
}

# The container's IPv4 address on $network, or empty. NETWORK SETTINGS ONLY.
lgtm_address() {
  local out
  out="$(docker inspect \
    --format '{{with index .NetworkSettings.Networks "'"$network"'"}}{{.IPAddress}}{{end}}' \
    "$container" 2>/dev/null || true)"
  out="$(printf '%s' "$out" | tr -d '[:space:]')"
  if printf '%s' "$out" | grep -Eq '^[0-9]{1,3}(\.[0-9]{1,3}){3}$'; then
    printf '%s' "$out"
  fi
}

remove_slice() {
  if [ "$DRY_RUN" -eq 1 ]; then
    say "would delete EndpointSlice $NAMESPACE/$SLICE (no stale address is left behind)"
    return 0
  fi
  if ! kube -n "$NAMESPACE" delete endpointslice "$SLICE" --ignore-not-found >/dev/null 2>&1; then
    warn "could not delete EndpointSlice $NAMESPACE/$SLICE (is context '$KUBE_CONTEXT' reachable?); a stale address may remain."
  else
    say "no EndpointSlice $NAMESPACE/$SLICE is left"
  fi
}

slice_manifest() { # $1 address
  cat <<YAML
apiVersion: discovery.k8s.io/v1
kind: EndpointSlice
metadata:
  name: $SLICE
  namespace: $NAMESPACE
  labels:
    kubernetes.io/service-name: $SERVICE
    app.kubernetes.io/part-of: rpg-mmo
    app.kubernetes.io/managed-by: $SELF
addressType: IPv4
ports:
  - name: http
    protocol: TCP
    port: $LOKI_PORT_IN_CLUSTER
endpoints:
  - addresses:
      - "$1"
    conditions:
      ready: true
YAML
}

# ---- main ---------------------------------------------------------------------

if ! lgtm_running; then
  warn "Loki container '$container' is not running; the cluster collector cannot reach Loki."
  warn "Start the monitoring stack, then re-run: $RERUN"
  remove_slice
  exit 0
fi

addr="$(lgtm_address)"
if [ -z "$addr" ]; then
  if [ "$DRY_RUN" -eq 1 ]; then
    say "would run: docker network connect $network $container (publishes no port)"
  else
    say "connecting $container to Docker network $network"
    docker network connect "$network" "$container" >/dev/null 2>&1 || true
    addr="$(lgtm_address)"
  fi
  if [ -z "$addr" ] && [ "$DRY_RUN" -eq 0 ]; then
    warn "'$container' has no address on network '$network' after the attach (does the network exist? is the cluster up?)."
    warn "Re-run when it is: $RERUN"
    remove_slice
    exit 0
  fi
else
  say "$container is already on network $network ($addr)"
fi

if [ "$DRY_RUN" -eq 1 ]; then
  say "would apply this EndpointSlice (address read after the attach if it is not known yet):"
  slice_manifest "${addr:-<address of $container on $network>}" | sed 's/^/[dry-run]   /'
  exit 0
fi

if ! slice_manifest "$addr" | kube apply -f - >/dev/null; then
  echo "ERROR $SELF: kubectl apply of EndpointSlice $NAMESPACE/$SLICE failed (does namespace $NAMESPACE exist? apply k8s/logging first)." >&2
  exit 1
fi
say "EndpointSlice $NAMESPACE/$SLICE -> $addr:$LOKI_PORT_IN_CLUSTER (Service $SERVICE)"
exit 0
