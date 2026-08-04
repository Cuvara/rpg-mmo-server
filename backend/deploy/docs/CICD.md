# CI/CD — Build automation & continuous deployment

Two workflows plus one reusable building block:

| Workflow | File | Trigger | Purpose |
|----------|------|---------|---------|
| CI | `.github/workflows/ci.yml` | push/PR to `main`/`master` (`backend/**`, `.github/workflows/**`), `workflow_dispatch` | Validate — per-module vet + test + build. Never deploys. |
| CD | `.github/workflows/cd.yml` | push to `develop`, `staging`, `release-*`; `workflow_dispatch` | Build the deployable bundle, then deploy it to a self-hosted runner. |
| _(reusable)_ | `.github/workflows/_go-module.yml` | `workflow_call` only | vet + test (+ optional build / artifact upload) for **one** Go module. |

Every job that touches Go in either workflow is a call of `_go-module.yml`, so
the per-module recipe (checkout, setup-go with a module-scoped cache, vet, test,
build) exists in exactly one place. **Adding a module or a binary to both
pipelines is one additive `uses:` block** — no step duplication.

`_go-module.yml` inputs:

| Input | Type | Default | Meaning |
|-------|------|---------|---------|
| `module_dir` | string | *(required)* | Module path from the repo root, e.g. `backend/gateway`. All steps run with this as working directory. |
| `go_version` | string | `1.26` | Toolchain version. |
| `cache_dependency_path` | string | `<module_dir>/go.sum` | `setup-go` cache key file. Modules with **no external deps have no `go.sum`** (`backend/smoketest`) — pass their `go.mod` instead, otherwise `setup-go` fails to resolve the path. |
| `run_tests` | bool | `true` | `false` skips `go test` (used by build-only jobs and by the CD `skip_tests` dispatch input, which keeps the job graph intact instead of skipping jobs). |
| `test_flags` | string | `-race -timeout 120s` | Appended to `go test ./...`. |
| `needs_docker` | bool | `false` | Sets up Docker Buildx before `run_build` (used by the nakama plugin job). |
| `run_build` | string | `''` | Optional shell command run after vet/test. Empty = no build step. |
| `artifact_name` / `artifact_path` | string | `''` | If `artifact_name` is set, `artifact_path` (repo-root relative) is uploaded. |
| `artifact_retention_days` | number | `14` | Artifact retention. |

`scripts/build-all.sh` remains the **local dev** one-shot (vet + test + build
every module, `--plugin`, `--images`). CI/CD no longer call it — they run the
per-module commands directly so each module is its own job with its own log,
cache and failure signal. Keep the module list in `build-all.sh` and the job
list in the workflows in sync when adding a module.

---

## 1. `scripts/build-all.sh` — build everything

Runs `go vet` + `go test` + `go build` across every module, then optionally the
Nakama Go plugin via Docker.

```bash
scripts/build-all.sh                 # vet + test + build all modules
scripts/build-all.sh --skip-tests    # vet + build only (fast compile check)
scripts/build-all.sh --race          # add -race (needs gcc/cgo — plain WSL often lacks it)
scripts/build-all.sh --plugin        # also build backend/deploy/modules/nakama.so via docker
scripts/build-all.sh --images        # also build the gateway + gameserver container images
```

`--images` builds `backend/deploy/docker/Dockerfile.{gateway,gameserver}` with
context `backend/`, tagging `$IMAGE_PREFIX/<svc>:$IMAGE_TAG`
(default `rpg-mmo/<svc>:dev`). Same docker/docker.exe auto-detection as
`--plugin`.

Coverage, in order (fail-fast):

| Module | vet | test | build |
|--------|-----|------|-------|
| `backend/shared` | ✅ | ✅ | — (library) |
| `backend/gateway` | ✅ | ✅ | `bin/gateway` |
| `backend/gameserver` | ✅ | ✅ | `bin/gameserver` |
| `backend/nakama` | ✅ | ✅ | compile check only (real artifact is `-buildmode=plugin`, built in Docker) |
| `backend/smoketest` | ✅ | ✅ | `bin/smoketest` |
| `backend/integration_test` | — | ✅ (E2E, `-v`) | — |

