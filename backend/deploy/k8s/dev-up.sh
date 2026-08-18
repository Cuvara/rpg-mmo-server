#!/usr/bin/env bash
# Bring the dev environment up ENTIRELY on k3s/Agones.
#
# After this runs, nothing in dev depends on docker compose: the data tier,
# Nakama, the gateway and the map fleet are all in-cluster workloads talking to
# each other over cluster DNS. The compose dev stack is left STOPPED but intact
# (containers and volumes both) so rollback-to-compose.sh can bring it back.
#
# Idempotent: safe to re-run. Used by hand and by .github/workflows/cd.yml in
# DEPLOY_MODE=k8s.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CTX="${KUBE_CONTEXT:-k3d-rpg-dev}"
K="kubectl --context $CTX"
RUN_DIR="${RPG_K8S_RUN_DIR:-/tmp/claude-1000/rpg-k8s-dev}"
IMPORT_IMAGES="${IMPORT_IMAGES:-1}"
# Pin an IMMUTABLE tag by default. `:develop` is a moving tag that is retagged
# by hand and silently lags the branch -- at cutover time the cluster's
# `gateway:develop` was `develop-307f1e8` while develop was at b633aff, i.e.
# the deployment under test was not the commit under test. Resolving the tag
# from git makes the running image auditable against a commit.
GIT_SHA="${GIT_SHA:-$(git -C "$HERE" rev-parse HEAD 2>/dev/null || echo develop)}"
GATEWAY_IMAGE="${GATEWAY_IMAGE:-rpg-mmo/gateway:${GIT_SHA}}"
GAMESERVER_IMAGE="${GAMESERVER_IMAGE:-rpg-mmo/gameserver-dotnet:${GIT_SHA}}"
# The compose dev stack. Stopped, never removed: `docker start` is the rollback.
COMPOSE_DEV_CONTAINERS="${COMPOSE_DEV_CONTAINERS:-rpg-gateway rpg-nakama rpg-redis rpg-postgres rpg-postgres-game}"
# The pre-cutover Agones fleet, allocated from by the COMPOSE gateway.
LEGACY_FLEET_NS="${LEGACY_FLEET_NS:-rpg-realtime}"
LEGACY_FLEET="${LEGACY_FLEET:-map-servers-dotnet-dev}"
K8S_FLEET="${K8S_FLEET:-map-servers-dotnet-k8s}"
# Floor of the Agones dynamic port range. Everything BELOW it in k3d's
# published 7000-7100 is reserved for infrastructure (gateway 7000, nakama
# 7001). See app/40-gateway.yaml.
AGONES_MIN_PORT="${AGONES_MIN_PORT:-7010}"

mkdir -p "$RUN_DIR"
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


# A wrong context here reaches a different cluster with the same manifests.
say "context"
$K config current-context >/dev/null
echo "context: $CTX"

# ---------------------------------------------------------------- images
# k3d does NOT share the host docker image store, and `k3d image import` fails
# on this box (Docker Desktop named pipe) -- stream through ctr instead.
if [ "$IMPORT_IMAGES" = "1" ]; then
  say "import images into the k3d node"
  for img in "$GATEWAY_IMAGE" "$GAMESERVER_IMAGE"; do
    if docker image inspect "$img" >/dev/null 2>&1; then
      echo "importing $img"
      docker save "$img" | docker exec -i k3d-rpg-dev-server-0 ctr -n k8s.io images import -
    else
      echo "WARNING: $img not in the local docker store; the cluster keeps whatever it already has"
    fi
  done
fi

# ---------------------------------------------------------------- manifests
say "apply the data tier (rpg-k8s-data)"
$K apply -k "$HERE/data"

say "wait for the data tier"
$K rollout status -n rpg-k8s-data statefulset/postgres-meta --timeout=180s
$K rollout status -n rpg-k8s-data statefulset/postgres-game --timeout=180s
$K rollout status -n rpg-k8s-data statefulset/redis         --timeout=180s
$K rollout status -n rpg-k8s-data deploy/nakama             --timeout=300s

