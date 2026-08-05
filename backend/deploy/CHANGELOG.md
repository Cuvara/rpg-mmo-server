# Changelog — Deploy / Infrastructure

All notable changes to deployment and infrastructure will be documented in this file.
Format based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Fixed
- Redis now starts with an explicit `--maxmemory-policy noeviction`. This Redis is
  a system of record for the server registry and the event stream, not a cache:
  evicting a registry hash silently drops a live game server out of matchmaking,
  and trimming a stream drops unacked cross-server events. `noeviction` was already
  the Redis default, so behaviour is unchanged — the point is that adding a
  `--maxmemory` limit later can no longer silently turn this into an LRU cache.
  See `backend/docs/ARCHITECTURE-DECISIONS.md`, ADR-4.

### Changed
- Tier cost/CCU tables in `CLAUDE.md` and `docs/README.md` marked as unbenchmarked
  estimates (ADR-7)

### Added
- **Numbered database migrations** for the game-state DB.
  `db/migrations/gamestate/001_init.sql` holds the current schema; every future
  change is a new numbered file. The gameserver applies them transactionally,
  in order, exactly once, with checksum verification of anything already applied
  (see `backend/gameserver-dotnet` CHANGELOG for the runner). The Nakama meta DB
  is untouched — it migrates itself.
- **`db/backup.sh`** — `pg_dump -Fc` of both instances through `docker exec`,
  timestamped into `$BACKUP_DIR` (default `/var/backups/rpg-mmo`) with
  per-database retention (`--keep`, default 7). Every dump is verified with
  `pg_restore --list` and only then renamed off `.partial`, so a corrupt or
  interrupted run never leaves a file that looks like a usable backup.
  `--skip-missing` makes absent containers a warning instead of a failure.
- **`db/restore.sh`** — restores an archive into the live database or, with
  `--target`, into a scratch database so restores can be rehearsed without
  risking live data. Refuses to run without `--yes`, verifies the archive first,
  and prints per-table row counts afterwards.
- **`db-migrate` CD job** between `bundle` and `deploy`: backs up both databases,
  then runs `gameserver-dotnet --migrate-only` against `GAME_DB_URL`. `deploy`
  now depends on it, so a failed migration stops the rollout with the previous
  version still serving. New environment settings: `BACKUP_DIR`, `BACKUP_KEEP`.