Toolchain detection:

- **go** — `PATH`, then `$HOME/go/bin/go`, then `/usr/local/go/bin/go`.
- **docker** (only with `--plugin`) — `docker`, then `docker.exe` (Docker Desktop
  from WSL). Each candidate must answer `docker info`, so a stub binary with no
  daemon is rejected rather than silently used.

`--race` is **off by default** because typical WSL dev boxes have no C toolchain;
CI turns it on explicitly. Env overrides: `TEST_TIMEOUT` (default `120s`),
`NAKAMA_VERSION` (default `3.40.0`, must match the server tag — Go plugins are
ABI-locked).

---

## 2. `scripts/deploy-local.sh` — restart services on the target machine

Run by the CD `deploy` job on the runner, and usable by hand on any VPS.

```bash
scripts/deploy-local.sh start|stop|restart|status|health
```

- **Supervision** — if `rpg-gateway.service` / `rpg-gameserver.service` exist,
  `systemctl restart` is used. Otherwise a nohup + pidfile fallback: old process
  is SIGTERM'd (15s grace, then SIGKILL) via `$RPG_DEPLOY_DIR/run/<name>.pid`,
  the new binary starts detached with output appended to
  `$RPG_DEPLOY_DIR/logs/<name>.log`.
- **Env** — sourced from the first readable of `/etc/rpg-mmo/env`,
  `$RPG_DEPLOY_DIR/.env`. Values are never echoed; only the file path is logged.
- **Healthcheck** — TCP connect (`nc -z`, fallback bash `/dev/tcp`) to the
  gateway and gameserver ports, retried once a second for 30s, plus a best-effort
  `curl` of the Nakama `/healthcheck` (warn-only — the meta stack may still be
  migrating).

Layout (override with `RPG_DEPLOY_DIR`, default `/opt/rpg-mmo`):

```
/opt/rpg-mmo/
  bin/{gateway,gameserver}          current binaries (+ .prev copies for manual rollback)
  deploy/docker-compose.yml         meta stack (postgres + redis + nakama)
  deploy/.env                       generated by CD from Environment secrets, mode 0600
  deploy/modules/nakama.so          plugin, mounted by compose
  scripts/deploy-local.sh
  run/*.pid   logs/*.log   COMMIT
```

---

## 3. `cd.yml` — jobs

Every job is single-purpose; scaling out (a new module, a new binary, a new
environment) adds a job instead of growing an existing one.

```
resolve ──────────────────────────────────────────────────────────┐
                                                                  │
test-shared ─┬─ test-gateway ─────┬─ build-gateway ───────────┐    │
             ├─ test-gameserver ──┼─ build-gameserver ────────┤    │
             ├─ test-smoketest ───┼─ build-smoketest ─────────┤    │
             ├─ test-nakama ──────┴─ build-plugin ────────────┤    │
             └──────────────────── test-integration ──────────┤    │
                                                              ▼    │
                                   build-images (*)         bundle │
                                                              │    │
                                                              ▼    ▼
                                                            deploy
                                                              │
                                                              ▼
                                                    post-deploy-smoke
                                                              │
                                                              ▼
                                                     summary (always)
```

`(*)` `build-images` needs `resolve` + the two binary builds and only runs for
production / `build_images=true`; it is **not** on the deploy path.