# Read the images the cluster is ALREADY running, before `apply` overwrites the
# specs with whatever tag the manifests carry. Comparing after the apply always
# reports a change -- the manifest tag is `:develop` and the deploy pins a
# commit -- so the fleet was drained and map_01 taken down on every run,
# including a no-op one.
pre_gs=$($K get fleet "$K8S_FLEET" -n rpg-k8s-realtime \
  -o jsonpath='{.spec.template.spec.template.spec.containers[0].image}' 2>/dev/null || true)

say "apply the app tier (rpg-k8s-realtime)"
# The Secret is NOT in the repo. It must already exist, or be applied from a
# filled copy of 30-secret-template.yaml kept outside the tree.
for f in 00-namespace.yaml 05-agones-sdk-rbac.yaml 10-rbac.yaml 20-configmaps.yaml; do
  [ -f "$HERE/app/$f" ] && $K apply -f "$HERE/app/$f"
done
if ! $K get secret rpg-app-secrets -n rpg-k8s-realtime >/dev/null 2>&1; then
  echo "ERROR: secret rpg-k8s-realtime/rpg-app-secrets is absent." >&2
  echo "Fill a copy of app/30-secret-template.yaml OUTSIDE the repo and apply it first." >&2
  exit 1
fi
$K apply -f "$HERE/app/40-gateway.yaml" -f "$HERE/app/50-fleet-map.yaml"

# Pin the resolved images over whatever the manifests carry. The Fleet is
# scaled to 0 across the image change on purpose: every replica registers the
# same GAMESERVER_MAP_ID at STARTUP, so a rolling update that briefly runs old
# and new together is two live servers for map_01 -- ADR-2's split world.
say "pin images ($GATEWAY_IMAGE / $GAMESERVER_IMAGE)"
$K set image -n rpg-k8s-realtime deploy/gateway gateway="$GATEWAY_IMAGE"
# Compare against the image a GameServer is actually RUNNING, not the Fleet
# spec: the `apply` above rewrites the spec back to whatever the manifest
# carries, so a spec comparison is unequal on every run and would drain and
# recreate the fleet -- taking map_01 down -- even when nothing changed.
if [ "$pre_gs" != "$GAMESERVER_IMAGE" ]; then
  echo "game server image change: ${pre_gs:-none} -> $GAMESERVER_IMAGE"
  drain_fleet rpg-k8s-realtime "$K8S_FLEET" || true
  $K patch fleet "$K8S_FLEET" -n rpg-k8s-realtime --type=json     -p "[{\"op\":\"replace\",\"path\":\"/spec/template/spec/template/spec/containers/0/image\",\"value\":\"$GAMESERVER_IMAGE\"}]"
  $K scale fleet "$K8S_FLEET" -n rpg-k8s-realtime --replicas=1
else
  # No drain needed, but `apply` just reset the spec to the manifest's moving
  # tag; put the pinned one back so the Fleet's spec matches what is running.
  $K patch fleet "$K8S_FLEET" -n rpg-k8s-realtime --type=json \
    -p "[{\"op\":\"replace\",\"path\":\"/spec/template/spec/template/spec/containers/0/image\",\"value\":\"$GAMESERVER_IMAGE\"}]" >/dev/null
  echo "game server image unchanged ($GAMESERVER_IMAGE); fleet left running"
fi

say "wait for the gateway"
$K rollout status -n rpg-k8s-realtime deploy/gateway --timeout=180s