- **`docs/DATABASE.md`** — migration workflow (how to add `002_*.sql`, the
  backward-compatibility rule that CD's migrate-before-deploy ordering implies),
  backup/restore usage, and a disaster-recovery runbook covering game-state loss,
  meta loss, a migration that fails mid-deploy, and checksum drift.

### Changed
- `db/init-gamestate.sql` is now documented as a **first-boot seed only** — schema
  changes go into numbered migrations. Its content is unchanged; the header was
  rewritten (and mirrored into the orphaned `shared/storage/pgstore/schema.sql`,
  which a Go test byte-compares against it).
- Bundle validation now also requires `db/migrations/gamestate/001_init.sql`,
  `db/backup.sh` and `db/restore.sh`.

- **Full-docker deploy mode (`vars.DEPLOY_MODE=containers`)** — the `deploy` job
  can now run the realtime services as containers instead of host binaries.
  Same secrets, same ports, same smoke test; the switch is one environment
  variable and is reversible.
  - New steps, all gated on the mode: stop host-mode services (so they release
    the ports), build `rpg-mmo/{gateway,gameserver-dotnet}:<sha>` **on the
    runner** from `docker/Dockerfile.{gateway,gameserver-dotnet}`, bring up the
    compose `realtime` profile alongside `monitoring`, register the game server,
    then probe `/healthz` on both metrics ports plus TCP on both game ports.
    Host mode keeps `deploy-local.sh restart` + `health` untouched.
  - Images are built on the target, not pulled: dev/staging have no registry
    credentials. `build-images` (GHCR) remains the production/k8s path.
  - `deploy` now checks out the repo (needed for the image build) and outputs
    `deploy_mode`; the pipeline summary reports it.
  - New environment variables: `DEPLOY_MODE`, `GATEWAY_CONTAINER_PORT`,
    `GAMESERVER_CONTAINER_PORT`, `GATEWAY_METRICS_PORT`,
    `GAMESERVER_METRICS_PORT`, `GAMESERVER_METRICS_ADDR`,
    `GAMESERVER_PUBLIC_ADDR`. The container ports default to the ports
    `GATEWAY_ADDR` / `GAMESERVER_ADDR` already name, so `:8000` / `:9200` hold
    in either mode.
- **`scripts/bootstrap-vps.sh`** — idempotent one-command VPS preparation for
  Ubuntu 22.04/24.04: Docker CE + compose plugin from the official apt repo, a
  deploy user and directory, a GitHub Actions runner registered and installed as
  a systemd service, and a ufw policy (SSH + game ports tcp/udp open, Grafana
  denied with an `--admin-ip` allowlist option and a matching `DOCKER-USER`
  rule, because Docker's iptables rules bypass ufw). `--dry-run` prints every
  action without executing it.
- **`scripts/register-gameserver.sh`** — writes the game server's Redis registry
  entry (`servers:id:*` / `servers:map:*`), extracted from `deploy-local.sh` so
  both deploy modes share one implementation. `GAMESERVER_PUBLIC_ADDR` is the
  address handed to clients — the single value that must change on a VPS.
- `docs/CICD.md`: §2b (register-gameserver.sh), §2c (bootstrap-vps.sh), §3b
  (deploy modes) and §8 "Moving to a VPS — what actually changes", a single
  table whose punchline is that no code changes.

### Changed
- `docker-compose.yml`: the `realtime` profile now holds the gateway **and** the
  C# game server (`container_name: rpg-gameserver`, published on
  `GAMESERVER_CONTAINER_PORT`, default 9200, plus its metrics port). Both are
  parameterized so CD can hand them the canonical ports.
- `Dockerfile.gameserver-dotnet` documents the metrics port with `EXPOSE 9101`.
- `deploy-local.sh` delegates registry writes to `register-gameserver.sh`.
- `bundle` ships `register-gameserver.sh` and asserts both scripts are present.

### Fixed
- The C# game server's env var names in compose were wrong (`MAP_ID` /
  `SERVER_ID`); the code reads `GAMESERVER_MAP_ID` / `GAMESERVER_ID`, so those
  settings were silently ignored. The `command:` array was also using
  `--flag=value`, which that server's arg parser does not match — configuration
  now goes through the environment, which works either way.
- Removed the dead `gameserver` compose service, which still referenced the
  deleted Go module's `rpg-mmo/gameserver:dev` image.

- CD deployed monitoring config changes without applying them: Prometheus and
  Grafana read their bind-mounted files only at container start, and
  `docker compose up -d` does not recreate a container just because a mounted
  file changed. The deploy job now hashes `deploy/monitoring/` before and after
  the sync and restarts `lgtm` when it differs.
- Game server metrics were reaching Prometheus under **dotted** OpenTelemetry
  names (`gameserver.players.online`), because Prometheus 3 negotiates UTF-8
  metric names and the C# exporter then serves the raw instrument names. Every
  "RPG Gameplay" panel queried the underscore form and returned *No data* while
  the scrape target read UP. `monitoring/prometheus.yaml` now pins
  `metric_name_escaping_scheme: underscores` on both game server jobs.

### Known issues (not fixed here — owned by `agent-gameserver-dotnet`)
- The C# metrics endpoint **cannot bind a wildcard**: `METRICS_ADDR=:9101`
  produces `http://+:9101/`, which OpenTelemetry's `PrometheusHttpListener`
  rejects (`UriFormatException: Invalid URI: The hostname could not be
  parsed`), so `/metrics` and `/healthz` never start. This is why the host-mode
  `gameserver` scrape target has been DOWN. Containers mode works around it with
  a resolvable prefix (`GAMESERVER_METRICS_ADDR=gameserver-dotnet:9101`);
  documented in `docs/MONITORING.md`.

