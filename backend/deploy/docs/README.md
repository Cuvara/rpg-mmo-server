# Deploy — Infrastructure & DevOps

Infrastructure configs for the RPG MMO backend. Supports 4 deployment tiers.
All open-source, $0 license: Docker, k3s, Agones, PostgreSQL, Redis, Grafana, Prometheus.

## What exists today

| Area | Files | Status |
|------|-------|--------|
| Local dev backing stack | `docker-compose.yml`, `nakama-plugin.Dockerfile`, `Makefile`, `.env.example` | ✅ Usable |
| Agones game server fleet | `agones/fleet-map-dotnet-dev.yaml`, `agones/secret-example.yaml`, `agones/allocation-dev.yaml` | ✅ Authored + server-side dry-run clean. **Never deployed** — ADR-14 stage 4 is the first run |
| Build automation | `scripts/build-all.sh` (repo root) | ✅ Usable |
| CD pipeline | `.github/workflows/cd.yml`, `scripts/deploy-local.sh` | ✅ Two modes: host binaries or full-docker (`vars.DEPLOY_MODE`) |
| VPS bootstrap | `scripts/bootstrap-vps.sh` | ✅ Docker + deploy user + Actions runner + ufw, idempotent, `--dry-run` |
| Gateway / gameserver images | `docker/Dockerfile.gateway`, `docker/Dockerfile.gameserver-dotnet` | ✅ Built + smoke-tested |
| k3s / Agones dev bootstrap | `k3s/setup-dev.sh`, `k3s/teardown-dev.sh`, `k3s/lib.sh`, `k3s/namespaces.yaml`, `k3s/validate-manifests.py` | ✅ Authored + manifests schema-validated (needs a live cluster) |
| Full k8s dev stack (Nakama, gateway, Redis, both PostgreSQL, Agones fleet) | `k8s/data/`, `k8s/app/`, `k8s/dev-up.sh`, `k8s/rollback-to-compose.sh`, `k8s/verify/` | ✅ Dev runs on it. **One replica of everything and `Recreate` on the two `hostPort` workloads — see `k8s/README.md` §Availability posture and ADR-17** |
| DB init + backup scripts, prod Redis config (Sentinel, eviction) | — | Planned |
| Dev observability (Grafana + Prometheus + Loki + Tempo) | `docker-compose.yml` profile `monitoring`, `monitoring/` | ✅ Usable (`make monitoring-up`) |

Docs:
- `RUNBOOK-local-dev.md` — start/stop/debug/reset the local stack, verification steps, REDIS_ADDR wiring.
- `CICD.md` — `build-all.sh` / `deploy-local.sh` usage, `cd.yml` job matrix, self-hosted
  runner registration + labels, Environment secrets, branch strategy, rollback.
- `DATABASE.md` — numbered game-state migrations (how to add one, the
  backward-compatibility rule), `db/backup.sh` / `db/restore.sh`, and the disaster
  recovery runbook.
- `DISASTER-RECOVERY.md` — what breaks when each dependency dies (Redis, both
  PostgreSQL instances, Nakama, gateway, game server, lgtm), what players
  experience, recovery commands, RTO/RPO, `db/redis-backup.sh` / `db/redis-restore.sh`,
  the open code gaps that make Redis data loss an unbounded outage, and the
  Sentinel upgrade path.
- `MONITORING.md` — `make monitoring-up` (one `grafana/otel-lgtm` container), scrape
  targets, the "RPG Gameplay" dashboard, metric contracts, Grafana Cloud / k3s paths.
- `REALTIME-FLOW.md` — what happens hop by hop when a player enters a map: the
  compose flow that runs today (deploy → `.env` → handshake → direct gameplay
  connection), the Agones flow as it stands (fleets Ready, `ALLOCATED 0`, and the
  address the gateway would hand out if they did), and what a working Agones path
  would require. Also the one-command checks for telling which of the three you
  are looking at.
- `K3S.md` — dev cluster bootstrap (`k3s/setup-dev.sh`), Agones install + fleets,
  cluster options on WSL2, offline manifest validation, graduation to a real k3s VPS.