# ------------------------------------------------- retire the compose stack
# ADR-2: one live game server per map_id. The legacy fleet registers into the
# COMPOSE Redis at startup, and its entry outlives the pod by the 15s heartbeat
# TTL -- during which the compose gateway would still hand a client a dead
# address. Scale to 0 first (SIGTERM makes the server deregister itself), then
# delete the entry rather than trusting the TTL.
if $K get fleet "$LEGACY_FLEET" -n "$LEGACY_FLEET_NS" >/dev/null 2>&1; then
  say "retire the legacy fleet $LEGACY_FLEET_NS/$LEGACY_FLEET"
  drain_fleet "$LEGACY_FLEET_NS" "$LEGACY_FLEET" || \
    echo "WARNING: legacy fleet did not fully drain -- check kubectl get gs -n $LEGACY_FLEET_NS"
fi

if docker ps --format '{{.Names}}' | grep -qx rpg-redis; then
  say "deregister leftovers from the compose registry"
  # --raw prints one member per line with no "1) " numbering and prints NOTHING
  # for an empty set. The --no-raw form turned "(empty array)" into a member
  # literally named `array)`, which was then "deregistered".
  for id in $(docker exec rpg-redis redis-cli --raw SMEMBERS 'servers:map:map_01' 2>/dev/null); do
    [ -n "$id" ] || continue
    echo "deregistering $id"
    docker exec rpg-redis redis-cli DEL "servers:id:${id}" >/dev/null 2>&1 || true
    docker exec rpg-redis redis-cli SREM 'servers:map:map_01' "$id" >/dev/null 2>&1 || true
  done
fi

say "stop the compose dev stack (containers and volumes are KEPT)"
# `docker stop` here is a Docker Desktop shim under WSL and intermittently
# returns non-zero with a vsock error AFTER stopping the container. Under
# `set -e` that aborted the script BEFORE the port-forwards, leaving dev with
# no reachable gateway and an exit status that still looked fine. Tolerate the
# status, then assert on the actual container state.
# One `docker ps` for the whole list, not one per container: under the Docker
# Desktop WSL shim each invocation is a Windows process launch costing seconds,
# and the naive loop spent minutes here.
running="$(docker ps --format '{{.Names}}' 2>/dev/null || true)"
to_stop=""
for c in $COMPOSE_DEV_CONTAINERS; do
  printf '%s\n' "$running" | grep -qx "$c" && to_stop="$to_stop $c"
done
if [ -n "$to_stop" ]; then
  echo "stopping:$to_stop"
  # shellcheck disable=SC2086
  docker stop $to_stop >/dev/null 2>&1 || true
fi
running="$(docker ps --format '{{.Names}}' 2>/dev/null || true)"
still_up=""
for c in $COMPOSE_DEV_CONTAINERS; do
  printf '%s\n' "$running" | grep -qx "$c" && still_up="$still_up $c"
done
if [ -n "$still_up" ]; then
  echo "ERROR: compose dev containers still running:$still_up" >&2
  echo "They hold the host ports the port-forwards below need, and rpg-gateway" >&2
  echo "would be a second gateway on the dev path. Stop them and re-run." >&2
  exit 1
fi
echo "compose dev stack stopped (containers and volumes kept)"

# ---------------------------------------------------------------- exposure
# The gateway and Nakama are reached on REAL published ports, not port-forwards:
# 40-gateway.yaml and data/nakama.yaml carry hostPort 7000 / 7001, which k3d's
# serverlb publishes onto the host because 7000-7100 is a mapped range. The
# Agones controller is pinned to MIN_PORT=7010 so its allocator can never take
# those two. Nothing here needs to start or supervise anything for the CLIENT
# path, which is the point -- a port-forward is a developer's terminal, not a
# deployment.
# Reserve 7000-7009 for infrastructure by pinning the Agones allocator's floor.
# ESTABLISHED here, not merely asserted: this is a property of the deployment,
# so a fresh cluster (and therefore CD) must end up with it without a human
# having run a kubectl command first. It is idempotent.
say "reserve the infrastructure ports (Agones MIN_PORT=7010)"
cur_min=$($K get deploy agones-controller -n agones-system \
  -o jsonpath='{range .spec.template.spec.containers[0].env[*]}{.name}={.value}{"\n"}{end}' 2>/dev/null \
  | sed -n 's/^MIN_PORT=//p')
