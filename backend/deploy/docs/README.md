# Deploy — Infrastructure & DevOps

Infrastructure configs for the RPG MMO backend. Supports 4 deployment tiers.
All open-source, $0 license: Docker, k3s, Agones, PostgreSQL, Redis, Grafana, Prometheus.

## What exists today

| Area | Files | Status |
|------|-------|--------|
| Local dev backing stack | `docker-compose.yml`, `nakama-plugin.Dockerfile`, `Makefile`, `.env.example` | ✅ Usable |
| Agones game server fleets | `agones/fleet-map.yaml`, `agones/fleet-dungeon.yaml`, `agones/autoscaler.yaml`, `agones/allocation.yaml` | ✅ Manifests authored |
| Build automation | `scripts/build-all.sh` (repo root) | ✅ Usable |
| CD pipeline | `.github/workflows/cd.yml`, `scripts/deploy-local.sh` | ✅ Authored (needs a live runner) |
| Gateway / gameserver images | `docker/Dockerfile.gateway`, `docker/Dockerfile.gameserver` | ✅ Built + smoke-tested |
| k3s / Agones dev bootstrap | `k3s/setup-dev.sh`, `k3s/teardown-dev.sh`, `k3s/lib.sh`, `k3s/namespaces.yaml`, `k3s/validate-manifests.py` | ✅ Authored + manifests schema-validated (needs a live cluster) |
| k8s base/overlays (Nakama, Gateway, Redis, Postgres) | — | Planned |
| DB init + backup scripts, prod Redis config (Sentinel, eviction) | — | Planned |
| Monitoring (Prometheus/Grafana) | — | Planned |

Docs:
- `RUNBOOK-local-dev.md` — start/stop/debug/reset the local stack, verification steps, REDIS_ADDR wiring.
- `CICD.md` — `build-all.sh` / `deploy-local.sh` usage, `cd.yml` job matrix, self-hosted
  runner registration + labels, Environment secrets, branch strategy, rollback.
- `K3S.md` — dev cluster bootstrap (`k3s/setup-dev.sh`), Agones install + fleets,
  cluster options on WSL2, offline manifest validation, graduation to a real k3s VPS.

## Local dev backing stack

`docker-compose.yml` runs the backing services the backend needs locally:

| Service | Image (pinned) | Ports | Notes |
|---------|----------------|-------|-------|
| `postgres` | `postgres:16.4-alpine` | 5432 | Nakama meta DB, `pg_isready` healthcheck, named volume `postgres-data` |
| `redis` | `redis:7.4-alpine` | 6379 | Sessions, server registry, event streams; AOF + RDB persistence on volume `redis-data`; `redis-cli ping` healthcheck; auth only when `REDIS_PASSWORD` is non-empty |
| `nakama` | `heroiclabs/nakama:3.40.0` | 7349 / 7350 / 7351 / 9100 | Waits for postgres healthy, runs `migrate up`, then serves; mounts `./modules` for the Go plugin |

The realtime services (gateway, gameserver) do have container images (see
"Gateway & gameserver container images" below), but the default local-dev flow
still runs them on the host with `go run`, sharing `JWT_SECRET` and `REDIS_ADDR`
with the stack:

```bash
export JWT_SECRET=dev-secret-change-me
export REDIS_ADDR=localhost:6379   # container-to-container it would be redis:6379
export REDIS_PASSWORD=             # must match .env
```

Env names come from `backend/shared/config` (`JWT_SECRET`, `REDIS_ADDR`,
`REDIS_PASSWORD`, `META_DB_URL`). Defaults already point at this stack.

```bash
cd backend/deploy
cp .env.example .env
make plugin     # compile backend/nakama -> modules/nakama.so
make up         # start postgres + redis + nakama
make health     # nakama /healthcheck + redis PING
make logs       # tail
make down       # stop (keeps data volumes) | make reset = stop + wipe volumes + plugin
```

Nakama console: <http://localhost:7351> — default `admin` / `password` (from `.env`).

### Nakama plugin build

`nakama-plugin.Dockerfile` is a multi-stage build on
`heroiclabs/nakama-pluginbuilder:3.40.0`. The build context **must be `backend/`**
because `backend/nakama/go.mod` has `replace github.com/duycuong/rpg-mmo/shared => ../shared`.

```bash
# from repo root — export nakama.so to the host (what compose mounts)
DOCKER_BUILDKIT=1 docker build -f backend/deploy/nakama-plugin.Dockerfile \
  --target export --output type=local,dest=backend/deploy/modules backend/

# or bake it into a runnable image (k3s / CI)
docker build -f backend/deploy/nakama-plugin.Dockerfile \
  --target runtime -t rpg-mmo/nakama:3.40.0 backend/
```

The pluginbuilder tag must match the nakama server tag exactly — Go plugins are
ABI-locked. See RUNBOOK-local-dev.md § "Version pinning rule".

### Secrets

`.env` values are local-dev defaults only (`localdev`, `admin/password`,
`dev-secret-change-me`, `defaultkey`; Redis runs with no auth by default).
`JWT_SECRET` must match the value used by
gateway and gameserver (`backend/shared/config`, env `JWT_SECRET`) — Nakama signs
session tokens with it and the gateway verifies them locally. From Beta tier
onward these move to k8s Secrets.