| Job | Runner | What it does |
|-----|--------|--------------|
| `resolve` | `ubuntu-latest` | Maps the ref (or the dispatch input) to an environment name and a runner-label JSON array. Unmapped refs fail loudly. Runs in parallel with all test jobs. |
| `test-shared` | `ubuntu-latest` | `_go-module.yml` on `backend/shared`. Gate for the other module tests. |
| `test-gateway`, `test-gameserver`, `test-nakama`, `test-smoketest` | `ubuntu-latest` | `_go-module.yml`, one job per module, all parallel after `test-shared`. |
| `test-integration` | `ubuntu-latest` | `_go-module.yml` on `backend/integration_test` (E2E, gateway + gameserver in-process). Needs the four module tests. |
| `build-gateway` / `build-gameserver` / `build-smoketest` | `ubuntu-latest` | `_go-module.yml` with `run_tests: false`; each needs only *its own* module test, so it starts before integration finishes. Uploads `bin-<name>-<sha>`. |
| `build-plugin` | `ubuntu-latest` | `_go-module.yml` with `needs_docker: true` → `docker build -f nakama-plugin.Dockerfile --target export` → uploads `nakama-plugin-<sha>`. Parallel with the binary builds. |
| `build-images` | `ubuntu-latest` | Gateway + gameserver container images → GHCR. Conditional (see below). |
| `bundle` | `ubuntu-latest` | Downloads `bin-*-<sha>` (`merge-multiple`) + `nakama-plugin-<sha>`, adds `docker-compose.yml`, `Makefile`, `.env.example`, `deploy-local.sh`, `COMMIT`, asserts every expected file is present, uploads `deploy-bundle-<sha>` (14 days, `include-hidden-files` for `.env.example`). Compiles nothing. |
| `deploy` | `${{ fromJSON(resolve.runner_labels) }}` | Download bundle → `install` into `$RPG_DEPLOY_DIR` (keeping `.prev` binaries) → write `.env` from Environment secrets → `docker compose up -d` → `deploy-local.sh restart` → `health` + `status`. Outputs `deploy_dir`. |
| `post-deploy-smoke` | same labels as `deploy` | Sources `$RPG_DEPLOY_DIR/deploy/.env` and runs `bin/smoketest` (Nakama health → device auth → `gateway_token` RPC → gateway `MsgAuth`/`MsgEnterWorld` → game server join → input/snapshot → disconnect). Separate job so "deploy broke" and "the flow broke" are distinguishable at a glance. Takes the deploy dir from `needs.deploy.outputs.deploy_dir`, so it needs no `environment:` (no second production approval). |
| `summary` | `ubuntu-latest` | `if: always()` — step-summary table with ref, commit, runner, deploy dir and the `deploy` / `post-deploy-smoke` results. |

**Artifact flow:** `bin-{gateway,gameserver,smoketest}-<sha>` + `nakama-plugin-<sha>`
→ `bundle` → `deploy-bundle-<sha>` → `deploy`. All names carry `<sha>` so
re-runs and concurrent branches never collide. Artifact uploads do not preserve
the executable bit, which is why `deploy` uses `install -m 0755`.

**`skip_tests`** flips `run_tests: false` on the test jobs rather than skipping
them, keeping the `needs:` graph (and therefore the deploy path) intact — the
jobs still run `go vet`.

### Ref → environment → runner labels

| Branch | GitHub Environment | Runner labels |
|--------|--------------------|---------------|
| `develop` | `dev` | `self-hosted`, `dev` |
| `staging` | `staging` | `self-hosted`, `staging` |
| `release-*` | `production` | `self-hosted`, `production` |

`workflow_dispatch` accepts an `environment` choice (`dev` / `staging` /
`production`) that overrides the ref mapping, a `skip_tests` boolean for
emergency builds, and a `build_images` boolean.

### Container images (GHCR)

The `build-images` job pushes `ghcr.io/dycuong03/rpg-mmo-gateway` and
`ghcr.io/dycuong03/rpg-mmo-gameserver` (tags: `<short-sha>` + `latest`) **only
when** the resolved environment is `production` **or** `workflow_dispatch` set
`build_images=true`. Auth is `docker/login-action@v3` with the built-in
`GITHUB_TOKEN` (`permissions: packages: write` on that job); layers are cached
with `type=gha`. Dev/staging deploys skip this entirely and keep using the host
binaries from the artifact bundle. The gameserver image name must stay in sync
with `agones/fleet-map.yaml` and `agones/fleet-dungeon.yaml`.

