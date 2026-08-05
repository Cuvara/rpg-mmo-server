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
  deploy/db/init-gamestate.sql      first-boot seed, mounted by postgres-game
  deploy/db/migrations/gamestate/   numbered migrations (ops copies; see docs/DATABASE.md)
  deploy/db/{backup,restore}.sh     pg_dump / pg_restore helpers
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

One idempotent command turns a bare Ubuntu 22.04/24.04 box into a deploy target:
Docker CE, the deploy user and directory, the Actions runner as a systemd
service, and a ufw policy.

```bash
sudo RUNNER_TOKEN=<registration-token> ./scripts/bootstrap-vps.sh --labels staging
./scripts/bootstrap-vps.sh --dry-run --skip-runner      # preview, change nothing
```

**Full flag reference and the reasoning behind each step live in
[`VPS-SETUP.md`](VPS-SETUP.md) §1** — kept in one place so they cannot drift
from the script.

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

**Setting one up is documented once, in [`VPS-SETUP.md`](VPS-SETUP.md) §1**, and
automated by `scripts/bootstrap-vps.sh`: Docker CE, the deploy user and
directory, the runner registered with the right labels and installed as a
systemd service, and the firewall. Do not duplicate those steps here — they
drift.

What this pipeline relies on the runner providing:

| Requirement | Why |
|-------------|-----|
| Labels `self-hosted` + the environment name (`dev` / `staging` / `production`) | `resolve` emits those labels and `deploy` runs on them. A missing label means the run queues forever. |
| `docker` + `docker compose` v2, runner user in the `docker` group | The meta stack, and the image builds in containers mode. |
| `curl`, and `nc` or bash `/dev/tcp` | Health probes. |
| Write access to `$RPG_DEPLOY_DIR` | The bundle is installed there. |
| Installed as a systemd service | Deploys survive logout and reboot. |

Go and the .NET SDK are **not** needed — only prebuilt binaries land there.

### Optional: systemd units for host mode

Recommended for staging/production when `DEPLOY_MODE=host`. `deploy-local.sh`
auto-detects `rpg-gateway.service` / `rpg-gameserver.service` and uses
`systemctl` instead of its nohup fallback, which gives you restart-on-crash and
survival across reboots.

```ini
# /etc/systemd/system/rpg-gateway.service
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
`ExecStart=/opt/rpg-mmo/bin/gameserver-dotnet --addr=:9200 --map-id=map_01`.
Then `sudo systemctl daemon-reload && sudo systemctl enable --now rpg-gateway rpg-gameserver`,
and grant the runner user passwordless restart rights in
`/etc/sudoers.d/rpg-mmo`:

```
runner ALL=(root) NOPASSWD: /bin/systemctl restart rpg-gateway, /bin/systemctl stop rpg-gateway, /bin/systemctl restart rpg-gameserver, /bin/systemctl stop rpg-gameserver
```

In `DEPLOY_MODE=containers` none of this applies — `restart: unless-stopped`
does the same job.

---

## 5. Secrets & variables (GitHub Environments)

**The complete catalogue — every secret and variable, with meaning, default and
whether it is required — is [`VPS-SETUP.md`](VPS-SETUP.md) §2**, along with
`scripts/setup-github-env.sh`, which sets all of them for a new environment in
one command. This section covers only how the pipeline *treats* them.

Environments are `dev`, `staging` and `production` (Settings → Environments).
Put protection rules (required reviewers, branch restriction to `release-*`) on
**production**.

**Hard requirements.** The deploy job fails loudly if any of these is empty:
`JWT_SECRET`, `POSTGRES_PASSWORD`, `NAKAMA_CONSOLE_PASSWORD` — plus
`GRAFANA_ADMIN_PASSWORD` whenever `vars.MONITORING_ENABLED` is not exactly
`false`, because a Grafana published with a default password is an open door.

**Handling.** Secrets are passed to the step as environment variables, checked
for emptiness *by name only*, and written to `$RPG_DEPLOY_DIR/deploy/.env` under
`umask 077` + `chmod 600`. The log prints the variable count, never a value.
That single `.env` is what both `docker compose` and the host binaries read, so
the meta stack and the realtime services cannot disagree about a secret.

**Scope matters.** These are *environment*-scoped. A repository-level secret of
the same name does not satisfy `secrets.X` inside a job with
`environment: <name>` — it resolves to empty and fails the check above.

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

## 8. Moving to a VPS

**Nothing in the code, and nothing in these workflows.** A VPS is just another
self-hosted runner with its own GitHub Environment: bootstrap the box, create
the environment, push the branch.

The step-by-step is [`VPS-SETUP.md`](VPS-SETUP.md) — §1 bootstrap, §2
environment, §3 first deploy, §4 verification checklist, §5 moving an
environment between machines.

The one value that is *wrong* by default off-box is
`GAMESERVER_PUBLIC_ADDR`: it defaults to a listen-style `:9200`, which every
client normalizes to its own loopback. Set it to `<public-host-or-ip>:<port>`.

---

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
