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
  run/*.pid   logs/*.log   COMMIT
```

`deploy-local.sh` is only used in **host** mode — see §2c below.

---

## 2b. Server registration — nothing to run

The gateway answers `MsgEnterWorld` by looking the map up in the Redis server
registry (`servers:map:{map_id}` → `servers:id:{server_id}`,
`shared/storage/redisstore/registry.go`).

**The C# game server writes that entry itself.** On startup it registers, then
heartbeats every 5s to re-arm the 15s TTL, and deregisters on graceful shutdown.
A missing entry is re-created by the next heartbeat, so a wiped Redis heals in
about 5s with no human step. This replaced `scripts/register-gameserver.sh`,
which wrote the entry once at deploy time with a 3600s TTL and no refresh.

Two environment variables drive it, both supplied from `deploy/.env`:

| Variable | Meaning |
|----------|---------|
| `REDIS_ADDR` | Registry to publish into. Unset = no self-registration, and the gateway will not find this server. |
| `GAMESERVER_PUBLIC_ADDR` | The address handed to CLIENTS, verbatim, in `MsgEnterWorldResp.ServerAddr`. It is **not** the listen address whenever containers map ports (listen `:9000`, published `:9200`). Falls back to `GAMESERVER_ADDR`, which is right in host mode. On a VPS set it to `<public-host>:<published-port>`. |

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
             ├─ test-nakama ──────┼─ build-plugin ────────────┤    │
             ├───────────────────-┴─ build-verify-probe ──────┤    │
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
| `build-verify-probe` | `ubuntu-latest` | `_go-module.yml` on `backend/deploy/k8s/verify/probe` with `run_tests: false`. Uploads `bin-verify-probe-<sha>`. Exists because the k8s post-deploy verification must not compile anything on the deploy runner — see §4 below. |
| `build-plugin` | `ubuntu-latest` | `_go-module.yml` with `needs_docker: true` → `docker build -f nakama-plugin.Dockerfile --target export` → uploads `nakama-plugin-<sha>`. Parallel with the binary builds. |
| `build-images` | `ubuntu-latest` | Gateway + gameserver container images → GHCR. Conditional (see below). |
| `bundle` | `ubuntu-latest` | Downloads `bin-*-<sha>` (`merge-multiple`) + `nakama-plugin-<sha>`, adds `docker-compose.yml`, `Makefile`, `.env.example`, `deploy-local.sh`, `COMMIT`, asserts every expected file is present, uploads `deploy-bundle-<sha>` (14 days, `include-hidden-files` for `.env.example`). Compiles nothing. |
| `deploy` | `${{ fromJSON(resolve.runner_labels) }}` | Checkout → download bundle → `install` into `$RPG_DEPLOY_DIR` (keeping `.prev` binaries) → write `.env` from Environment secrets → **bring up the data tier** (postgres, redis, nakama; no `realtime` profile) → **apply pending migrations** → `docker compose up -d` with the remaining profiles → then **either** `deploy-local.sh restart` (host mode) **or** build images + bring up the `realtime` profile + register the game server (containers mode). Outputs `deploy_dir`, `deploy_mode`. See §3b. |

> **Why migrations run inside `deploy` and not in `db-migrate`.** They were in the earlier
> job, which cannot work on an environment that has never been deployed: the database does
> not exist yet, `--migrate-only` fails on connect, and `deploy` — the job that would have
> created it — is gated on that migration succeeding. A first production deploy failed
> exactly that way with `Failed to connect to 127.0.0.1:5443`, having already pushed its
> images.
>
> Splitting the stack in two keeps the ordering that mattered and fixes the one that did
> not: the data tier comes up, migrations run against it, and only then does the `realtime`
> profile start. **Schema is still migrated before any new binary serves traffic.** The
> data-tier step deliberately omits `--remove-orphans` and the `realtime` profile, so it
> cannot disturb a running gateway or game server while the schema changes, and it waits on
> `pg_isready` rather than on compose reporting the container started — on a first deploy
> postgres has an empty data directory to initialise first.
>
> `db-migrate` is now backup-only, and its display name says so. The job id is unchanged
> because `deploy` depends on it and the backup is still what gates the deploy.
| `post-deploy-smoke` | same labels as `deploy` | Sources `$RPG_DEPLOY_DIR/deploy/.env`, overrides the two client-facing addresses (below), and runs `bin/smoketest` (Nakama health → device auth → `gateway_token` RPC → gateway `MsgAuth`/`MsgEnterWorld` → game server join → input/snapshot → disconnect). Separate job so "deploy broke" and "the flow broke" are distinguishable at a glance. Takes the deploy dir from `needs.deploy.outputs.deploy_dir`, so it needs no `environment:` (no second production approval). |
| `post-deploy-smoke` | same labels as `deploy` | Sources `$RPG_DEPLOY_DIR/deploy/.env` and runs `bin/smoketest` (Nakama health → device auth → `gateway_token` RPC → gateway `MsgAuth`/`MsgEnterWorld` → game server join → input/snapshot → disconnect). Separate job so "deploy broke" and "the flow broke" are distinguishable at a glance. Takes the deploy dir from `needs.deploy.outputs.deploy_dir`, so it needs no `environment:` (no second production approval). |
| `summary` | `ubuntu-latest` | `if: always()` — step-summary table with ref, commit, runner, deploy dir and the `deploy` / `post-deploy-smoke` results. |

**`post-deploy-smoke` inputs.** `deploy/.env` is written for the **services**, and the
smoke test is a **client**, so sourcing it is not sufficient — two of the addresses it
needs are listen addresses, not dialable ones. The step therefore exports these on top,
after sourcing:

| Variable | Value exported | Why not `deploy/.env` |
|---|---|---|
| `NAKAMA_URL` | `http://127.0.0.1:${NAKAMA_HTTP_PORT}` | Absent from `.env` by design (compose gives the game server the in-network `http://nakama:7350`). A host-mode game server inherits `.env`, and `NAKAMA_URL` unset is what **disables** its Nakama S2S integration. |
| `GATEWAY_ADDR` | `127.0.0.1:${GATEWAY_CONTAINER_PORT}` | `.env`'s value is the gateway's listen address, which also feeds the derivation of `GATEWAY_CONTAINER_PORT` and must keep matching the container's hardcoded `--addr=:8000`. |

`GATEWAY_CONTAINER_PORT` equals the listen port in host mode and is mapped onto it in
containers mode, so one expression is correct for both. The game-server hop needs no
override: the smoke test dials whatever `EnterWorldResponse.ServerAddr` carries, i.e.
`GAMESERVER_PUBLIC_ADDR`, which "Write environment file" already validates as dialable.

> Anything the smoke test reads that is left to its own default is only correct while the
> environment happens to use the default port. Dev and staging do; production does not, and
> was the first deploy to fail on it. The healthcheck in `deploy` does **not** cover this —
> it probes the *metrics* ports, which are forwarded per-environment, so it goes green
> while the client-facing path is unreachable.

**Artifact flow:** `bin-{gateway,gameserver,smoketest,verify-probe}-<sha>` + `nakama-plugin-<sha>`
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
| Registration | the game server self-registers (`REDIS_ADDR`) | the game server self-registers (`REDIS_ADDR`) |
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

### Two environments on one runner

Nothing stops one machine from carrying several of those labels, and today one
does. When it happens, the environments must be told apart or they will fight,
and **a different `RPG_DEPLOY_DIR` alone is not enough** — that moves the files
and leaves everything else shared. Six variables decide isolation:

| Variable | Default | Isolates |
|---|---|---|
| `COMPOSE_PROJECT_NAME` | the compose file's `name:` (`rpg-mmo-meta`) | the network and the **named volumes** — i.e. the postgres and redis *data* |
| `COMPOSE_NAME_PREFIX` | `rpg` | container names (`rpg-gateway`, `rpg-postgres`, …) |
| `RPG_DEPLOY_DIR` | `/opt/rpg-mmo` | binaries, `deploy/.env`, logs, backups |
| `GAME_DB_URL` | *(unset → in-memory store)* | which game database migrations run against |
| `REDIS_ADDR` | `localhost:6379` | which registry the game server publishes into |
| every `*_PORT` | compose's own defaults | what each service publishes on the host |

The port set is `POSTGRES_PORT`, `POSTGRES_GAME_PORT`, `REDIS_PORT`,
`NAKAMA_GRPC_PORT`, `NAKAMA_HTTP_PORT`, `NAKAMA_CONSOLE_PORT`,
`NAKAMA_METRICS_PORT`, `GATEWAY_CONTAINER_PORT`, `GATEWAY_METRICS_PORT`,
`GAMESERVER_CONTAINER_PORT`, `GAMESERVER_METRICS_PORT`, `GRAFANA_PORT`,
`PROMETHEUS_PORT`, `OTLP_GRPC_PORT`, `OTLP_HTTP_PORT`.

**Every default reproduces what is in use today**, so an environment that sets
none of these keeps behaving as it did. Two consequences worth stating outright:

- **Do not rename `COMPOSE_PROJECT_NAME` on a live environment.** Compose would
  no longer recognise the running containers or the volumes holding their data;
  the stack would come up empty beside the old one rather than replacing it.
- **The backup scripts follow `COMPOSE_NAME_PREFIX`.** `cd.yml` derives
  `META_CONTAINER`, `GAME_CONTAINER` and `REDIS_CONTAINER` from it. If they are
  ever set by hand, set them consistently — a prefix mismatch makes the pre-deploy
  dump target the *other* environment's databases and still report success, and the
  migration it exists to protect then runs with no usable checkpoint.

#### The reserved-identity registry and the preflight guard

The section above is advice, and advice is exactly what failed: `staging` was
created with **no** isolation variables at all, so it resolved to dev's
`RPG_DEPLOY_DIR`, dev's compose project, dev's container names and dev's ports.
Nothing in the pipeline objected. A staging deploy would have regenerated dev's
`deploy/.env` from staging's variables (silently dropping `ALLOCATOR=agones`,
which staging does not set) and taken dev's containers over.

Two things now stand between that and the stack:

1. **`backend/deploy/environments.tsv`** — one row per GitHub Environment,
   declaring the deploy directory, compose project, container prefix and every
   published port that environment is allowed to own. It is an *assertion over*
   the GitHub Environment variables, not a source of truth: changing a value
   here does not change a deploy. It exists because the variables themselves are
   invisible to code review, and this file is not.

2. **`backend/deploy/preflight-isolation.sh`**, run by the `deploy` job as
   **Isolation preflight**, immediately after the checkout and *before* the
   bundle sync — the first step that would otherwise write into the deploy
   directory. It fails the deploy when:
   - the resolved deploy dir / compose project / prefix / any port is reserved
     for a **different** row in `environments.tsv` (this needs nothing running,
     and is the check that catches "staging is configured exactly like dev");
   - the resolved values contradict this environment's **own** row (drift
     between the variables and the reviewed file, in either direction);
   - `$RPG_DEPLOY_DIR/deploy/.env` carries a `DEPLOY_ENVIRONMENT` stamp naming
     another environment (deploys now write that stamp);
   - a container named `<prefix>-<service>` exists under a **different** compose
     project — `up -d` would adopt it;
   - a port it is about to publish is already published by a container in
     another compose project, or bound by any non-docker process.

   What it **cannot** catch is listed at the bottom of the script itself and is
   worth reading before trusting it: an unlisted environment that is not
   currently running, two rows in `environments.tsv` edited to agree with each
   other but wrong, ports opened after the check (Agones fleet GameServers, a
   binary someone starts by hand), races between two simultaneous deploys, and
   anything on another host.

**Changing a port for an environment is therefore a two-sided edit**: the GitHub
Environment variable *and* its row in `environments.tsv`, in the same change.
Doing one without the other fails the next deploy loudly, which is the intent.

#### The three reserved port sets

Offsets are `dev` → `+10` production → `+20` staging, which keeps them readable.
`7000–7100` is deliberately skipped everywhere: k3d's serverlb publishes that
whole range for the Agones fleet.

| | dev | production | staging |
|---|---|---|---|
| `RPG_DEPLOY_DIR` | `/mnt/e/rpg-mmo-deploy` | `/mnt/e/rpg-mmo-deploy-prod` | `/mnt/e/rpg-mmo-deploy-staging` |
| `COMPOSE_PROJECT_NAME` | *(unset → `rpg-mmo-meta`)* | `rpg-mmo-prod` | `rpg-mmo-staging` |
| `COMPOSE_NAME_PREFIX` | *(unset → `rpg`)* | `rpg-prod` | `rpg-stg` |
| gateway / metrics | 8000 / 9102 | 8010 / 9112 | 8020 / 9122 |
| gameserver / metrics | 9200 / 9101 | 9210 / 9111 | 9220 / 9121 |
| postgres / postgres-game | 5432 / 5433 | 5442 / 5443 | 5452 / 5453 |
| redis | 6379 | 6389 | 6399 |
| nakama gRPC / HTTP / console / metrics | 7349 / 7350 / 7351 / 9100 | 7359 / 7360 / 7361 / 9110 | 7369 / 7370 / 7371 / 9120 |
| grafana / prometheus | 3001 / 9090 | 3002 / 9091 | 3003 / 9092 |
| OTLP gRPC / HTTP | 4317 / 4318 | 4327 / 4328 | 4337 / 4338 |

`dev` keeps the unset defaults on purpose: renaming `COMPOSE_PROJECT_NAME` on a
live stack orphans its containers **and its data volumes** (see the warning
above), and dev is the only proven Agones environment here.

#### `DEPLOY_MODE=host` isolates too, but through systemd

`scripts/deploy-local.sh` derives its unit names from `COMPOSE_NAME_PREFIX`
(`${prefix}-gateway.service`), because systemd unit names are global to the host
and a fixed `rpg-` prefix meant a host-mode staging deploy would restart dev's
units. Pidfiles and logs were already per-`RPG_DEPLOY_DIR`. Note the preflight
guard sees a pure host-mode environment only through its ports — it has no
containers to check.

`GAMESERVER_PUBLIC_ADDR` is not isolation but is easy to get wrong alongside it:
it is what the game server self-registers into Redis and what the gateway hands
back in `MsgEnterWorldResp`, so it must name a host and port a **client** can
reach — not the container port, and not a placeholder.

### Container images (GHCR)

The `build-images` job pushes `ghcr.io/cuvara/rpg-mmo-gateway` and
`ghcr.io/cuvara/rpg-mmo-gameserver` (tags: `<short-sha>` + `latest`) **only
when** the resolved environment is `production` **or** `workflow_dispatch` set
`build_images=true`. Auth is `docker/login-action@v3` with the built-in
`GITHUB_TOKEN` (`permissions: packages: write` on that job); layers are cached
with `type=gha`. Dev/staging deploys skip this entirely and keep using the host
binaries from the artifact bundle. No Agones manifest references a GHCR image any
more — the prod fleets were deleted with the Go server (see `K3S.md`), and the
only fleet, `agones/fleet-map-dotnet-dev.yaml`, uses the local tag
`rpg-mmo/gameserver-dotnet:dev`.

> **`cd.yml` should pass `--build-arg GIT_REVISION=$(git rev-parse HEAD)` to the
> gameserver image build.** `docker/Dockerfile.gameserver-dotnet` stamps it into
> `org.opencontainers.image.revision`, which is what makes "was this image built
> from the commit under test?" answerable — a question that has already been
> answered wrongly once (see `K3S.md`, "Deploying the dotnet fleet"). Without the
> arg the label reads `unknown` and `validate-manifests.py --check-image` fails,
> which is the intended behaviour, not a bug. The workflow lives outside
> `deploy/` and has not been changed here.

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

| `kubectl` (k8s mode), `python3`, `bash` >= 4 | `dev-up.sh` and the post-deploy verification suite. |

Go and the .NET SDK are **not** needed — only prebuilt binaries land there, and
that is a constraint on the workflow, not just an observation about the box.

**Anything the `deploy` job needs to run must be built by an `ubuntu-latest` job
and travel in the bundle.** The k8s post-deploy verification broke this twice at
once: `verify.sh` built `verify/probe` on demand and `verify/lib/checks_flow.sh`
built `backend/smoketest` on demand, both with a bare `go build`. On the dev
runner Go is not on the default non-login PATH, so CD run 32207995779 reported
`data.nakama_plugin` FAIL ("probe build failed"), `flow.smoke` FAIL ("go: command
not found") and `flow.stack_identity` SKIP. The fix is the bundle, not
`actions/setup-go` on the deploy job: a compiler there would produce binaries no
CI job had gated, and would make the apply-artifacts-to-a-cluster job depend on a
toolchain it has no reason to carry. The verification step now exports
`VERIFY_SMOKETEST_BIN=$GITHUB_WORKSPACE/dist/bin/smoketest` and
`VERIFY_PROBE_BIN=$GITHUB_WORKSPACE/dist/bin/verify-probe`, and fails the step
with `::error::` if the bundle is missing either — a pipeline defect must not be
reported as a broken deployment.

That fix left one gap, now closed: building the probe in CD made a deploy the
first place a non-compiling probe could surface, on a change already merged.
`ci.yml` gates it at PR time as `test-verify-probe`. The module has no test
files, so that job runs `go vet` and a build and deliberately does **not** run
`go test` — a green `[no test files]` is the ambiguous "passed while asserting
nothing" signal this pipeline already refuses elsewhere. The probe links against
`backend/shared` through a `replace` directive, so a wire-format change can break
it there and nowhere else in CI.

### 4a. The `dev` runner is WSL, and `docker` there is a shim

**Read this before debugging a failed `dev` deploy.** It is the least obvious
thing about this pipeline and it is not visible from any workflow file.

The `dev` runner is a WSL2 Ubuntu distro on a Windows box with Docker Desktop.
**Docker Desktop's WSL integration is DISABLED for this distro**, so the
Linux-native Docker CLI cannot reach a daemon:

```
$ ls -l /usr/bin/docker
/usr/bin/docker -> /mnt/wsl/docker-desktop/cli-tools/usr/bin/docker

$ /usr/bin/docker version
Cannot connect to the Docker daemon at unix:///var/run/docker.sock. Is the docker daemon running?

$ curl --unix-socket /var/run/docker.sock http://localhost/_ping
curl: (56) Recv failure          # the socket file exists but nothing is listening
```

The fix in place is a two-line shim that forwards to the Windows CLI:

```sh
# /usr/local/bin/docker   (mode 0755)
#!/bin/sh
exec docker.exe "$@"
```

It works because the runner's `PATH` (`~/actions-runner/.path`, captured at
`svc.sh install` time) lists `/usr/local/bin` **before** `/usr/bin`:

```
… :/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin: …
$ PATH="$(cat ~/actions-runner/.path)" command -v docker
/usr/local/bin/docker
```

Notes that will save you time:

- The `.path` file is a **snapshot**. Changing your interactive shell's `PATH`
  does not change the runner's. There is a second, identical shim at
  `~/cuongnd/bin/docker` for interactive use; `~/bin` is **not** on the runner's
  `PATH`, which is exactly why the `/usr/local/bin` one had to be added.
- Because `/usr/local/bin` wins, **the shim shadows `/usr/bin/docker`
  unconditionally.** Enabling WSL integration later would change nothing on its
  own — see [§4b](#4b-why-we-have-not-enabled-wsl-integration).
- A CD deploy failed on exactly this after a reboot, before the shim existed.

#### The path-translation rule this forces on every script

`docker.exe` is a Windows binary. It does not understand Linux absolute paths,
and — critically — **it does not reject them.** It resolves them against the
current drive:

```
$ docker.exe compose -f /mnt/e/SecretProject/rpg-mmo-server/backend/deploy/docker-compose.yml ps
open E:\mnt\e\SecretProject\rpg-mmo-server\backend\deploy\docker-compose.yml: The system cannot find the path specified.
```

For `-f` that is a loud error. **For a bind mount it is silent:**

```
$ docker.exe run --rm -v /mnt/e/SecretProject/rpg-mmo-server/backend/deploy:/x alpine ls /x
$ echo $?
0                        # exit 0, and /x is an EMPTY directory
```

Docker Desktop creates the nonexistent `E:\mnt\e\…` on the Windows side and
mounts *that*. The container sees an empty directory and the command succeeds.
This is the same failure shape as the Redis restore bug in
[`DISASTER-RECOVERY.md`](DISASTER-RECOVERY.md#4-the-bug-the-drill-found---mode-live-restored-nothing-and-said-done):
a wrong result reported as success.

**`$PWD` is an absolute path, so it is affected too** — this is the trap most
likely to bite someone:

```
$ cd backend/deploy
$ docker.exe run --rm -v "$PWD:/x" alpine sh -c 'ls /x | wc -l'
0                        # silently empty
$ docker.exe run --rm -v ".:/x"   alpine sh -c 'ls /x | wc -l'
12                       # correct
```

So the rule is narrower than "keep paths cwd-relative": **the mount source must
be a literal relative path (`.`, `./monitoring`), never `$PWD` or `$(pwd)`.**

Current state — verified 2026-08-06, nothing in the repo violates this:

| Consumer | Why it is safe |
|---|---|
| `docker-compose.yml` | Every bind mount is relative (`./db/…`, `./modules`, `./monitoring/…`); `cd.yml` `cd`s into `$RPG_DEPLOY_DIR/deploy` first |
| `db/backup.sh`, `db/restore.sh`, `db/redis-backup.sh`, `db/redis-restore.sh` | Move bytes through `docker exec` / `docker run -i` **stdio**, never a host bind mount. Where they do use `-v`, the source is a **named volume**, which is immune to path translation |
| `scripts/build-all.sh`, all four `db/` scripts | Ship a `detect_docker()` that tries `docker` then `docker.exe`, so they work on the VPS and under WSL without the shim |
| `cd.yml` image builds | `working-directory: backend` + relative `-f deploy/docker/…` |

Bind mounts were confirmed live, not just read: `prometheus.yaml` (3917 B),
`rpg-dashboards.yaml` (518 B), the dashboards dir and `nakama.so` (18 MB) are
all present *inside* the running containers, so nothing silently mounted empty.

None of this applies to a real Linux VPS, where `docker` is `docker` and
absolute paths are absolute paths. The constraint is WSL-dev-only and costs
nothing to keep.

### 4b. Why we have not enabled WSL integration

Docker Desktop can expose a working `/var/run/docker.sock` inside this distro
(Settings → Resources → WSL integration → toggle `Ubuntu`). That would retire
the shim and the path-translation rule. **Recommendation: not now.** Evidence:

| | |
|---|---|
| **What it would fix** | The silent-empty-mount landmine above, permanently. That is the whole benefit, and it is a latent risk, not a live bug — no current code trips it |
| **Speed** | Not an argument. `docker.exe` costs **~85 ms per invocation** (measured, 10× `docker version`) vs ~25 ms native. Across a CD run that is a few seconds |
| **Bind-mount throughput** | Unchanged. The repo and `$RPG_DEPLOY_DIR` both live on `/mnt/e`, a Windows NTFS drive. Container I/O against it crosses the same 9p/drvfs boundary either way |
| **The toggle alone does nothing** | `/usr/local/bin` precedes `/usr/bin` in the runner's frozen `.path`, so `docker` keeps resolving to the shim. Switching is a **two-step** change: flip the toggle **and** remove the shim. Flipping only the toggle produces no observable difference, which is a great way to conclude it "did not work" |
| **Blast radius** | Every deploy path changes daemon endpoint at once: image builds, `compose up`, the health probes, the four `db/` scripts, and the Agones/k8s containers currently running on Docker Desktop's Kubernetes. All of it is re-validated only by running a real deploy |
| **Timing** | G1 (self-registration) is mid-flight and the security change has just landed. Swapping the container runtime plumbing underneath that, for a latent-risk fix worth a few seconds per deploy, is a bad trade |

**Revisit when** someone actually needs an absolute host bind mount, or when the
`dev` runner moves off WSL. Until then the cost of staying is one documented
shim and one rule (`.` not `$PWD`), both now written down here instead of living
in one person's head.

If we do switch, the procedure and its rollback:

```bash
# 1. Docker Desktop → Settings → Resources → WSL integration → enable "Ubuntu" → Apply & restart
#    (GUI, user action — cannot be scripted from here)

# 2. Verify the socket is actually live BEFORE touching the shim
curl --unix-socket /var/run/docker.sock http://localhost/_ping   # want: OK
/usr/bin/docker version --format '{{.Server.Version}}'           # want: a version, not an error

# 3. Only then retire the shim (keep a copy — this is the rollback)
sudo mv /usr/local/bin/docker /usr/local/bin/docker.shim.bak
PATH="$(cat ~/actions-runner/.path)" command -v docker           # want: /usr/bin/docker

# 4. Re-run a full dev deploy and re-verify the bind mounts landed non-empty:
docker exec rpg-lgtm ls -l /otel-lgtm/prometheus.yaml
docker exec rpg-nakama ls -l /nakama/data/modules/nakama.so

# ROLLBACK (any step fails):
sudo mv /usr/local/bin/docker.shim.bak /usr/local/bin/docker
#   The shim is self-contained and needs no daemon-side state, so restoring it
#   is instant and does not depend on the toggle being flipped back.
```

Do **not** delete the shim before step 2 passes: with the toggle off and the
shim gone, the runner has no working `docker` at all and every deploy fails.

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
`JWT_SECRET`, `JOIN_TOKEN_SECRET`, `POSTGRES_PASSWORD`, `NAKAMA_CONSOLE_PASSWORD` — plus
`GRAFANA_ADMIN_PASSWORD` whenever `vars.MONITORING_ENABLED` is not exactly
`false`, because a Grafana published with a default password is an open door.
It also fails when `JOIN_TOKEN_SECRET` equals `JWT_SECRET`: the split exists so a
compromised game server cannot mint client auth tokens, and one shared value
silently gives that up.

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

### 6b. Which workflow gates which PR

In practice feature branches PR into **`develop`**, not `main` — the diagram
above describes the intended trunk flow, not what the team does day to day. That
mismatch is what hid the following bug until 2026-08-06:

> `ci.yml` listed only `[main, master]` under `pull_request`, so **every PR into
> `develop` reported no checks at all**. `gh pr checks <n>` answered "no checks
> reported on the ... branch", which is visually easy to mistake for "nothing
> failing". Go changes merged into `develop` with zero CI for the life of the
> project.

Current, after the fix:

| Workflow | Runs on PR into | Path filter on PR | What it proves |
|---|---|---|---|
| `ci.yml` (Go) | `main`, `master`, `develop`, `staging` | **none** — every PR runs it | shared/gateway/nakama/smoketest/integration compile + unit tests pass; gateway and smoketest binaries build |
| `ci-dotnet.yml` (C#) | `main`, `master`, `develop`, `staging` | **none** — every PR runs it | `gameserver-dotnet` restores, builds, and its xUnit suite passes |
| `cd.yml` | never — `push` only, to `develop` / `staging` / `release-*` | n/a | deployment; **not** a PR gate |

Two deliberate choices:

- **No `paths:` filter on `pull_request`.** A path-filtered workflow that does
  not match never runs, and GitHub then reports the PR as having no checks —
  indistinguishable at a glance from a passing PR, and a permanent block if a
  required status check is ever added to branch protection. Every PR into a
  protected branch therefore runs the full suite, including docs-only PRs. A few
  minutes of hosted-runner time is cheaper than one silently ungated merge. The
  `push` triggers keep their filters.
- **`cd.yml` is not a PR gate and cannot become one.** It runs on the
  self-hosted runner and needs Docker on that machine; a paused daemon makes it
  fail with `[backup] ERROR: docker not available (tried docker, docker.exe)`.
  That is correct fail-fast behaviour for a deploy, but it must never be what
  stands between a PR and a merge. PR gating is GitHub-hosted only.

#### `Publish AOT` verifies its prerequisites, it does not install them

The `Publish AOT` job in `ci-dotnet.yml` needs `clang` and the zlib development
headers to link the NativeAOT binary. It used to get them with an unconditional
`sudo apt-get update && sudo apt-get install -y clang zlib1g-dev`.

**Both are already on the `ubuntu-24.04` runner image**, so that step installed
nothing of substance — the image ships Clang 16/17/18 with an unversioned
`clang` alternative, and job logs show `zlib1g-dev is already the newest
version` with `0 upgraded, 1 newly installed` (the `clang` metapackage, 20.5 kB).
What it did do is contact the Ubuntu mirrors on every run. On 2026-08-19 that
`apt-get update` hung four times — twice for 15-25 minutes, against the job's
`timeout-minutes: 25` — and produced red Xs on PRs #169, #171 and #172 that had
nothing to do with the code under test.

The step is now a **probe**: `command -v clang` plus a `dpkg-query -s
zlib1g-dev` / `/usr/include/zlib.h` check. On the common path it does no network
I/O at all. It falls back to `apt-get` (3 attempts, backoff) only for what is
genuinely missing, and it exits 1 with an explicit `::error::` if a prerequisite
is still absent afterwards — a future image that drops Clang must fail here with
a clear message, not as an obscure link error inside `dotnet publish`. The step
carries `timeout-minutes: 5`, so a mirror hang costs five minutes instead of the
job's entire budget.

**What did not change:** every step after it. Per ADR-11 decision 4 a clean
NativeAOT publish does *not* imply a working binary — Arch allocates chunk
backing arrays through `System.Array.CreateInstance(Type, int)`, so an
un-hinted component publishes without warning and throws `NotSupportedException`
on the first archetype creation, i.e. the first player spawn. The job's
smoke-run therefore performs a **real join**, not a start-and-ping, and must
stay unconditional.

#### Known gap: wire-compat coverage

The Go gateway and the C# game server share a wire protocol
(`backend/shared/messages/` ⇄ `Shared.GameLogic`). Dropping the `paths:` filter
means a Go-side wire change now also runs the C# suite — but **a green
`ci-dotnet.yml` does not prove wire compatibility.** Those tests exercise C# in
isolation and never observe the Go encoder, so a mismatched field name or tag
passes both suites independently.

The only check that catches a real wire break is the cross-language E2E suite in
`backend/integration_test`, which needs both binaries running together. Today it
executes only inside `cd.yml` on push, so **a wire break is caught after merge,
at deploy time, not on the PR.** Closing this properly means running the
integration suite on GitHub-hosted runners as a PR gate, which needs the C#
gameserver published in-workflow. Not attempted here — it is a separate change
with its own failure modes.

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
`GAMESERVER_PUBLIC_ADDR`: it defaults to a listen-style `:9200`, which is handed
to clients verbatim and is not dialable by them. Set it to
`<public-host-or-ip>:<port>`.

---

## 9. Known limits / unverified

- GitHub Actions cannot be executed locally; `cd.yml` is syntax-validated only.
  The `resolve` → `deploy` label plumbing (`fromJSON`) and the Environment secret
  wiring need one real run per environment to confirm.
- The `deploy` job assumes `docker compose` v2 and a Linux runner.
- Host mode still healthchecks with a TCP connect. Both binaries do serve
  `/healthz` on their metrics port now (containers mode uses it) — host mode
  could be upgraded the same way.
- Containers mode is verified on the dev runner. On a real VPS,
  `bootstrap-vps.sh` itself has only been exercised via `--dry-run` plus
  `bash -n` — the apt/runner/ufw paths need one real box to confirm.
- No blue/green or drain step — `deploy-local.sh restart` is a hard restart and
  drops in-flight realtime connections. Acceptable at Dev/Beta tier; revisit at
  Soft Launch (Agones handles this for game servers).