- `../k8s/README.md` — the k8s dev stack that actually runs (namespaces `rpg-k8s-data`
  and `rpg-k8s-realtime`): bring-up, ports, verification, rollback, and the
  **availability posture** — one replica everywhere, why `Recreate` is required on the
  `hostPort` workloads, and what a gateway rollout costs (ADR-17).

## Local dev backing stack

`docker-compose.yml` runs the backing services the backend needs locally:

| Service | Image (pinned) | Ports | Notes |
|---------|----------------|-------|-------|
| `postgres` | `postgres:16.4-alpine` | 5432 | Nakama meta DB, `pg_isready` healthcheck, named volume `postgres-data` |
| `redis` | `redis:7.4-alpine` | 6379 | Sessions, server registry, event streams; AOF + RDB persistence on volume `redis-data`; `redis-cli ping` healthcheck; auth only when `REDIS_PASSWORD` is non-empty |
| `nakama` | `heroiclabs/nakama:3.40.0` | 7349 / 7350 / 7351 / 9100 | Waits for postgres healthy, runs `migrate up`, then serves; mounts `./modules` for the Go plugin |

The realtime services (gateway, gameserver) do have container images (see
"Gateway & gameserver container images" below), but the default local-dev flow
still runs them on the host (`go run` for gateway, `dotnet run` for
gameserver-dotnet), sharing `JWT_SECRET` and `REDIS_ADDR` with the stack:

```bash
export JWT_SECRET=dev-secret-change-me
export JOIN_TOKEN_SECRET=dev-join-secret-change-me   # must match .env; NOT JWT_SECRET
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

`docker/Dockerfile.gateway` and `docker/Dockerfile.gameserver-dotnet` produce the
images the Agones fleets reference.

**Gateway** (Go, multi-stage):

| Stage | Base | Notes |
|-------|------|-------|
| builder | `golang:1.26-alpine` | matches the `go 1.26` toolchain in every `go.mod`; `CGO_ENABLED=0`, `-trimpath`, `-ldflags "-s -w"` |
| runtime | `gcr.io/distroless/static-debian12:nonroot` | static binary; runs as uid 65532 |

Size: **gateway ~ 16.1 MB**.

**Game Server** (C# .NET 10 NativeAOT, multi-stage):

| Stage | Base | Notes |
|-------|------|-------|
| builder | `mcr.microsoft.com/dotnet/sdk:10.0` | `dotnet publish -c Release -r linux-x64 /p:PublishAot=true` |
| runtime | `gcr.io/distroless/static-debian12:nonroot` | NativeAOT self-contained binary; runs as uid 65532 |

`EXPOSE 8000` (gateway) / `9000` (gameserver, matching `containerPort` in the
fleet manifests).

The gateway build **context must be `backend/`** — `go.mod` carries
`replace github.com/duycuong/rpg-mmo/shared => ../shared`, so `shared/` has to be
visible. Same rule as `nakama-plugin.Dockerfile`. The gameserver-dotnet build
context is `backend/gameserver-dotnet/`.

```bash
# local (WSL: use docker.exe and run from backend/deploy — absolute /mnt/* paths
# do not survive the Windows CLI's path translation, cwd-relative ones do)
cd backend/deploy
docker build -f docker/Dockerfile.gateway -t rpg-mmo/gateway:dev ..
# Context is backend/ (`..`), NOT backend/gameserver-dotnet — the Dockerfile
# COPYs gameserver-dotnet/... from it. The tag is gameserver-DOTNET; plain
# rpg-mmo/gameserver:dev was the deleted Go server.
# GIT_REVISION is stamped into org.opencontainers.image.revision so the tag's
# contents can be checked later — see K3S.md.
docker build -f docker/Dockerfile.gameserver-dotnet \
  --build-arg GIT_REVISION="$(git rev-parse HEAD)" \
  -t rpg-mmo/gameserver-dotnet:dev ..