### Added
- **Monitoring now deploys through CD (VPS-ready)** — `cd.yml` brings the
  `monitoring` profile up on every environment; no hand-run `make monitoring-up`.
  - `bundle` stages `backend/deploy/monitoring/` **and** `backend/deploy/db/`
    into the artifact and asserts each mounted file exists. Previously only
    `docker-compose.yml` + `Makefile` + `.env.example` shipped, so a fresh host
    got empty *directories* where `prometheus.yaml` / `init-gamestate.sql` were
    expected — silently wrong config instead of a hard failure.
  - `deploy` installs those trees into `$RPG_DEPLOY_DIR/deploy/`, replacing the
    previous copies wholesale so deletions in git propagate.
  - Compose step runs `docker compose --profile monitoring up -d
    --remove-orphans`, gated on `vars.MONITORING_ENABLED != 'false'` (default
    ON). Disabling it removes the running `rpg-lgtm` rather than orphaning it.
  - Env-file step writes `MONITORING_ENABLED`, `OTEL_LGTM_VERSION`,
    `GRAFANA_USER/ADMIN_PASSWORD/PORT/BIND`, `PROMETHEUS_PORT/BIND`,
    `OTLP_GRPC_PORT/HTTP_PORT/BIND`. New **required** environment secret
    `GRAFANA_ADMIN_PASSWORD` (fails the deploy with `::error` when monitoring is
    enabled and it is unset).
- `scripts/deploy-local.sh health` curls Grafana `/api/health` on
  `GRAFANA_PORT`. Warn-only by design and skipped when `MONITORING_ENABLED=false`
  — observability is off the gameplay critical path, so a dead Grafana must not
  fail a deploy that put a healthy game stack on the box.
- `docs/MONITORING.md` §"Deploying to a VPS": per-environment secret/variable
  table, how staging/production get monitoring (set one secret), and firewall
  guidance — SSH tunnel, Caddy reverse proxy + TLS, ufw allowlist, plus the
  `DOCKER-USER` chain caveat (Docker's iptables rules bypass ufw `INPUT`).
  Guidance only; no proxy is implemented. Also documents the two Grafana gotchas
  found while verifying the deploy: the anonymous-admin default (above) and the
  fact that `GF_SECURITY_ADMIN_PASSWORD` is applied **only when Grafana creates
  the admin user** — rotating the secret against an existing `lgtm-data` volume
  silently keeps the old password, so the doc gives the drop-the-DB procedure
  (`grafana cli admin reset-admin-password` reports success but produces a hash
  that does not authenticate against this image).

### Fixed
- **Grafana was reachable as an anonymous org Admin.** `grafana/otel-lgtm`'s
  `run-grafana.sh` exports `GF_AUTH_ANONYMOUS_ENABLED=true` +
  `GF_AUTH_ANONYMOUS_ORG_ROLE=Admin` whenever the variable is *unset*, so the
  login page was decoration — verified live: `GET /api/org` returned 200 with no
  credentials. The `lgtm` service now always sets
  `GF_AUTH_ANONYMOUS_ENABLED: ${GRAFANA_ANONYMOUS:-false}`. Would have shipped
  an open admin console the moment Grafana was published on a VPS.

### Changed
- `lgtm` service port bindings are parameterised for VPS exposure control:
  Grafana `${GRAFANA_BIND:-0.0.0.0}`, while OTLP and the bundled Prometheus now
  default to `127.0.0.1` — both are completely unauthenticated and nothing
  off-box talks to them yet.
- Grafana admin password env renamed `GRAFANA_PASSWORD` → `GRAFANA_ADMIN_PASSWORD`
  (matches the CD secret name), default `admin` → `localdev` (matches the other
  dev defaults in `.env.example`). Updated in `docker-compose.yml`,
  `.env.example`, `Makefile` (`monitoring-up` banner) and `docs/MONITORING.md`.
  **Action:** update the key in any existing local `backend/deploy/.env`.
- `docs/CICD.md`: `GRAFANA_ADMIN_PASSWORD` added to the required-secrets table,
  new monitoring-variables table, deploy-dir layout lists `deploy/monitoring/`
  and `deploy/db/`.
- `monitoring` compose profile: one `grafana/otel-lgtm` container (Grafana +
  Prometheus + Loki + Tempo + Pyroscope + OTel Collector) on `${GRAFANA_PORT:-3000}`,
  `${PROMETHEUS_PORT:-9090}`, OTLP `${OTLP_GRPC_PORT:-4317}` / `${OTLP_HTTP_PORT:-4318}`,
  persisted in the `lgtm-data` volume. Replaces the hand-rolled Prometheus+Grafana
  pair — fewer moving parts and OTLP ingest is ready for traces/logs.
- `monitoring/prometheus.yaml` mounted over the image's own (its documented
  override point) with scrape jobs for nakama (`nakama:9100`), host-run gateway
  (`host.docker.internal:9102`) and C# gameserver (`host.docker.internal:9101`),
  plus the containerised `realtime` variants.
