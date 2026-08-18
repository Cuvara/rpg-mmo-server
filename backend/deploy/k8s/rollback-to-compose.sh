#!/usr/bin/env bash
# Roll dev BACK off k3s/Agones onto the pre-cutover hybrid stack:
#   data tier + Nakama + gateway in docker compose, game servers in the
#   `rpg-realtime` Agones fleet reaching compose over host.k3d.internal.
#
# Takes a couple of minutes and destroys NOTHING: it stops in-cluster
# workloads by scaling them to zero and leaves every PVC, every compose volume
# and every compose container in place. Re-running dev-up.sh reverses it.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CTX="${KUBE_CONTEXT:-k3d-rpg-dev}"
K="kubectl --context $CTX"
RUN_DIR="${RPG_K8S_RUN_DIR:-/tmp/claude-1000/rpg-k8s-dev}"
COMPOSE_DEV_CONTAINERS="${COMPOSE_DEV_CONTAINERS:-rpg-postgres rpg-postgres-game rpg-redis rpg-nakama rpg-gateway}"
LEGACY_FLEET_NS="${LEGACY_FLEET_NS:-rpg-realtime}"
LEGACY_FLEET="${LEGACY_FLEET:-map-servers-dotnet-dev}"
K8S_FLEET_NS="${K8S_FLEET_NS:-rpg-k8s-realtime}"
K8S_FLEET="${K8S_FLEET:-map-servers-dotnet-k8s}"

say() { printf '\n== %s\n' "$*"; }

# Agones does NOT evict an ALLOCATED GameServer when its Fleet is scaled to 0 --
# allocated means "in use", so scaling alone leaves the pod running AND its
# registry entry live. Every scale-down here therefore deletes the GameServers
# explicitly. The delete is a graceful pod termination, so the server's SIGTERM
# path drains and DEREGISTERS itself rather than leaving an entry to expire on
# the 15s heartbeat TTL -- which is the window in which the gateway would still
# hand a client the address of a server that is gone (ADR-2).
drain_fleet() { # namespace fleet
  local ns="$1" fleet="$2"
  $K scale fleet "$fleet" -n "$ns" --replicas=0 >/dev/null
  $K delete gs -n "$ns" -l "agones.dev/fleet=$fleet" --ignore-not-found --timeout=120s >/dev/null 2>&1 || true
  for _ in $(seq 1 30); do
    [ "$($K get gs -n "$ns" -l "agones.dev/fleet=$fleet" --no-headers 2>/dev/null | wc -l)" = "0" ] && break
    sleep 2
  done
  local left
  left=$($K get gs -n "$ns" -l "agones.dev/fleet=$fleet" --no-headers 2>/dev/null | wc -l)
  echo "$ns/$fleet drained; GameServers remaining: $left"
  [ "$left" = "0" ]
}


