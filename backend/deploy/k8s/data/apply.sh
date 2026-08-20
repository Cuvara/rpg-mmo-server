#!/usr/bin/env bash
# Bring up the data tier (PostgreSQL meta + PostgreSQL gamestate + Redis) and
# Nakama in one namespace on a k3d cluster.
#
#   ./apply.sh                       # apply into rpg-k8s-data on k3d-rpg-dev
#   KUBE_CONTEXT=... NS=... ./apply.sh
#
# Why a script and not plain `kubectl apply -k .`: the gamestate initdb
# ConfigMap is generated from deploy/db/init-gamestate.sql, which lives above
# this directory, and kustomize refuses file sources above its root. Everything
# else is ordinary kustomize. See README.md §The initdb ConfigMap.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
KUBE_CONTEXT="${KUBE_CONTEXT:-k3d-rpg-dev}"
NS="${NS:-rpg-k8s-data}"
K="kubectl --context ${KUBE_CONTEXT}"

# ALWAYS pass a context. A `docker-desktop` context also exists on this box and
# it is a different cluster; defaulting to whatever is current is how the wrong
# one gets changed.
echo "==> context=${KUBE_CONTEXT} namespace=${NS}"

# BEFORE the StatefulSet, not after: the ConfigMap is the first-boot seed, and
# postgres runs /docker-entrypoint-initdb.d exactly once, at container start on
# an empty PGDATA. Creating it afterwards is a race that is usually won and
# silently lost on a slow apply, leaving an empty database with no error.
# Generated from the ORIGINAL SQL so it cannot drift from compose.
echo "==> gamestate-initdb ConfigMap (from ../../db/init-gamestate.sql)"
$K create namespace "${NS}" --dry-run=client -o yaml | $K apply -f -
$K -n "${NS}" create configmap gamestate-initdb \
  --from-file=10-init-gamestate.sql="${HERE}/../../db/init-gamestate.sql" \
  --dry-run=client -o yaml | $K -n "${NS}" apply -f -

$K apply -k "${HERE}"

echo "==> waiting for rollout"
$K -n "${NS}" rollout status statefulset/postgres-meta --timeout=180s
$K -n "${NS}" rollout status statefulset/postgres-game --timeout=180s
$K -n "${NS}" rollout status statefulset/redis         --timeout=180s
$K -n "${NS}" rollout status deployment/nakama         --timeout=300s

$K -n "${NS}" get pods,svc,pvc