### Concurrency

`concurrency.group` is `cd-<environment>` with `cancel-in-progress: true` — one
deploy per environment at a time; a newer push supersedes a queued/running one.
Two `release-*` branches therefore share the production lock, which is intended.

---

## 4. Self-hosted runner setup (per environment)

The runner machine **is** the target — your VPS, or your own machine for `dev`.

1. **GitHub → Settings → Actions → Runners → New self-hosted runner**, pick Linux
   x64, follow the shown `./config.sh` command, and add the labels:

   ```bash
   ./config.sh --url https://github.com/<owner>/rpg-mmo-server \
     --token <REGISTRATION_TOKEN> \
     --name rpg-dev-01 \
     --labels dev            # or: staging | production
   ```

   `self-hosted` is added automatically; you only supply the tier label.

2. **Install it as a service** so deploys survive logout:

   ```bash
   sudo ./svc.sh install && sudo ./svc.sh start && sudo ./svc.sh status
   ```

3. **Prerequisites on the runner**: `docker` + `docker compose` v2 (runner user in
   the `docker` group), `curl`, `netcat` (`nc`), and write access to
   `$RPG_DEPLOY_DIR` (default `/opt/rpg-mmo`):

   ```bash
   sudo mkdir -p /opt/rpg-mmo && sudo chown -R "$USER" /opt/rpg-mmo
   ```

   Go is **not** needed on the runner — only prebuilt binaries land there.

4. **Optional systemd units** (recommended for staging/production). Create
   `/etc/systemd/system/rpg-gateway.service`:

   ```ini
   [Unit]
   Description=RPG MMO Gateway
   After=network.target docker.service

   [Service]
   Type=simple
   EnvironmentFile=-/etc/rpg-mmo/env
   ExecStart=/opt/rpg-mmo/bin/gateway --addr=:8000
   Restart=always
   RestartSec=3
   User=rpg

   [Install]
   WantedBy=multi-user.target
   ```

   …and `rpg-gameserver.service` with
   `ExecStart=/opt/rpg-mmo/bin/gameserver --addr=:9000 --map-id=map_01`.
   Then `sudo systemctl daemon-reload && sudo systemctl enable --now rpg-gateway rpg-gameserver`.
   `deploy-local.sh` auto-detects the units and uses `systemctl` instead of nohup.
   Grant the runner user passwordless restart rights, e.g. in
   `/etc/sudoers.d/rpg-mmo`:

   ```
   runner ALL=(root) NOPASSWD: /bin/systemctl restart rpg-gateway, /bin/systemctl stop rpg-gateway, /bin/systemctl restart rpg-gameserver, /bin/systemctl stop rpg-gameserver
   ```

---

## 5. Secrets & variables (GitHub Environments)

Create three Environments — **dev**, **staging**, **production** (Settings →
Environments). Add protection rules (required reviewers, branch restriction to
`release-*`) on **production**.

Required **secrets** per environment — the deploy job fails if any is empty:

| Secret | Used for |
|--------|----------|
| `JWT_SECRET` | HS256 secret shared by Nakama, gateway, gameserver. Nakama signs client session tokens with it so the gateway verifies locally with no roundtrip. |
| `POSTGRES_PASSWORD` | Nakama meta DB password. |
| `NAKAMA_CONSOLE_PASSWORD` | Nakama admin console login. |

Optional secrets: `REDIS_PASSWORD` (empty = no auth), `NAKAMA_SERVER_KEY`
(defaults to `defaultkey` — change it outside dev).