if [ "$cur_min" != "$AGONES_MIN_PORT" ]; then
  echo "MIN_PORT is ${cur_min:-unset}, setting $AGONES_MIN_PORT"
  # A GameServer already holding a port below the new floor keeps it; the floor
  # only constrains future allocations, so this does not disturb a live fleet.
  $K set env deploy/agones-controller -n agones-system "MIN_PORT=$AGONES_MIN_PORT"
  $K rollout status deploy/agones-controller -n agones-system --timeout=180s
else
  echo "MIN_PORT already $AGONES_MIN_PORT"
fi
for probe in "gateway 7000" "nakama 7001"; do
  set -- $probe
  for i in $(seq 1 30); do
    if timeout 2 bash -c "exec 3<>/dev/tcp/127.0.0.1/$2" 2>/dev/null; then break; fi
    sleep 1
  done
done
# NOTE: a bare TCP connect is NOT proof here -- the k3d serverlb accepts on
# every mapped port whether or not anything is behind it. Nakama's /healthcheck
# is an application-level answer, so it is what gets asserted.
if ! curl -fsS --max-time 5 http://127.0.0.1:7001/healthcheck >/dev/null 2>&1; then
  echo "ERROR: Nakama does not answer /healthcheck on the published port 7001." >&2
  exit 1
fi
echo "nakama http answers on 127.0.0.1:7001"
echo "gateway published on 127.0.0.1:7000"

# The ONLY forward that remains, and it is not part of the deployment: the
# verification suite's persistence assertions run on THIS host and need a route
# to postgres-game, which is a headless ClusterIP by design. No client uses it.
say "test-runner-only port-forward (postgres-game)"
mkdir -p "$RUN_DIR"
pf() { # name localport namespace target targetport
  local name="$1" lport="$2" ns="$3" target="$4" tport="$5"
  local pidf="$RUN_DIR/$name.pid" pid
  if [ -f "$pidf" ] && kill -0 "$(cat "$pidf")" 2>/dev/null \
     && timeout 2 bash -c "exec 3<>/dev/tcp/127.0.0.1/$lport" 2>/dev/null; then
    echo "$name already forwarded (pid $(cat "$pidf")) on :$lport"; return 0
  fi
  # A pidfile whose process is alive but whose socket is NOT listening is the
  # trap: the forward looks established and nothing is bound. Kill and redo.
  [ -f "$pidf" ] && { kill "$(cat "$pidf")" 2>/dev/null || true; rm -f "$pidf"; }
  nohup $K port-forward --address 0.0.0.0 -n "$ns" "$target" "$lport:$tport" \
    >"$RUN_DIR/$name.log" 2>&1 &
  pid=$!
  echo "$pid" > "$pidf"
  local i
  for i in $(seq 1 30); do
    if ! kill -0 "$pid" 2>/dev/null; then
      echo "ERROR: port-forward $name died immediately:" >&2
      sed 's/^/    /' "$RUN_DIR/$name.log" >&2
      rm -f "$pidf"; return 1
    fi
    if timeout 2 bash -c "exec 3<>/dev/tcp/127.0.0.1/$lport" 2>/dev/null; then
      echo "$name -> 0.0.0.0:$lport (pid $pid, listening after ${i}s)"; return 0
    fi
    sleep 1
  done
  echo "ERROR: port-forward $name never accepted a connection on :$lport" >&2
  sed 's/^/    /' "$RUN_DIR/$name.log" >&2
  return 1
}
pf postgres-game 15433 rpg-k8s-data svc/postgres-game 5432

say "state"
$K get pods -n rpg-k8s-data -n rpg-k8s-data 2>/dev/null || true
$K get pods -A | grep -E 'rpg-k8s|rpg-realtime' || true
echo
echo "dev is on k3s/Agones. Verify with:"
echo "  cd $HERE/verify && JWT_SECRET=<jwt> ./verify.sh --target k8s-dev"