Nakama refuses to start when `session.encryption_key` and
`session.refresh_encryption_key` are identical, so compose passes
`--session.refresh_encryption_key "$${JWT_SECRET}-refresh"` while
`--session.encryption_key` keeps the raw `JWT_SECRET` that gateway/gameserver
verify against. Do not collapse the two into one value.

For deployed environments (dev/staging/production) these values come from GitHub
Environment secrets and are written to `.env` by the CD workflow — see
`CICD.md`.

## Gateway & gameserver container images

`docker/Dockerfile.gateway` and `docker/Dockerfile.gameserver` produce the images
the Agones fleets reference. Both are multi-stage:

| Stage | Base | Notes |
|-------|------|-------|
| builder | `golang:1.26-alpine` | matches the `go 1.26` toolchain in every `go.mod`; `CGO_ENABLED=0`, `-trimpath`, `-ldflags "-s -w"` |
| runtime | `gcr.io/distroless/static-debian12:nonroot` | static binary → no libc; no shell/package manager; runs as uid 65532 |

Sizes (measured): **gateway ≈ 16.1 MB**, **gameserver ≈ 37.4 MB** (~30s cold
build each, ~2s warm). `EXPOSE 8000` (gateway) / `9000` (gameserver, matching
`containerPort` in the fleet manifests).

The build **context must be `backend/`** — both `go.mod` files carry
`replace github.com/duycuong/rpg-mmo/shared => ../shared`, so `shared/` has to be
visible. Same rule as `nakama-plugin.Dockerfile`.

```bash
# local (WSL: use docker.exe and run from backend/deploy — absolute /mnt/* paths
# do not survive the Windows CLI's path translation, cwd-relative ones do)
cd backend/deploy
docker build -f docker/Dockerfile.gateway    -t rpg-mmo/gateway:dev    ..
docker build -f docker/Dockerfile.gameserver -t rpg-mmo/gameserver:dev ..

# or via the build script (auto-detects docker vs docker.exe)
scripts/build-all.sh --images                        # -> rpg-mmo/{gateway,gameserver}:dev
IMAGE_PREFIX=ghcr.io/dycuong03 IMAGE_TAG=v1 scripts/build-all.sh --images --skip-tests
```

Smoke-check a built image (distroless has no shell, so probe from the host):

```bash
docker run --rm -d --name gw -e JWT_SECRET=dev-secret-change-me -p 8100:8000 \
  rpg-mmo/gateway:dev --addr=:8000
docker logs gw     # expect: {"level":"INFO","msg":"gateway listening","addr":"[::]:8000"}
docker stop gw
```

### CI images & fleet linkage

`cd.yml` builds and pushes to GHCR **only** when the ref resolves to the
`production` environment (a `release-*` branch, or `workflow_dispatch` with
`environment=production`) **or** `workflow_dispatch` is run with
`build_images=true`. Dev/staging deploys keep shipping host binaries in the
artifact bundle.

| Image | Tags |
|-------|------|
| `ghcr.io/dycuong03/rpg-mmo-gateway` | `<short-sha>`, `latest` |
| `ghcr.io/dycuong03/rpg-mmo-gameserver` | `<short-sha>`, `latest` |

`agones/fleet-map.yaml` and `agones/fleet-dungeon.yaml` both reference
`ghcr.io/dycuong03/rpg-mmo-gameserver:latest` — renaming the image in `cd.yml`
means renaming it in both manifests too.

### Compose parity testing (optional)

`docker-compose.yml` carries profile-gated `gateway` and `gameserver` services.
They are **off by default** — normal local dev runs both on the host with
`go run`. Start them only for container parity checks:

```bash
scripts/build-all.sh --images
cd backend/deploy && docker compose --profile realtime up -d
```

They join the compose network (`REDIS_ADDR=redis:6379`,
`GAME_DB_URL=…@postgres-game:5432/gamestate`) and publish on host ports
**8100 / 9300** so a host-run gateway (:8000) and gameserver (:9000) can run
alongside. No compose healthcheck: distroless has no `nc`/`curl` — probe with
`nc -z localhost 8100` from the host, or a `tcpSocket` probe in k8s.

## Build & deploy automation

```bash
scripts/build-all.sh                # vet + test + build every module -> bin/
scripts/build-all.sh --plugin       # also build modules/nakama.so via docker (docker.exe on WSL)
scripts/build-all.sh --images       # also build the gateway + gameserver container images
scripts/deploy-local.sh restart     # stop/start gateway+gameserver on this machine, then healthcheck
```

`.github/workflows/cd.yml` builds the same bundle on `ubuntu-latest` and deploys
it to a self-hosted runner: `develop` → dev, `staging` → staging, `release-*` →
production. Full details in `CICD.md`.

## Agones (k3s)

`agones/` holds Fleet definitions for map and dungeon servers, a buffer-based
FleetAutoscaler, and an allocation policy for dungeon instances.

```bash
kubectl apply -f agones/
```

## Tiers

| Tier | Cost/mo | Setup | CCU |
|------|---------|-------|-----|
| Dev/Alpha | $40-60 | 1 VPS all-in-one | < 200 |
| Beta | $80-150 | 2 VPS (app + DB) | 200-500 |
| Soft Launch | $200-400 | 3 VPS separated | 500-2000 |
| Growth | $400-1000+ | Multi-node k3s | 2000-5000+ |

## Quick Start (Dev Tier, k3s — planned)

```bash
./k3s/setup.sh                      # planned
kubectl apply -k k8s/overlays/dev/  # planned
kubectl apply -f agones/
```
