#!/usr/bin/env bash
# Tag and push all RPG MMO images to a container registry.
#
# Dev/staging use k3d import (no registry needed). This script is for
# environments with a real registry: CI/CD pushing to GHCR, or a self-hosted
# registry in front of a multi-node cluster.
#
# Usage:
#   ./push.sh                                   # defaults: ghcr.io/cuvara, tag=latest
#   REGISTRY=ghcr.io/cuvara TAG=v1.2.3 ./push.sh
#   REGISTRY=registry.example.com/rpg TAG=$(git rev-parse --short HEAD) ./push.sh
#   ./push.sh --dry-run                         # print commands without executing
#
# Prerequisites:
#   - Images must be built locally first (scripts/build-all.sh or CI build step)
#   - Authenticated to the registry: `docker login $REGISTRY` or
#     `echo $TOKEN | docker login ghcr.io -u USERNAME --password-stdin`
#
# What this does NOT do:
#   - Build images. That is build-all.sh's job.
#   - Create imagePullSecrets. See README.md §Container Registry.
#   - Update manifests. Image tags in manifests are deployment-time overrides
#     (kustomize image transformer or sed in CD), not committed changes.
set -euo pipefail

REGISTRY="${REGISTRY:-ghcr.io/cuvara}"
TAG="${TAG:-latest}"
DRY_RUN=false

for arg in "$@"; do
  case "$arg" in
    --dry-run) DRY_RUN=true ;;
    --help|-h)
      echo "Usage: REGISTRY=... TAG=... $0 [--dry-run]"
      exit 0
      ;;
  esac
done

# The three images this project builds. Local names match what
# Dockerfile.gateway, Dockerfile.gameserver-dotnet and Dockerfile.nakama
# (or nakama-plugin.Dockerfile) produce.
IMAGES=(
  "rpg-mmo/gateway"
  "rpg-mmo/gameserver-dotnet"
  "rpg-mmo/nakama"
)

run() {
  echo "  $*"
  if [ "$DRY_RUN" = false ]; then
    "$@"
  fi
}

echo "==> registry=${REGISTRY} tag=${TAG} dry_run=${DRY_RUN}"
echo ""

for local_image in "${IMAGES[@]}"; do
  # Strip the local prefix to get the short name: rpg-mmo/gateway -> gateway
  short="${local_image#rpg-mmo/}"
  remote="${REGISTRY}/${short}:${TAG}"

  echo "--- ${local_image} -> ${remote}"

  # Verify the local image exists before attempting to tag.
  if [ "$DRY_RUN" = false ]; then
    if ! docker image inspect "${local_image}:develop" >/dev/null 2>&1 && \
       ! docker image inspect "${local_image}:${TAG}" >/dev/null 2>&1; then
      echo "  SKIP: local image ${local_image}:{develop,${TAG}} not found"
      echo ""
      continue
    fi
  fi

  # Tag from the local develop tag (what build-all.sh produces) or from the
  # requested tag if it already exists locally.
  local_tag="develop"
  if [ "$DRY_RUN" = false ] && ! docker image inspect "${local_image}:${local_tag}" >/dev/null 2>&1; then
    local_tag="${TAG}"
  fi

  run docker tag "${local_image}:${local_tag}" "${remote}"
  run docker push "${remote}"
  echo ""
done

echo "==> done. To create an imagePullSecret for this registry:"
echo "    kubectl -n <namespace> create secret docker-registry registry-creds \\"
echo "      --docker-server=${REGISTRY} \\"
echo "      --docker-username=<user> \\"
echo "      --docker-password=<token>"