# 1. Free the host ports FIRST. Both stacks want 8000/7350, and compose cannot
#    bind them while the port-forwards hold them.
say "stop port-forwards"
if [ -d "$RUN_DIR" ]; then
  for pidf in "$RUN_DIR"/*.pid; do
    [ -e "$pidf" ] || continue
    pid="$(cat "$pidf")"
    if kill -0 "$pid" 2>/dev/null; then echo "killing $(basename "$pidf" .pid) (pid $pid)"; kill "$pid" || true; fi
    rm -f "$pidf"
  done
fi

# 2. ADR-2 again, in the other direction. The k8s fleet must be gone from the
#    IN-CLUSTER registry before the compose fleet takes map_01 back, and its
#    entry survives the pod by the 15s heartbeat TTL.
say "retire the k8s fleet $K8S_FLEET_NS/$K8S_FLEET"
if $K get fleet "$K8S_FLEET" -n "$K8S_FLEET_NS" >/dev/null 2>&1; then
  drain_fleet "$K8S_FLEET_NS" "$K8S_FLEET" || \
    echo "WARNING: k8s fleet did not fully drain -- map_01 may still have a live registrant"
fi
if $K get pod redis-0 -n rpg-k8s-data >/dev/null 2>&1; then
  # --raw: one member per line, and nothing at all for an empty set. The
  # --no-raw form turned "(empty array)" into a member named `array)`.
  for id in $($K exec -n rpg-k8s-data redis-0 -- redis-cli --raw SMEMBERS 'servers:map:map_01' 2>/dev/null); do
    [ -n "$id" ] || continue
    echo "deregistering $id from the in-cluster registry"
    $K exec -n rpg-k8s-data redis-0 -- redis-cli DEL "servers:id:${id}" >/dev/null 2>&1 || true
    $K exec -n rpg-k8s-data redis-0 -- redis-cli SREM 'servers:map:map_01' "$id" >/dev/null 2>&1 || true
  done
fi

say "scale the in-cluster app+data tiers to zero (PVCs are KEPT)"
$K scale deploy/gateway -n rpg-k8s-realtime --replicas=0 2>/dev/null || true
$K scale deploy/nakama  -n rpg-k8s-data     --replicas=0 2>/dev/null || true
for s in postgres-meta postgres-game redis; do
  $K scale statefulset/"$s" -n rpg-k8s-data --replicas=0 2>/dev/null || true
done

# 3. Compose back up, in dependency order, then the legacy fleet re-registers
#    into it on startup.
say "start the compose dev stack"
# `docker start` is a Docker Desktop shim under WSL and intermittently returns
# non-zero with a vsock error AFTER starting the container. Under `set -e` that
# aborted the rollback BEFORE the legacy fleet was restored, leaving dev with
# neither stack serving map_01. Tolerate the status, assert on real state.
for c in $COMPOSE_DEV_CONTAINERS; do
  if docker ps -a --format '{{.Names}}' | grep -qx "$c"; then
    echo "starting $c"; docker start "$c" >/dev/null 2>&1 || true
  else
    echo "WARNING: container $c does not exist -- bring it up with docker compose instead" >&2
  fi
done
missing=""
for c in $COMPOSE_DEV_CONTAINERS; do
  docker ps --format '{{.Names}}' | grep -qx "$c" || missing="$missing $c"
done
if [ -n "$missing" ]; then
  echo "ERROR: compose dev containers did not start:$missing" >&2
  echo "Bring them up with: cd backend/deploy && docker compose up -d" >&2
  exit 1
fi
echo "compose dev stack running"

say "wait for compose Postgres"
for _ in $(seq 1 60); do
  st="$(docker inspect -f '{{.State.Health.Status}}' rpg-postgres-game 2>/dev/null || echo missing)"
  [ "$st" = healthy ] && break
  sleep 2
done
echo "rpg-postgres-game: ${st:-unknown}"

say "restore the legacy fleet $LEGACY_FLEET_NS/$LEGACY_FLEET"
if $K get fleet "$LEGACY_FLEET" -n "$LEGACY_FLEET_NS" >/dev/null 2>&1; then
  $K scale fleet "$LEGACY_FLEET" -n "$LEGACY_FLEET_NS" --replicas=1
  # Do not report the rollback complete until a server has actually REGISTERED
  # an address. The pod being Ready is not the same thing: registration happens
  # after startup, and for a few seconds the registry holds an entry with an
  # empty `addr`, which a client would be handed and could not dial.
  echo "waiting for a legacy server to register an address"
  for _ in $(seq 1 60); do
    addr=""
    for id in $(docker exec rpg-redis redis-cli --raw SMEMBERS 'servers:map:map_01' 2>/dev/null); do
      addr=$(docker exec rpg-redis redis-cli --raw HGET "servers:id:${id}" addr 2>/dev/null || true)
      [ -n "$addr" ] && break
    done
    if [ -n "$addr" ]; then echo "map_01 served at $addr"; break; fi
    sleep 2
  done
  [ -n "${addr:-}" ] || echo "WARNING: no server registered an address for map_01 within 120s" >&2
else
  echo "WARNING: legacy fleet absent; re-apply backend/deploy/agones/" >&2
fi

say "state"
docker ps --format '{{.Names}}\t{{.Status}}' | grep -E '^rpg-(gateway|nakama|redis|postgres)' || true
$K get fleet -A || true
echo
echo "dev is back on the compose stack. Verify with:"
echo "  cd $HERE/verify && JWT_SECRET=<jwt> ./verify.sh --target dev-agones"
