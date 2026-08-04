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
| k3s setup scripts, k8s base/overlays | — | Planned |
| Gateway / gameserver Dockerfiles | — | Planned |
| DB init + backup scripts, prod Redis config (Sentinel, eviction) | — | Planned |
| Monitoring (Prometheus/Grafana) | — | Planned |

Docs:
- `RUNBOOK-local-dev.md` — start/stop/debug/reset the local stack, verification steps, REDIS_ADDR wiring.
- `CICD.md` — `build-all.sh` / `deploy-local.sh` usage, `cd.yml` job matrix, self-hosted
  runner registration + labels, Environment secrets, branch strategy, rollback.

## Local dev backing stack

`docker-compose.yml` runs the backing services the backend needs locally:

| Service | Image (pinned) | Ports | Notes |
|---------|----------------|-------|-------|
| `postgres` | `postgres:16.4-alpine` | 5432 | Nakama meta DB, `pg_isready` healthcheck, named volume `postgres-data` |
| `redis` | `redis:7.4-alpine` | 6379 | Sessions, server registry, event streams; AOF + RDB persistence on volume `redis-data`; `redis-cli ping` healthcheck; auth only when `REDIS_PASSWORD` is non-empty |
| `nakama` | `heroiclabs/nakama:3.40.0` | 7349 / 7350 / 7351 / 9100 | Waits for postgres healthy, runs `migrate up`, then serves; mounts `./modules` for the Go plugin |

The realtime services (gateway, gameserver) are not containerized yet — run them
on the host with `go run`, sharing `JWT_SECRET` and `REDIS_ADDR` with the stack:

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

## Build & deploy automation

```bash
scripts/build-all.sh                # vet + test + build every module -> bin/
scripts/build-all.sh --plugin       # also build modules/nakama.so via docker (docker.exe on WSL)
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