Optional **variables** (`vars.*`, non-secret, per environment) with defaults:
`RPG_DEPLOY_DIR` (`/opt/rpg-mmo`), `NAKAMA_VERSION` (`3.40.0`), `POSTGRES_DB`
(`nakama`), `POSTGRES_USER` (`nakama`), `NAKAMA_CONSOLE_USER` (`admin`),
`GATEWAY_ADDR` (`:8000`), `GAMESERVER_ADDR` (`:9000`), `GAMESERVER_MAP_ID`
(`map_01`), `REDIS_ADDR` (`localhost:6379`), `GAME_DB_URL` (*empty*).

`GAME_DB_URL` is the game-state PostgreSQL DSN the gameserver opens at boot.
**Empty (the default) keeps the in-memory player store** — state is lost on
restart. Point it at the `postgres-game` compose service as the *host* sees it,
e.g. dev uses
`postgres://game:localdev@localhost:5433/gamestate?sslmode=disable` (the
gameserver is run on the host by `deploy-local.sh`, not in the compose network).
A wrong or unreachable DSN is fatal: the gameserver logs
`postgres player store init failed` and exits 1, which fails the deploy
healthcheck. The compose step therefore waits for `rpg-postgres-game` to report
`healthy` before restarting the realtime services.

**Secrets are never echoed.** They are passed to a step as env vars, checked for
emptiness by name only, and written to `$RPG_DEPLOY_DIR/deploy/.env` under
`umask 077` + `chmod 600`. The log prints the variable count, not the values.

> **Nakama key constraint (runtime-fatal):** `session.encryption_key` and
> `session.refresh_encryption_key` must differ — Nakama refuses to start when they
> are identical. `docker-compose.yml` therefore passes
> `--session.refresh_encryption_key "$${JWT_SECRET}-refresh"` while
> `--session.encryption_key` stays the raw `JWT_SECRET` (the value the gateway and
> gameserver verify against). Do not "simplify" these to the same value.

---

## 6. Branch strategy

```
feature/*  ──PR──►  main      (ci.yml validates; no deploy)
                      │
                      ├──►  develop     ──► CD ──► dev VPS
                      ├──►  staging     ──► CD ──► staging VPS
                      └──►  release-X.Y ──► CD ──► production VPS
```

- `main` stays the integration trunk validated by `ci.yml`.
- `develop` / `staging` are long-lived deploy branches — fast-forward from `main`.
- `release-X.Y` is cut from `main` for a production push; hotfixes land on the
  release branch and are cherry-picked back.

## 7. Rollback

1. **Preferred — re-run the last good deploy.** Actions → CD → pick the previous
   successful run → *Re-run all jobs*. It re-deploys that commit's artifact
   (available for 14 days).
2. **Or dispatch an older ref.** Actions → CD → *Run workflow* → pick the branch/tag
   and the target environment.
3. **Fast local rollback (binaries only)** on the runner box:

   ```bash
   cd /opt/rpg-mmo/bin
   mv gateway.prev gateway && mv gameserver.prev gameserver
   /opt/rpg-mmo/scripts/deploy-local.sh restart
   ```

   The deploy job keeps one previous copy of each binary as `*.prev`.
4. **Meta stack** — `docker compose` images are version-pinned via
   `NAKAMA_VERSION`; roll back by setting the environment variable to the old tag
   and re-running the deploy. Postgres/Redis volumes are untouched by deploys.

## 8. Known limits / unverified

- GitHub Actions cannot be executed locally; `cd.yml` is syntax-validated only.
  The `resolve` → `deploy` label plumbing (`fromJSON`) and the Environment secret
  wiring need one real run per environment to confirm.
- The `deploy` job assumes `docker compose` v2 and a Linux runner.
- Gateway/gameserver expose no HTTP health endpoint yet, so the healthcheck is a
  TCP connect. Upgrade it when a `/healthz` lands.
- No blue/green or drain step — `deploy-local.sh restart` is a hard restart and
  drops in-flight realtime connections. Acceptable at Dev/Beta tier; revisit at
  Soft Launch (Agones handles this for game servers).