- Provisioned "RPG Gameplay" Grafana dashboard (`monitoring/dashboards/`): tick
  p99, players online, gateway connections, auth/enter-world failure ratio, save
  and allocation errors, scrape-target health.
- `make monitoring-up|monitoring-down|monitoring-logs|monitoring-targets`;
  `.env.example` gained `OTEL_LGTM_VERSION`, `GRAFANA_PORT`, `GRAFANA_USER`,
  `GRAFANA_PASSWORD`, `PROMETHEUS_PORT`, `OTLP_*_PORT`, `GATEWAY_METRICS_PORT`.
- `docs/MONITORING.md` — rationale, usage, dashboard guide, import-by-ID infra
  dashboards (1860 / 763 / 9628), Grafana Cloud (Alloy) and k3s
  (kube-prometheus-stack) graduation paths.

### Changed
- Containerised gateway (`--profile realtime`) exports metrics on `:9102`
  (`METRICS_ADDR`), published as `${GATEWAY_METRICS_PORT:-9102}`.

### Changed
- Removed `docker/Dockerfile.gameserver` (Go). Added
  `docker/Dockerfile.gameserver-dotnet` (C# .NET 10 NativeAOT multi-stage build:
  `dotnet/sdk:10.0` builder → `distroless/static-debian12:nonroot` runtime).
- `cd.yml` updated to build the C# gameserver image instead of the Go one.
- Added `ci-dotnet.yml` workflow for C# gameserver build + test.

### Changed
- Fleet manifests (`agones/fleet-map.yaml`, `fleet-dungeon.yaml`,
  `fleet-map-dev.yaml`, `fleet-dungeon-dev.yaml`) inject `POD_NAME` via the
  downward API (`fieldRef: metadata.name`). The gameserver uses it as its
  `--server-id`, so the id it registers equals the `gameServerName` the gateway
  receives from a `GameServerAllocation` and signs into the join token.

### Fixed
- k3s bootstrap hardening from the first live run on Docker Desktop Kubernetes:
  kubectl resolution now prefers the binary that actually has a kube context
  (WSL: Linux kubectl often has an empty kubeconfig while kubectl.exe holds
  docker-desktop); agones-system namespace is created before applying the
  pinned install.yaml; agones-sdk ServiceAccount + rolebinding are created in
  the GameServer namespace (Agones only pre-creates them in `default`).

