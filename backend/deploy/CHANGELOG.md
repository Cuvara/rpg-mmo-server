# Changelog — Deploy / Infrastructure

All notable changes to deployment and infrastructure will be documented in this file.
Format based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

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
