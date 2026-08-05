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
  bin/{gateway,gameserver-dotnet,smoketest}   current binaries (+ .prev for manual rollback)
  deploy/docker-compose.yml         meta stack (postgres ×2 + redis + nakama + lgtm [+ realtime])
  deploy/.env                       generated by CD from Environment secrets, mode 0600
  deploy/modules/nakama.so          plugin, mounted by compose
  deploy/monitoring/                prometheus.yaml + Grafana dashboards, mounted by the lgtm service
  deploy/db/init-gamestate.sql      game-state schema, mounted by postgres-game
  scripts/deploy-local.sh
  scripts/register-gameserver.sh
  run/*.pid   logs/*.log   COMMIT
```

`deploy-local.sh` is only used in **host** mode — see §2c below.

---

## 2b. `scripts/register-gameserver.sh` — publish the registry entry

```bash
scripts/register-gameserver.sh [register|deregister]
```

The gateway answers `MsgEnterWorld` by looking the map up in the Redis server
registry (`servers:map:{map_id}` → `servers:id:{server_id}`,
`shared/storage/redisstore/registry.go`). **The C# game server never writes that
entry** — it has no Redis client at all (no `StackExchange.Redis` in
`GameServer.csproj`, no register/heartbeat call in `Program.cs`), so without
this script every `MsgEnterWorld` fails with *no available server for map …*.

Both deploy modes call it: `deploy-local.sh start` in host mode, the CD deploy
job right after `compose up` in containers mode. It is configured entirely from
`deploy/.env`; the field that matters is:

| Variable | Meaning |
|----------|---------|
| `GAMESERVER_PUBLIC_ADDR` | The address written to the registry, i.e. **the address clients are told to dial** (`MsgEnterWorldResp.ServerAddr`). It must be dialable *by the client*, not by the server. Defaults to `:<gameserver port>`, which clients normalize to loopback — right on a dev box, wrong on a VPS, where it must be `<public-host-or-ip>:<port>`. |
| `GAMESERVER_ID` / `GAMESERVER_MAP_ID` | Registry key + map index. |
| `REDIS_ADDR` / `REDIS_PASSWORD` | Where to write. Falls back to `docker exec <redis container> redis-cli` when no native `redis-cli` exists. |
| `REGISTRY_TTL` | Seconds, default 3600 — deliberately long because nothing heartbeats this entry yet. |

Delete the script the day the C# server registers itself.

---

## 2c. `scripts/bootstrap-vps.sh` — prepare a fresh VPS

One idempotent command turns a bare Ubuntu 22.04/24.04 box into a deploy target.
Run it as root on the VPS:

```bash
sudo RUNNER_TOKEN=<registration-token> ./scripts/bootstrap-vps.sh --labels staging
sudo ./scripts/bootstrap-vps.sh --dry-run --skip-runner      # preview, change nothing
```

What it does:

1. **Docker CE + compose plugin** from the official apt repo, service enabled.
2. **Deploy user** (`--deploy-user`, default `rpg`) added to the `docker` group,
   plus `$RPG_DEPLOY_DIR` (default `/opt/rpg-mmo`) with the `bin/ deploy/
   scripts/ run/ logs/` layout the deploy job expects.
3. **GitHub Actions runner** downloaded, registered `--unattended --replace` with
   labels `self-hosted,<--labels>`, and installed as a systemd service
   (`svc.sh install/start`) so it survives reboot and logout. The registration
   token is passed to one command and never written to disk.
4. **ufw**: default deny incoming; allow SSH, and the gateway + game server
   ports on **both tcp and udp** (udp reserved for the KCP transport, so the
   firewall does not need a second visit). Grafana is **denied**, with an
   `--admin-ip` flag for the allowlist variant. Because Docker's `DOCKER-USER`
   iptables chain is traversed *before* ufw's INPUT — a published container port
   ignores `ufw deny` — it also adds a matching `DOCKER-USER` DROP rule and warns
   that the rule is not reboot-persistent. Keeping `GRAFANA_BIND=127.0.0.1` and
   using an SSH tunnel remains the recommended posture.

Key flags (each has an env equivalent; the flag wins): `--runner-token`,
`--labels`, `--repo-url`, `--runner-name`, `--runner-version`, `--deploy-user`,
`--deploy-dir`, `--gateway-port`, `--gameserver-port`, `--ssh-port`,
`--grafana-port`, `--admin-ip`, `--skip-{docker,runner,firewall,user}`,
`--dry-run`. It exits non-zero on an unknown flag, a flag missing its value, a
non-numeric port, or a missing `RUNNER_TOKEN` (unless `--skip-runner`).

It finishes by printing the GitHub-side steps that remain — creating the
Environment, its secrets and its variables. There is no code change anywhere in
that list; see §8.

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
| `deploy` | `${{ fromJSON(resolve.runner_labels) }}` | Checkout → download bundle → `install` into `$RPG_DEPLOY_DIR` (keeping `.prev` binaries) → write `.env` from Environment secrets → `docker compose up -d` → then **either** `deploy-local.sh restart` (host mode) **or** build images + bring up the `realtime` profile + register the game server (containers mode). Outputs `deploy_dir`, `deploy_mode`. See §3b. |
| `post-deploy-smoke` | same labels as `deploy` | Sources `$RPG_DEPLOY_DIR/deploy/.env` and runs `bin/smoketest` (Nakama health → device auth → `gateway_token` RPC → gateway `MsgAuth`/`MsgEnterWorld` → game server join → input/snapshot → disconnect). Separate job so "deploy broke" and "the flow broke" are distinguishable at a glance. Takes the deploy dir from `needs.deploy.outputs.deploy_dir`, so it needs no `environment:` (no second production approval). |
| `summary` | `ubuntu-latest` | `if: always()` — step-summary table with ref, commit, runner, deploy dir and the `deploy` / `post-deploy-smoke` results. |

**Artifact flow:** `bin-{gateway,gameserver,smoketest}-<sha>` + `nakama-plugin-<sha>`
→ `bundle` → `deploy-bundle-<sha>` → `deploy`. All names carry `<sha>` so
re-runs and concurrent branches never collide. Artifact uploads do not preserve
the executable bit, which is why `deploy` uses `install -m 0755`.

**`skip_tests`** flips `run_tests: false` on the test jobs rather than skipping
them, keeping the `needs:` graph (and therefore the deploy path) intact — the
jobs still run `go vet`.

### 3b. Deploy modes — `vars.DEPLOY_MODE`

The `deploy` job can put the realtime services on the box two ways. It is a
per-environment variable, so one environment can move without touching any
other, and the switch is reversible.

| | `host` (default) | `containers` |
|---|---|---|
| What runs | `bin/gateway` + `bin/gameserver-dotnet` from the bundle | `rpg-gateway` + `rpg-gameserver` containers |
| Supervision | `scripts/deploy-local.sh` — systemd unit if present, else `setsid nohup` + pidfile | `restart: unless-stopped` (docker) |
| Images | — | built **on the runner** from `backend/deploy/docker/Dockerfile.{gateway,gameserver-dotnet}`, tagged `rpg-mmo/<svc>:<sha>` |
| Compose profiles | `monitoring` | `monitoring` + `realtime` |
| Redis / game DB | over the published host ports (`localhost:6379`, `localhost:5433`) | in-network service names (`redis:6379`, `postgres-game:5432`) |
| Healthcheck | TCP connect to both ports (`deploy-local.sh health`) | HTTP `/healthz` on each metrics port **and** TCP on each game port |
| Registration | `deploy-local.sh start` → `register-gameserver.sh` | dedicated step → `register-gameserver.sh` |
| Ports | the process binds `GATEWAY_ADDR` / `GAMESERVER_ADDR` directly | the container publishes `GATEWAY_CONTAINER_PORT` / `GAMESERVER_CONTAINER_PORT`, which **default to the ports those same addresses name** — so `:8000` / `:9200` stay true either way |

Order of operations in containers mode: **stop host-mode services first**
(`deploy-local.sh stop`, a no-op when nothing is running) — otherwise the old
host processes still own the ports the containers are about to publish. Then
build, `compose up`, register, probe.

Images are built on the target rather than pulled because dev/staging have no
registry credentials and the images never leave the box. The `build-images` job
(GHCR) stays the production/k8s path and is unaffected.

Switching an environment back is one variable: set `DEPLOY_MODE=host` and
redeploy — the `realtime` profile drops out and `--remove-orphans` removes the
containers before `deploy-local.sh` starts the binaries again.

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
On a fresh VPS, `scripts/bootstrap-vps.sh` (§2c) does everything in this section
plus Docker, the deploy user and the firewall in one command; the manual steps
below are the reference for what it automates.

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
| `GRAFANA_ADMIN_PASSWORD` | Grafana admin login (`GF_SECURITY_ADMIN_PASSWORD` on the `lgtm` container). Required **unless** `vars.MONITORING_ENABLED` is exactly `false` — a Grafana published with a default password is an open door. |

Optional secrets: `REDIS_PASSWORD` (empty = no auth), `NAKAMA_SERVER_KEY`
(defaults to `defaultkey` — change it outside dev).

Optional **variables** (`vars.*`, non-secret, per environment) with defaults:
`RPG_DEPLOY_DIR` (`/opt/rpg-mmo`), `NAKAMA_VERSION` (`3.40.0`), `POSTGRES_DB`
(`nakama`), `POSTGRES_USER` (`nakama`), `NAKAMA_CONSOLE_USER` (`admin`),
`GATEWAY_ADDR` (`:8000`), `GAMESERVER_ADDR` (`:9000`), `GAMESERVER_MAP_ID`
(`map_01`), `REDIS_ADDR` (`localhost:6379`), `GAME_DB_URL` (*empty*).

Deploy-mode variables (all optional; see §3b):

| Variable | Default | Effect |
|----------|---------|--------|
| `DEPLOY_MODE` | `host` | `containers` runs the realtime services as containers. Any other value fails the deploy loudly. |
| `GATEWAY_CONTAINER_PORT` | port of `GATEWAY_ADDR` | Host port the gateway container publishes. |
| `GAMESERVER_CONTAINER_PORT` | port of `GAMESERVER_ADDR` | Host port the game server container publishes. |
| `GAMESERVER_PUBLIC_ADDR` | `:<gameserver container port>` | **Address handed to clients.** Must be `<public-host>:<port>` on a VPS — the default resolves to loopback. |
| `GATEWAY_METRICS_PORT` / `GAMESERVER_METRICS_PORT` | `9102` / `9101` | Published `/metrics` + `/healthz` ports. |
| `GAMESERVER_METRICS_ADDR` | `gameserver-dotnet:9101` | Listen address of the C# metrics endpoint *inside* its container. Must stay a **resolvable name** — see `docs/MONITORING.md` § "The C# metrics endpoint cannot bind a wildcard". |

Monitoring variables (all optional; see `docs/MONITORING.md` §Deploying to a VPS
for the full table and firewall guidance):

| Variable | Default | Effect |
|----------|---------|--------|
| `MONITORING_ENABLED` | `true` | `false` deploys the plain meta stack — the compose step drops `--profile monitoring`, and `--remove-orphans` then removes a running `rpg-lgtm`. |
| `GRAFANA_USER` | `admin` | Grafana admin username. |
| `GRAFANA_PORT` / `GRAFANA_BIND` | `3000` / `0.0.0.0` | Published Grafana port + host interface. Use `127.0.0.1` on a public VPS and reach it over an SSH tunnel or a TLS reverse proxy. |
| `PROMETHEUS_PORT` / `PROMETHEUS_BIND` | `9090` / `127.0.0.1` | Bundled Prometheus — unauthenticated, keep on loopback. |
| `OTLP_GRPC_PORT` / `OTLP_HTTP_PORT` / `OTLP_BIND` | `4317` / `4318` / `127.0.0.1` | OTLP ingest — unauthenticated, keep on loopback. |
| `OTEL_LGTM_VERSION` | `0.11.15` | `grafana/otel-lgtm` image tag. |

Because the monitoring stack is plain compose config plus one secret, a new
environment gets observability by *setting `GRAFANA_ADMIN_PASSWORD`* — there is
no separate workflow or manual bring-up step.

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

   The deploy job keeps one previous copy of each binary as `*.prev`. In
   containers mode the equivalent is re-tagging: every deploy also leaves
   `rpg-mmo/<svc>:<sha>` in the local image store, so
   `GATEWAY_IMAGE=rpg-mmo/gateway:<old-sha>` in `deploy/.env` followed by
   `docker compose --profile realtime --profile monitoring up -d` rolls back
   without a rebuild.
4. **Meta stack** — `docker compose` images are version-pinned via
   `NAKAMA_VERSION`; roll back by setting the environment variable to the old tag
   and re-running the deploy. Postgres/Redis volumes are untouched by deploys.

## 8. Moving to a VPS — what actually changes

**Nothing in the code, and nothing in this repository's workflows.** A VPS is
just another self-hosted runner with its own GitHub Environment. The whole
delta:

| # | What | Where | Value |
|---|------|-------|-------|
| 1 | Prepare the box | on the VPS, once | `sudo RUNNER_TOKEN=… ./scripts/bootstrap-vps.sh --labels staging` (§2c) — installs Docker, the deploy user/dir, the runner, the firewall |
| 2 | Environment secrets | GitHub → Environments → `staging` | `JWT_SECRET`, `POSTGRES_PASSWORD`, `NAKAMA_CONSOLE_PASSWORD`, `GRAFANA_ADMIN_PASSWORD` — freshly generated, never the dev values |
| 3 | Run as containers | same Environment, variables | `DEPLOY_MODE=containers` |
| 4 | Deploy directory | variable | `RPG_DEPLOY_DIR=/opt/rpg-mmo` (whatever `bootstrap-vps.sh --deploy-dir` created) |
| 5 | **Client-dialable game server address** | variable | `GAMESERVER_PUBLIC_ADDR=<public-host-or-ip>:9200` — the one value that is *wrong* by default off-box: clients normalize the default `:9200` to their own loopback |
| 6 | Lock down Grafana | variable | `GRAFANA_BIND=127.0.0.1` (reach it over `ssh -L`), or `--admin-ip` at bootstrap time |
| 7 | Ports, if non-default | variables | `GATEWAY_CONTAINER_PORT`, `GAMESERVER_CONTAINER_PORT` — must match what `bootstrap-vps.sh --gateway-port/--gameserver-port` opened |
| 8 | Deploy | git | push the branch that maps to the environment (`staging`), or dispatch `cd.yml` with `environment=staging` |

The post-deploy smoke job is the acceptance test: it exercises Nakama auth →
gateway `MsgAuth`/`MsgEnterWorld` → game server join → input/snapshot on the
real box.

Not covered by the above, and still open: TLS/reverse proxy in front of Grafana
and Nakama, DB backups, and `GAME_DB_URL` (empty keeps the in-memory player
store — and the C# server does not read it yet regardless).

## 9. Known limits / unverified

- GitHub Actions cannot be executed locally; `cd.yml` is syntax-validated only.
  The `resolve` → `deploy` label plumbing (`fromJSON`) and the Environment secret
  wiring need one real run per environment to confirm.
- The `deploy` job assumes `docker compose` v2 and a Linux runner.
- Host mode still healthchecks with a TCP connect. Both binaries do serve
  `/healthz` on their metrics port now (containers mode uses it) — host mode
  could be upgraded the same way.
- The C# game server cannot bind a wildcard metrics address; containers mode
  works around it with a named prefix (`GAMESERVER_METRICS_ADDR`). In host mode
  the metrics endpoint fails to start entirely, so the `gameserver` scrape
  target is DOWN there. Fix belongs in `GameServer/Observability/MetricsEndpoint.cs`.
- Containers mode is verified on the dev runner. On a real VPS,
  `bootstrap-vps.sh` itself has only been exercised via `--dry-run` plus
  `bash -n` — the apt/runner/ufw paths need one real box to confirm.
- No blue/green or drain step — `deploy-local.sh restart` is a hard restart and
  drops in-flight realtime connections. Acceptable at Dev/Beta tier; revisit at
  Soft Launch (Agones handles this for game servers).