# or via the build script (auto-detects docker vs docker.exe)
scripts/build-all.sh --images                        # -> rpg-mmo/{gateway,gameserver}:dev
IMAGE_PREFIX=ghcr.io/cuvara IMAGE_TAG=v1 scripts/build-all.sh --images --skip-tests
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
| `ghcr.io/cuvara/rpg-mmo-gateway` | `<short-sha>`, `latest` |
| `ghcr.io/cuvara/rpg-mmo-gameserver` | `<short-sha>`, `latest` |

No Agones manifest references a GHCR image any more: the prod fleets were
deleted with the Go game server, and `agones/fleet-map-dotnet-dev.yaml` uses the
local tag `rpg-mmo/gameserver-dotnet:dev`. See `K3S.md` — and note that a
mutable local tag is a claim about content, which is why the Dockerfile now
stamps `org.opencontainers.image.revision` from a `GIT_REVISION` build arg and
`k3s/validate-manifests.py --check-image` can assert it.

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

> **⚠️ COSTS AND TIER CCU ARE ESTIMATES, AND THE PLAYER CEILING IS UNKNOWN.** No
> load test has been run on VPS hardware, and the per-game-server ceiling cannot
> be measured on the current box — see `backend/docs/BENCHMARK.md`.

**What is measured, and worth planning on: bandwidth.** **45.9 KB/s per client at
200 players**, inside ADR-7's < 50 KB/s mobile threshold, reproduced to **0.3%**
across six runs. RAM **~30 MiB idle → ~82 MiB at 200 players**.

**What is not measured: the player ceiling.** The former "150 players, bottleneck
= JSON serialization" figure is **retracted**. It predates Protobuf, the
entity-type enum and id interning — three changes that removed 81% of the wire
and with it the constraint that produced 150. Separately, the load generator
shares this host with the server under test and uses more CPU than it: under a
deploy competing for the box, tick p99 moved **3.3×** while bytes per client
moved 0.3%. Tick and CCU figures describe the measuring rig; bandwidth describes
the protocol. ADR-7 item 6 (generator on separate hardware) is a ⛔ **BLOCKER** on
any capacity claim.

Two configured ceilings bite before any performance limit does, and both matter
when sizing a deployment:

| Setting | Default | Effect |
|---|---|---|
| `GAMESERVER_CAPACITY` | 100, and **set explicitly in both map fleets** | Join is refused with "Server is full", and the refusal is now logged at Warning with the count and the limit — a full server and a broken one used to produce identical (empty) logs. A **policy limit, not headroom against a measured ceiling** — there is no measured ceiling to have headroom against. What 100 *is* justified by: 100 players observed running with tick p99 0.49 ms against a 66.67 ms budget and 0% of ticks over budget. Why it is not higher: the load generator shares the box (ADR-7) and k3d's `serverlb` sits in the gameplay data path (#143), so headroom at 100 says nothing about 150. Raise it when those two are cleared and a run brackets a real ceiling — #145. |
| `GATEWAY_CONN_RATE_PER_MIN` | 10 per source IP | Fine for real clients (one connect each), but it blocks load testing and any NAT with many players behind one address. Raise it if a carrier NAT is expected. |

| Tier | Cost/mo ⚠️ | Setup | CCU ⚠️ |
|------|---------|-------|-----|
| Dev/Alpha | $40-60 | 1 VPS all-in-one | < 200 |
| Beta | $80-150 | 2 VPS (app + DB) | 200-500 |
| Soft Launch | $200-400 | 3 VPS separated | 500-2000 |
| Growth | $400-1000+ | Multi-node k3s | 2000-5000+ |

The "game servers @ 150" column has been **removed rather than updated**. Every
value in it was tier CCU divided by the retracted 150 figure — arithmetic on a
number that no longer exists, presented with the confidence of a measurement. It
returns when a ceiling is measured on separate hardware, not before.

## Quick Start (Dev Tier, k3s — planned)

```bash
./k3s/setup.sh                      # planned
kubectl apply -k k8s/overlays/dev/  # planned
kubectl apply -f agones/
```