### Added
- `k3s/setup-dev.sh` — idempotent dev-cluster bootstrap: resolves kubectl
  (Linux `kubectl` → `kubectl.exe` → Docker Desktop's bundled path), preflights
  the cluster, installs Agones **1.59.0** (pinned to `agones.dev/agones` in
  `gameserver/go.mod`) with `apply --server-side --force-conflicts` (the CRDs
  exceed the 262 kB client-side apply annotation), waits for `agones-system`
  Available *and* for the `agones.dev/v1` webhook to actually serve Fleets,
  applies namespaces + dev Secret/ConfigMap + fleets, then blocks until a
  `GameServer` reaches `Ready`. Flags: `--with-dungeon`, `--with-autoscaler`,
  `--prod-fleets`, `--skip-agones`.
- `k3s/teardown-dev.sh` — reverse order (autoscalers → fleets → stray
  GameServers → config → namespaces, `--all` also uninstalls Agones);
  `--fleets-only` keeps the namespaces.
- `k3s/lib.sh` — shared helpers covering the WSL2 quirks: kubectl resolution,
  `kube_apply_file`/`kube_delete_file` that always pipe local manifests through
  stdin (kubectl.exe cannot read Linux paths), fail-fast `require_cluster` that
  checks `current-context` before touching the network (an empty kubeconfig
  otherwise makes kubectl burn ~25 s retrying `localhost:8080`), `retry`/`wait_for`.
- `k3s/namespaces.yaml` — `rpg-realtime` / `rpg-meta` / `rpg-data`.
- `k3s/validate-manifests.py` — offline manifest validation. `kubectl apply
  --dry-run=client` cannot check a `Fleet` without a live API server, so this
  extracts each CRD's `openAPIV3Schema` from the pinned Agones `install.yaml`
  (cached under `~/.cache/rpg-mmo/`) and validates with `jsonschema`, translating
  OpenAPI-3.0-isms (`x-kubernetes-*`, `nullable`, boolean `exclusiveMinimum`).
- `agones/fleet-map-dev.yaml`, `agones/fleet-dungeon-dev.yaml` — dev variants
  using the local `rpg-mmo/gameserver:dev` image with
  `imagePullPolicy: IfNotPresent` (the ghcr.io image is not published yet),
  literal env, and **no external dependencies** (in-memory registry + player
  store) so they reach `Ready` on a bare laptop cluster.
- `agones/autoscaler-dev.yaml` (buffer 1, max 2) and `agones/allocation-dev.yaml`.
- `docs/K3S.md` — cluster-option comparison and why Docker Desktop Kubernetes
  was chosen over k3d/native k3s on this box, bootstrap/teardown usage, image
  import per cluster type, `host.docker.internal` wiring, offline validation and
  its limits, graduation path to a real k3s VPS (kubeconfig secret + CD job
  sketch), and a WSL2 troubleshooting table.
### Changed
- **CI/CD topology — two fat jobs split into single-purpose jobs.** `cd.yml`'s
  `build-test` (vet + test + build + plugin + images + bundle) and `deploy`
  (sync + env + compose + restart + smoke + summary) were monoliths where one
  slow or flaky step blocked everything and a failure told you nothing about
  *what* broke. New graph:
  `resolve` ∥ `test-shared` → {`test-gateway`, `test-gameserver`, `test-nakama`,
  `test-smoketest`} → `test-integration`; `build-{gateway,gameserver,smoketest}`
  and `build-plugin` each hang off their own module test; `build-images`
  (GHCR, production / `build_images=true` only) off the binary builds; `bundle`
  assembles the artifacts; `deploy` → `post-deploy-smoke` → `summary`
  (`if: always()`). Deploy step contents are unchanged; the smoke test and the
  summary are now their own jobs, so "deploy failed" and "the flow failed" are
  distinguishable. Job graph documented in `docs/CICD.md` §3.

### Added
- `.github/workflows/_go-module.yml` — reusable `workflow_call` workflow that
  runs checkout + `setup-go` (cache keyed on the module's own `go.sum` via
  `cache-dependency-path`) + `go vet` + `go test` + an optional build and
  artifact upload for **one** Go module. Inputs: `module_dir`, `go_version`,
  `cache_dependency_path`, `run_tests`, `test_flags`, `needs_docker`,
  `run_build`, `artifact_name`, `artifact_path`, `artifact_retention_days`.
  Both `ci.yml` and `cd.yml` call it, so the per-module recipe exists once and
  adding a module is one additive `uses:` block.
- `ci.yml` now covers `backend/nakama` and `backend/smoketest` (previously
  untested in CI), gained `workflow_dispatch`, a `ci-<ref>` concurrency group,
  `.github/workflows/**` in its path filter, and per-binary build jobs.
- CD artifact flow is now per-binary: `bin-{gateway,gameserver,smoketest}-<sha>`
  and `nakama-plugin-<sha>` are merged by the `bundle` job into
  `deploy-bundle-<sha>` (still `include-hidden-files: true` for `.env.example`).
- CD `deploy` now passes `GAME_DB_URL` (from the environment variable
  `vars.GAME_DB_URL`, default empty) into the generated `deploy/.env`, wiring
  the PostgreSQL game-state persistence into deployed gameservers. Empty keeps
  the in-memory player store. Because the gameserver opens the DSN at boot and
  exits 1 when it cannot connect, the compose step now waits for the
  `rpg-postgres-game` container healthcheck before restarting the realtime
  services.
- `docker/Dockerfile.gateway` and `docker/Dockerfile.gameserver` — real
  container images for the realtime services (previously the Agones fleets
  referenced images that were never built). Multi-stage:
  `golang:1.26-alpine` builder (`CGO_ENABLED=0`, `-trimpath`,
  `-ldflags "-s -w"`, `go mod download` layer-cached) →
  `gcr.io/distroless/static-debian12:nonroot` runtime (no shell, non-root
  uid 65532). Build context must be `backend/` (`replace ... => ../shared`).
  Measured sizes: gateway 16.1 MB, gameserver 37.4 MB. `EXPOSE` 8000 / 9000,
  the latter matching `containerPort` in `agones/fleet-{map,dungeon}.yaml`.
- `scripts/build-all.sh --images` — builds both images via the existing
  docker/docker.exe auto-detection, cwd-relative from `backend/deploy/`.
  Tag overridable with `IMAGE_PREFIX` / `IMAGE_TAG` (default `rpg-mmo/*:dev`).
- `docker-compose.yml`: profile-gated `gateway` + `gameserver` services
  (`profiles: ["realtime"]`) wired to `redis:6379` and
  `postgres-game:5432`, published on host ports 8100 / 9300. Off by default —
  `docker compose up` behaviour is unchanged; they exist for container-parity
  testing while normal local dev keeps both processes on the host.
- `.github/workflows/cd.yml`: `build_images` boolean `workflow_dispatch` input
  plus GHCR build & push steps (`docker/login-action@v3`,
  `docker/build-push-action@v6`, gha layer cache, `packages: write` on the
  `build-test` job). Gated to run only when the resolved environment is
  `production` **or** `build_images=true`. Tags
  `ghcr.io/dycuong03/rpg-mmo-{gateway,gameserver}:<short-sha>` and `:latest`,
  matching the Agones fleet manifests.
- `postgres-game` service in `docker-compose.yml`: second PostgreSQL instance
  (`postgres:16.4-alpine`) for game state, separate from the Nakama meta DB —
  DB/user `gamestate`/`game`, host port `${POSTGRES_GAME_PORT:-5433}`, own
  `postgres-game-data` volume, `pg_isready` healthcheck, and
  `db/init-gamestate.sql` mounted into `/docker-entrypoint-initdb.d/`.
- `db/init-gamestate.sql` — `player_states` schema for first boot of an empty
  volume. Byte-identical to `backend/shared/storage/pgstore/schema.sql` (a Go
  test enforces this); the gameserver applies the same idempotent DDL at boot.
- `make psql-game` — psql shell on the game state DB.
- `.env.example`: `POSTGRES_GAME_DB`, `POSTGRES_GAME_USER`,
  `POSTGRES_GAME_PASSWORD`, `POSTGRES_GAME_PORT`.

### Changed
- `agones/fleet-map.yaml`, `agones/fleet-dungeon.yaml` — reality-pass against
  `gameserver/cmd/gameserver/main.go` and `gameserver/agones/sdk.go`:
  `portPolicy: Dynamic` made explicit (Agones assigns the host port; the
  container always binds `:9000`), `initialDelaySeconds` 5 → 10 to cover the
  Postgres migration on start, `--redis` added so gateway and gameservers share
  one registry/event stream, and `JWT_SECRET` / `REDIS_ADDR` / `GAME_DB_URL`
  wired to Secret `rpg-realtime-secrets` + ConfigMap `gameserver-config` with
  `optional: true` so the fleets still start before those objects exist. Added
  `app.kubernetes.io/part-of` / `rpg-mmo/role` labels.
- `agones/allocation.yaml` — documented that `GameServerAllocation` is a
  create-only aggregated-API resource (`kubectl create`, never `apply`).
- `docs/RUNBOOK-local-dev.md`: documents the two-PostgreSQL layout, port 5433,
  game-state verification/reset steps, and the host gameserver wiring
  `GAME_DB_URL=postgres://game:localdev@localhost:5433/gamestate?sslmode=disable`.

### Added
- CD post-deploy smoke phase: `bin/smoketest` (new `backend/smoketest` module)
  is staged into the deployment bundle, installed to `$RPG_DEPLOY_DIR/bin`, and
  run after the healthcheck with env sourced from `$RPG_DEPLOY_DIR/deploy/.env`.
  It exercises the full flow (Nakama health → device auth → `gateway_token` RPC
  → gateway auth/enter-world → game server join → input/snapshot loop → clean
  disconnect) and fails the deploy on any broken step (`SMOKE=FAIL`).

### Fixed
- Nakama refuses to start when `session.encryption_key` equals
  `session.refresh_encryption_key` — compose now derives the refresh key as
  `${JWT_SECRET}-refresh` (found by running the stack for real).
- `scripts/build-all.sh --plugin` failed under WSL with `docker.exe`: Windows
  docker CLI cannot resolve absolute `/mnt/*` context paths. Build now runs
  from `backend/deploy` with cwd-relative Dockerfile/context/output paths.

### Added
- Initial deploy module structure
- CLAUDE.md agent instructions for DevOps Engineer role
- `docker-compose.yml` — local dev meta stack: `postgres` (postgres:16.4-alpine,
  pg_isready healthcheck, named volume) + `nakama` (heroiclabs/nakama:3.40.0,
  waits for postgres healthy, runs `migrate up` then serves, mounts `./modules`
  for the Go plugin, exposes 7349/7350/7351/9100). All image tags pinned.
- `nakama-plugin.Dockerfile` — multi-stage build on
  heroiclabs/nakama-pluginbuilder:3.40.0 producing `nakama.so` from
  `backend/nakama` (+ `backend/shared` for the replace directive); `export`
  target writes the .so to the host, `runtime` target bakes it into a
  nakama image.
- `docker-compose.yml` — `redis` service (redis:7.4-alpine, `redis-cli ping`
  healthcheck, named volume `redis-data`, AOF `everysec` + RDB save rule,
  port 6379 via `REDIS_PORT`, `--requirepass` applied only when
  `REDIS_PASSWORD` is non-empty). Backs the upcoming shared
  RedisSessionStore / RedisServerRegistry / RedisEventStream (go-redis v9).
- `Makefile` — `plugin`, `image`, `up`, `down`, `reset`, `logs`, `logs-nakama`,
  `ps`, `psql`, `redis-cli`, `health`, `console` targets. `health` checks both
  the Nakama HTTP healthcheck and a Redis PING.
- `.env.example` — pinned NAKAMA_VERSION, postgres credentials, `REDIS_PORT` /
  `REDIS_PASSWORD` (empty default = no auth, matches shared/config), `JWT_SECRET`
  (shared HS256 secret with gateway/gameserver), console credentials, server key.
- `.gitignore` (ignores `.env`, `modules/*.so`) and `modules/.gitkeep`.
- `docs/RUNBOOK-local-dev.md` — build/start/stop/verify/debug/reset procedures,
  port table, plugin ABI version-pinning rule, failure-mode table, Redis
  verification (PING, XINFO STREAM/GROUPS, FLUSHALL) and `REDIS_ADDR` /
  `REDIS_PASSWORD` wiring for host-run gateway + gameserver.

- `scripts/build-all.sh` (repo root) — single build entrypoint used by devs and
  CI: `go vet` + `go test` + `go build` across shared / gateway / gameserver /
  nakama / integration_test, binaries to `bin/`. Flags `--skip-tests`, `--race`
  (off by default — WSL boxes usually lack gcc), `--plugin` (builds
  `backend/deploy/modules/nakama.so` via docker). Detects `go` from PATH →
  `$HOME/go/bin` → `/usr/local/go/bin`, and docker from `docker` → `docker.exe`
  (Docker Desktop under WSL), validating each with `docker info`. Fail-fast with
  per-step output.
- `scripts/deploy-local.sh` (repo root) — `start|stop|restart|status|health` for
  gateway + gameserver on the target machine. Uses systemd units
  (`rpg-gateway` / `rpg-gameserver`) when present, otherwise nohup + pidfile with
  SIGTERM→SIGKILL stop. Loads env from `/etc/rpg-mmo/env` or
  `$RPG_DEPLOY_DIR/.env` without echoing values; post-start healthcheck via
  `nc -z` (bash `/dev/tcp` fallback) plus best-effort Nakama `/healthcheck` curl.
- `.github/workflows/cd.yml` — CD pipeline. Triggers: push to `develop`,
  `staging`, `release-*`, plus `workflow_dispatch` (environment choice +
  `skip_tests`). Jobs: `resolve` (ref → environment + runner labels),
  `build-test` (ubuntu-latest, runs `build-all.sh --plugin --race`, uploads
  `deploy-bundle-<sha>`), `deploy` (self-hosted runner labeled `dev` / `staging`
  / `production`; installs binaries keeping `.prev` copies, writes `.env` from
  Environment secrets at mode 0600, `docker compose up -d`, then
  `deploy-local.sh restart` + healthcheck). Per-environment concurrency group
  with `cancel-in-progress`. `ci.yml` untouched.
- `docs/CICD.md` — build script reference, CD job matrix, self-hosted runner
  registration + labels, systemd unit + sudoers samples, required Environment
  secrets (`JWT_SECRET`, `POSTGRES_PASSWORD`, `NAKAMA_CONSOLE_PASSWORD`) and
  optional vars, branch strategy, rollback procedures, known limits.

### Changed
- `docs/README.md` — documents current state (Agones manifests + local dev meta
  stack), local stack usage, plugin build commands, and secrets handling; adds a
  build/deploy automation section and the `CICD.md` index entry.

### Fixed
- `docker-compose.yml` — Nakama refuses to start when `session.encryption_key`
  and `session.refresh_encryption_key` are identical (runtime-fatal). The
  refresh key is now `$${JWT_SECRET}-refresh` while the session key keeps the
  raw `JWT_SECRET` that gateway/gameserver verify against. Documented in
  `docs/README.md` and `docs/CICD.md`.
- `docs/RUNBOOK-local-dev.md` — replaced the "nothing has been executed yet"
  caveat with the verified end-to-end path (plugin build → `restart nakama` →
  `gateway_token` RPC → gateway `MsgAuth`). Records the actual module-load log
  lines, the real `gateway_token` smoke test (needs a *user* session, not
  `http_key`; body is a JSON-encoded string), the profile-hook storage check,
  measured latencies (device auth ~22 ms, RPC ~1-4 ms), and the `docker.exe`
  WSL fallback (run compose from `backend/deploy/`; `-f <abs WSL path>` breaks on
  path translation). Confirmed `nakama-pluginbuilder:3.40.0` ships `go1.26.5`,
  matching `backend/nakama/go.mod`, so no toolchain override is needed —
  `GOTOOLCHAIN=auto` is explicitly called out as the wrong fix (it reintroduces
  the plugin ABI mismatch).
