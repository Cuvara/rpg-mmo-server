# Container Registry

## Current State

**Dev and staging use k3d image import** -- no registry is involved. Images are
built locally (or in CI) and piped into the k3d node:

```bash
docker save rpg-mmo/gateway:develop | \
  docker exec -i k3d-rpg-dev-server-0 ctr -n k8s.io images import -
```

This works because k3d does not share the host's Docker image store. Without
the import, `imagePullPolicy: IfNotPresent` falls through to a registry pull of
a tag that exists only on the host, and the pod sits in `ErrImagePull`.

## Moving to a Real Registry

For multi-node clusters or any environment where k3d import is not available,
images must be pushed to a container registry. The manifests already use
`imagePullPolicy: IfNotPresent`, which pulls from a registry when the image is
not present on the node.

### 1. Push images

```bash
# Authenticate (GHCR example)
echo "$GHCR_TOKEN" | docker login ghcr.io -u "$GITHUB_USER" --password-stdin

# Push all three images
REGISTRY=ghcr.io/cuvara TAG=v1.0.0 ./push.sh

# Or dry-run first
REGISTRY=ghcr.io/cuvara TAG=v1.0.0 ./push.sh --dry-run
```

### 2. Create imagePullSecret

Each namespace that runs pods needs a pull secret if the registry is private:

```bash
# rpg-k8s-data (nakama)
kubectl -n rpg-k8s-data create secret docker-registry registry-creds \
  --docker-server=ghcr.io \
  --docker-username="$GITHUB_USER" \
  --docker-password="$GHCR_TOKEN"

# rpg-k8s-realtime (gateway, fleet)
kubectl -n rpg-k8s-realtime create secret docker-registry registry-creds \
  --docker-server=ghcr.io \
  --docker-username="$GITHUB_USER" \
  --docker-password="$GHCR_TOKEN"
```

### 3. Reference in manifests

The gateway and nakama Deployments, and the Fleet template, already carry
optional `imagePullSecrets` references. When `registry-creds` does not exist in
the namespace the field is silently ignored (the Secret is not `required`). When
it does exist, the kubelet uses it to authenticate pulls.

```yaml
spec:
  template:
    spec:
      imagePullSecrets:
        - name: registry-creds
```

### 4. Override image tags at deploy time

Do NOT edit the committed manifests to change image tags. Use one of:

- **kustomize image transformer** (for the data tier):
  ```bash
  cd k8s/data
  kustomize edit set image rpg-mmo/nakama=ghcr.io/cuvara/nakama:v1.0.0
  ```

- **sed in CD** (for the app tier, which is not kustomized):
  ```bash
  sed -i "s|rpg-mmo/gateway:develop|ghcr.io/cuvara/gateway:${TAG}|g" app/40-gateway.yaml
  ```

- **Environment-specific overlays** (future: kustomize overlays per tier).

## Images

| Image | Dockerfile | Build context | Default local tag |
|-------|-----------|---------------|-------------------|
| `rpg-mmo/gateway` | `docker/Dockerfile.gateway` | `backend/` | `develop` |
| `rpg-mmo/gameserver-dotnet` | `docker/Dockerfile.gameserver-dotnet` | `backend/` | `develop` |
| `rpg-mmo/nakama` | `docker/Dockerfile.nakama` | `backend/` | `3.40.0` |

## Supported Registries

Any OCI-compliant registry works. Tested/documented:

- **GHCR** (`ghcr.io/cuvara`) -- GitHub Container Registry, free for public repos
- **Docker Hub** (`docker.io/cuvara`) -- rate-limited on free tier
- **Self-hosted** (`registry.example.com/rpg`) -- for air-gapped or on-prem
