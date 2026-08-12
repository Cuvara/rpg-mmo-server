# VPS Setup — bringing a new machine online

Start: a bare Ubuntu VPS you can SSH into.
Finish: pushing to a branch deploys the whole backend to it, verified by an
automated smoke test.

Two commands do the work — one on the VPS, one against GitHub:

```bash
# on the VPS
sudo RUNNER_TOKEN=<token> ./scripts/bootstrap-vps.sh --labels staging

# anywhere with the gh CLI
./scripts/setup-github-env.sh staging --generate
```

Everything below explains those two, what they leave behind, and how to check
it worked. **No code changes are involved in adding a machine or an
environment** — see §5.

Related docs: `CICD.md` (pipeline internals, job graph, rollback),
`MONITORING.md` (Grafana/Prometheus), `RUNBOOK-local-dev.md` (laptop stack),
`K3S.md` (the later Agones/k3s tier).

---

## Prerequisites

| What | Detail |
|------|--------|
| **VPS** | Ubuntu 22.04 or 24.04, x86_64 or arm64, root/sudo access. 2 vCPU / 4 GB RAM is comfortable for the dev-alpha tier (Postgres ×2 + Redis + Nakama + Grafana stack + two game processes). |
| **Repo access** | Admin on the GitHub repository — needed to register a runner and to create Environments. |
| **`gh` CLI** | On your workstation, authenticated: `gh auth login`. Not needed on the VPS. |
| **Runner registration token** | Short-lived (1 hour), single-use. See below. |
| **Domain / TLS** | Optional. Nothing here requires one; you will want one before exposing Grafana or Nakama publicly. |
| **Open ports** | The bootstrap script configures ufw. If your provider has its own firewall (Hetzner, AWS SG, …), mirror the rules there — a cloud firewall sits in front of ufw and will silently drop traffic ufw allows. |

### Getting the runner registration token

UI: **repo → Settings → Actions → Runners → New self-hosted runner**. The token
appears in the `./config.sh --token …` line of the shown snippet.

CLI (same thing, scriptable):

```bash
gh api -X POST repos/<owner>/<repo>/actions/runners/registration-token --jq .token
```

It expires after an hour, so fetch it immediately before running the bootstrap.

---

## 1. Bootstrap the VPS

Copy the repo (or just the script) to the box and run it as root:

```bash
git clone https://github.com/<owner>/rpg-mmo-server.git
cd rpg-mmo-server

sudo RUNNER_TOKEN=$(…) ./scripts/bootstrap-vps.sh --labels staging
```

Preview first if you like — `--dry-run` prints every action and changes nothing,
and does not even require root:

```bash
./scripts/bootstrap-vps.sh --dry-run --skip-runner
```

### What it does

Every step is idempotent; re-running is safe.

1. **Docker CE + compose plugin** from Docker's official apt repository, service
   enabled. Skipped if a working `docker compose` is already present.
2. **Deploy user + directory** — creates the user (default `rpg`), adds it to
   the `docker` group, and creates `$RPG_DEPLOY_DIR` with the
   `bin/ deploy/ scripts/ run/ logs/` layout the deploy job expects.
3. **GitHub Actions runner** — downloads the release, runs
   `installdependencies.sh`, registers `--unattended --replace` with the labels
   you chose, then `svc.sh install` + `svc.sh start` so it runs as a systemd
   service surviving reboot and logout. The token is passed to one command and
   never written to disk.
4. **ufw** — default deny incoming, allow outgoing; SSH plus the gateway and
   game server ports on **tcp and udp** (udp is reserved for the KCP transport,
   opened now so the firewall needs no second visit). Grafana is **denied**;
   `--admin-ip` switches it to a single-IP allowlist. It also writes the
   matching `DOCKER-USER` iptables rule, because Docker's published ports bypass
   ufw's INPUT chain entirely — a `ufw deny` alone does *not* close a container
   port.

### Flags

Each has an environment-variable equivalent; the flag wins.

| Flag | Env | Default | Meaning |
|------|-----|---------|---------|
| `--runner-token` | `RUNNER_TOKEN` | *(required)* | Registration token. Not needed with `--skip-runner`. |
| `--labels` | `RUNNER_LABELS` | `staging` | Runner labels **without** `self-hosted` (added automatically). This is what binds the machine to an environment — see below. |
| `--repo-url` | `REPO_URL` | this repo | Repository to register against. |
| `--runner-name` | `RUNNER_NAME` | `rpg-<label>-<hostname>` | Display name in the runners list. |
| `--runner-version` | `RUNNER_VERSION` | `2.321.0` | actions/runner release. |
| `--deploy-user` | `DEPLOY_USER` | `rpg` | Service account; owns the deploy dir, member of `docker`. |
| `--deploy-dir` | `RPG_DEPLOY_DIR` | `/opt/rpg-mmo` | Must match the `RPG_DEPLOY_DIR` variable in §2. |
| `--gateway-port` | `GATEWAY_PORT` | `8000` | Opened tcp + udp. |
| `--gameserver-port` | `GAMESERVER_PORT` | `9200` | Opened tcp + udp. |
| `--ssh-port` | `SSH_PORT` | `22` | Opened tcp. |
| `--grafana-port` | `GRAFANA_PORT` | `3000` | **Denied** unless `--admin-ip` is given. |
| `--admin-ip` | `ADMIN_IP` | *(empty)* | Allow Grafana from this address only. |
| `--skip-docker` / `--skip-runner` / `--skip-firewall` / `--skip-user` | — | off | Skip that step. |
| `--dry-run` | `DRY_RUN=1` | off | Print actions, execute nothing. |

### Choosing labels

The label **is** the routing: `cd.yml` maps a branch to an environment name, and
runs the deploy job on `["self-hosted", "<environment>"]`.

| Branch | Environment | Runner needs the label |
|--------|-------------|------------------------|
| `develop` | `dev` | `dev` |
| `staging` | `staging` | `staging` |
| `release-*` | `production` | `production` |

So `--labels staging` makes the box the staging target. A custom environment
(`--labels qa`) works for `workflow_dispatch` deploys once the `resolve` job
knows the name — it currently accepts `dev`, `staging`, `production`, and
rejects anything else loudly, so adding a fourth means editing `resolve` in
`cd.yml`.

One machine can carry several labels (`--labels "staging,qa"`) — but read the
port caveat in §5 before doing it.

---

## 2. Create the GitHub Environment

The pipeline reads its configuration from a GitHub **Environment** matching the
runner label: secrets for anything sensitive, variables for everything else.

### One command

```bash
./scripts/setup-github-env.sh staging --generate
```

That creates the Environment if missing and sets **every** secret and variable
in the two tables below — including the ones that have workflow defaults, so the
environment is self-documenting instead of depending on a default you have to
read the YAML to discover.

`--generate` invents a strong random value for every secret. Without it the
script prompts for each (input hidden), offering dev defaults. `--dry-run`
prints the exact `gh` commands without running them. Secret values are passed to
`gh` on stdin, never in argv — argv is world-readable via `/proc`.

For `production` the script switches to **strict mode** automatically: every
secret must be ≥ 32 characters and must not contain a placeholder like
`dev-secret`, `localdev`, `password`, `changeme`, `defaultkey`. It refuses
rather than publishing a weak production secret. `--strict` forces the same
rules on any environment.

Useful variants:

```bash
# non-default paths/ports, game DB wired up, Grafana on loopback
./scripts/setup-github-env.sh staging --generate \
  --deploy-dir /srv/rpg --deploy-mode containers \
  --gameserver-public-addr game.example.com:9200 \
  --game-db-url 'postgres://game:<pw>@localhost:5433/gamestate?sslmode=disable'

# see everything it would do
./scripts/setup-github-env.sh production --generate --dry-run --non-interactive
```

### Secrets — the complete list

Authoritative source is `cd.yml`; regenerate this list any time with:

```bash
grep -oE 'secrets\.[A-Z_]+' .github/workflows/cd.yml | sort -u
```

| Secret | Required | Example | Meaning |
|--------|----------|---------|---------|
| `JWT_SECRET` | **yes** — deploy fails if empty | 48 random chars | HS256 secret shared by Nakama, gateway and game server. Nakama signs client session tokens with it, so the gateway verifies them locally with no roundtrip. Changing it invalidates every live session. |
| `JOIN_TOKEN_SECRET` | **yes** — deploy fails if empty, or if it equals `JWT_SECRET` | 48 random chars | HS256 secret the gateway signs gateway→gameserver join tokens with and every game server verifies them with. Separate from `JWT_SECRET` on purpose: a compromised game-server pod must not be able to forge client auth tokens. Rotate with a comma-separated `new,old` list. |
| `POSTGRES_PASSWORD` | **yes** | 48 random chars | Nakama meta DB password. Changing it on an existing volume does **not** re-key Postgres — the old password keeps working. |
| `NAKAMA_CONSOLE_PASSWORD` | **yes** | 48 random chars | Login for the Nakama admin console (port 7351). |
| `GRAFANA_ADMIN_PASSWORD` | **yes when `MONITORING_ENABLED != false`** | 48 random chars | Grafana admin password. Only applied when Grafana *creates* its admin user, i.e. on an empty `grafana.db` — see `MONITORING.md` for the rotation procedure. |
| `NAKAMA_SERVER_KEY` | no (defaults to `defaultkey`) | 48 random chars | Server key the game client presents to Nakama. Change it outside dev; the Unity client must use the same value. |
| `REDIS_PASSWORD` | no (empty = no auth) | 48 random chars | Enables `--requirepass` on Redis. Gateway and game server pick it up from the same generated `.env`. |

`GITHUB_TOKEN` also appears in `cd.yml` — that one is injected by Actions and is
not something you set.

### Variables — the complete list

```bash
grep -oE 'vars\.[A-Z_]+' .github/workflows/cd.yml | sort -u
```

**Deployment shape**

| Variable | Default | Meaning |
|----------|---------|---------|
| `RPG_DEPLOY_DIR` | `/opt/rpg-mmo` | Where the bundle is installed. Must match `bootstrap-vps.sh --deploy-dir`. |
| `DEPLOY_MODE` | `host` | `containers` runs gateway + game server as Docker containers (recommended for a VPS); `host` runs the bundled binaries under `deploy-local.sh`. Anything else fails the deploy loudly. See `CICD.md` §3b. |
| `GATEWAY_ADDR` | `:8000` | Gateway listen address. |
| `GAMESERVER_ADDR` | `:9000` | Game server listen address. Set it to `:9200` to match the port `bootstrap-vps.sh` opens by default. |
| `GAMESERVER_MAP_ID` | `map_01` | Map this server hosts. |
| `GAMESERVER_PUBLIC_ADDR` | `:<gameserver port>` | **The address handed to clients verbatim** in `MsgEnterWorldResp.ServerAddr`, so it must be one they can dial. The default is listen-style (hostless), which only clients that rewrite it to their own loopback will accept at all — the Go smoketest does, a C# `TcpClient` throws — so it is correct only for host-mode deploys. On a VPS this must be `<public-host-or-ip>:<port>`. This is the single most common misconfiguration. |
| `GATEWAY_CONTAINER_PORT` / `GAMESERVER_CONTAINER_PORT` | port of the matching `*_ADDR` | Host ports the containers publish (containers mode). |
| `REDIS_ADDR` | `localhost:6379` | Redis as the *host processes* see it. Containers use the in-network name automatically. |
| `GAME_DB_URL` | *(empty)* | Game-state Postgres DSN. **Empty keeps the in-memory player store — progress is lost on restart**, and the `db-migrate` job skips itself. Point it at the `postgres-game` service as the host sees it, e.g. `postgres://game:localdev@localhost:5433/gamestate?sslmode=disable`. |

**Backing services**

| Variable | Default | Meaning |
|----------|---------|---------|
| `NAKAMA_VERSION` | `3.40.0` | Nakama server image tag. Must match the pluginbuilder tag — Go plugins are ABI-locked. |
| `POSTGRES_DB` / `POSTGRES_USER` | `nakama` / `nakama` | Meta DB name and user. |
| `NAKAMA_CONSOLE_USER` | `admin` | Console username. |

**Monitoring** (full treatment in `MONITORING.md`)

| Variable | Default | Meaning |
|----------|---------|---------|
| `MONITORING_ENABLED` | `true` | `false` deploys without the Grafana/Prometheus container and removes a running one. |
| `GRAFANA_USER` | `admin` | Grafana admin username. |
| `GRAFANA_PORT` / `GRAFANA_BIND` | `3000` / `0.0.0.0` | **Set `GRAFANA_BIND=127.0.0.1` on any public VPS** and reach it with `ssh -L 3000:127.0.0.1:3000 rpg@<vps>`. The default publishes a login page to the whole internet. |
| `GRAFANA_ANONYMOUS` | `false` | Leave off. The upstream image enables anonymous **Admin** access when this is unset. |
| `PROMETHEUS_PORT` / `PROMETHEUS_BIND` | `9090` / `127.0.0.1` | Unauthenticated — keep on loopback. |
| `OTLP_GRPC_PORT` / `OTLP_HTTP_PORT` / `OTLP_BIND` | `4317` / `4318` / `127.0.0.1` | Unauthenticated — keep on loopback. |
| `OTEL_LGTM_VERSION` | `0.11.15` | `grafana/otel-lgtm` image tag. |
| `GATEWAY_METRICS_PORT` / `GAMESERVER_METRICS_PORT` | `9102` / `9101` | Published `/metrics` + `/healthz` ports. |
| `GAMESERVER_METRICS_ADDR` | `:9101` | Listen address of the C# metrics endpoint inside its container. The wildcard answers on any `Host`, so both the in-network scrape and a host-side probe work. |

**Database backup** (used by the `db-migrate` job)

| Variable | Default | Meaning |
|----------|---------|---------|
| `BACKUP_DIR` | `$RPG_DEPLOY_DIR/backups` | Where `pg_dump` output lands. `/var/backups` is root-only on a stock host and CD does not run as root. |
| `BACKUP_KEEP` | `7` | Dumps retained per database. |

### Doing it by hand

If you would rather not run the script, this is the equivalent (the script adds
validation, prompting and generation on top):

```bash
REPO=<owner>/rpg-mmo-server
ENV=staging

gh api -X PUT "repos/$REPO/environments/$ENV" --silent

for s in JWT_SECRET JOIN_TOKEN_SECRET POSTGRES_PASSWORD NAKAMA_CONSOLE_PASSWORD GRAFANA_ADMIN_PASSWORD; do
  openssl rand -base64 48 | tr -d '\n=+/' | cut -c1-48 \
    | gh secret set "$s" --env "$ENV" --repo "$REPO"
done

gh variable set RPG_DEPLOY_DIR         --env "$ENV" --repo "$REPO" --body '/opt/rpg-mmo'
gh variable set DEPLOY_MODE            --env "$ENV" --repo "$REPO" --body 'containers'
gh variable set GATEWAY_ADDR           --env "$ENV" --repo "$REPO" --body ':8000'
gh variable set GAMESERVER_ADDR        --env "$ENV" --repo "$REPO" --body ':9200'
gh variable set GAMESERVER_PUBLIC_ADDR --env "$ENV" --repo "$REPO" --body 'game.example.com:9200'
gh variable set GAMESERVER_MAP_ID      --env "$ENV" --repo "$REPO" --body 'map_01'
gh variable set REDIS_ADDR             --env "$ENV" --repo "$REPO" --body 'localhost:6379'
gh variable set GAME_DB_URL            --env "$ENV" --repo "$REPO" \
  --body 'postgres://game:localdev@localhost:5433/gamestate?sslmode=disable'
gh variable set MONITORING_ENABLED     --env "$ENV" --repo "$REPO" --body 'true'
gh variable set GRAFANA_BIND           --env "$ENV" --repo "$REPO" --body '127.0.0.1'
```

For `production`, also add protection rules (required reviewers, branch
restriction to `release-*`) under **Settings → Environments → production**.

---

## 3. First deploy

Either push the branch that maps to the environment:

```bash
git push origin staging
```

…or dispatch any ref at any environment:

```bash
gh workflow run cd.yml --ref staging -f environment=staging
gh run watch $(gh run list --workflow=cd.yml --limit 1 --json databaseId -q '.[0].databaseId')
```

### What to expect, job by job

Everything up to `bundle` runs on GitHub's hosted runners; from `db-migrate`
onward it runs on **your** machine.

| Job | Where | What it means if it fails |
|-----|-------|---------------------------|
| `resolve` | hosted | The ref or dispatch input did not map to a known environment. |
| `test-shared` → `test-gateway` / `test-nakama` / `test-smoketest` | hosted | Go tests. Nothing to do with your VPS. |
| `test-integration` | hosted | Gateway ↔ game server E2E. |
| `build-gateway` / `build-smoketest` / `build-plugin` | hosted | Binaries + the Nakama plugin `.so` (built inside the matching Nakama image — the plugin ABI is version-locked). |
| `build-images` | hosted | Only for production or `build_images=true`; pushes to GHCR. Not on the deploy path. |
| `bundle` | hosted | Assembles binaries + compose file + monitoring/db configs into one artifact, and NativeAOT-publishes the C# game server. Asserts every expected file is present. |
| `db-migrate` | **your VPS** | `pg_dump`s both databases (skipping any that do not exist yet), then applies pending game-state migrations. Skips itself entirely when `GAME_DB_URL` is empty. A checksum drift on an already-applied migration fails here. |
| `deploy` | **your VPS** | Installs the bundle, writes `deploy/.env` from your secrets (mode 0600, never echoed), brings the compose stack up, then either starts the host binaries or the containers, registers the game server in Redis, and health-probes everything. |
| `post-deploy-smoke` | **your VPS** | The real acceptance test: Nakama health → device auth → `gateway_token` RPC → gateway `MsgAuth`/`MsgEnterWorld` → game server join → input/snapshot → disconnect. A failure here means the stack is up but the game flow is broken — most often `GAMESERVER_PUBLIC_ADDR`. |
| `summary` | hosted | Always runs; prints ref, commit, runner, deploy dir and mode. |

First deploy is the slow one — image pulls plus a NativeAOT build. Later ones
reuse Docker layer cache.

### What lands on the machine

```
$RPG_DEPLOY_DIR/                       # default /opt/rpg-mmo
  COMMIT                               # deployed git SHA
  bin/
    gateway  gameserver-dotnet  smoketest
    *.prev                             # previous binary, for a manual rollback
  deploy/
    docker-compose.yml  Makefile  .env.example
    .env                               # generated by CD, mode 0600 — secrets live here
    modules/nakama.so                  # Nakama Go plugin, bind-mounted
    monitoring/                        # prometheus.yaml + Grafana dashboards
    db/                                # init SQL, migrations/, backup.sh, restore.sh
  scripts/
    deploy-local.sh
  backups/{meta,gamestate}/            # pg_dump output, BACKUP_KEEP retained
  run/*.pid  logs/*.log                # host mode only
```

Docker volumes (`postgres-data`, `postgres-game-data`, `redis-data`,
`lgtm-data`) hold the actual state and are **not** touched by a deploy.

---

## 4. Verify

Run these on the VPS after the first green pipeline.

**Containers** — expect `rpg-postgres`, `rpg-postgres-game`, `rpg-redis`,
`rpg-nakama`, `rpg-lgtm`, plus `rpg-gateway` and `rpg-gameserver` in containers
mode:

```bash
docker ps --filter name=rpg- --format 'table {{.Names}}\t{{.Status}}\t{{.Ports}}'
```

**Health endpoints:**

```bash
curl -fsS http://127.0.0.1:7350/healthcheck && echo ' nakama ok'
curl -fsS http://127.0.0.1:9102/healthz     && echo ' gateway ok'

curl -fsS http://127.0.0.1:9101/healthz     && echo ' gameserver ok'
```

**Metrics and Grafana:**

```bash
curl -s http://127.0.0.1:9090/api/v1/query?query=up | jq -r '.data.result[]|"\(.metric.job) up=\(.value[1])"'
# gateway-container and gameserver-container should read up=1 in containers mode.

curl -s -o /dev/null -w '%{http_code}\n' http://127.0.0.1:3000/api/org   # want 401, NOT 200
```

A `200` on that last one means anonymous admin access is on — fix
`GRAFANA_ANONYMOUS` before doing anything else.

**Game flow, by hand** (the same binary the pipeline runs):

```bash
cd "$RPG_DEPLOY_DIR"
set -a; . deploy/.env; set +a
./bin/smoketest
# -> five PASS lines and SMOKE=PASS
```

**Database state:**

```bash
# migrations applied
docker exec rpg-postgres-game psql -U game -d gamestate \
  -c 'select version, name, applied_at from schema_migrations order by version;'

# player rows written by the smoke run
docker exec rpg-postgres-game psql -U game -d gamestate \
  -c 'select user_id, map_id, x, y, hp, updated_at from player_states order by updated_at desc limit 5;'
```

If `player_states` stays empty while the smoke test passes, `GAME_DB_URL` is
unset and the server is on the in-memory store — check the game server log for
`using in-memory player store`.

**Registry entry** (what clients are told to dial):

```bash
docker exec rpg-redis redis-cli HGETALL "servers:id:gs-dotnet-map_01"
# the `addr` field must be reachable FROM A CLIENT, not just from the VPS
```

---

## 5. Moving an environment to another machine

Environments are bound to machines only by **runner labels**, so migration is a
label move. No code, no workflow, no secret changes.

1. Bootstrap the new machine with the same label:
   `sudo RUNNER_TOKEN=… ./scripts/bootstrap-vps.sh --labels staging`
2. Take the old runner out of rotation — remove it in **Settings → Actions →
   Runners**, or on the old box:
   ```bash
   cd /opt/actions-runner
   sudo ./svc.sh stop && sudo ./svc.sh uninstall
   sudo -u rpg ./config.sh remove --token $(gh api -X POST repos/<owner>/<repo>/actions/runners/remove-token --jq .token)
   ```
3. Update anything host-specific in the environment — realistically just
   `GAMESERVER_PUBLIC_ADDR`, and `RPG_DEPLOY_DIR` if the path differs.
4. Re-run the last successful CD run, or dispatch a fresh one. It deploys to
   whichever runner now carries the label.

**Data does not move.** Postgres and Redis live in Docker volumes on the old
box. To carry state over, use `backend/deploy/db/backup.sh` on the old machine
and `restore.sh` on the new one before the first deploy.

### Two environments on one machine

Possible — give the runner both labels — but the two deploys will collide:

- Both write the same `$RPG_DEPLOY_DIR` unless you set a different
  `RPG_DEPLOY_DIR` per environment.
- Both compose projects use the same fixed `container_name`s (`rpg-postgres`, …)
  and publish the same host ports. The second deploy fails on a name/port
  clash.
- The `concurrency` group is per environment, so the two can run *simultaneously*
  and interleave.

Give each environment its own `RPG_DEPLOY_DIR` **and** its own port set
(`GATEWAY_ADDR`, `GAMESERVER_ADDR`, `GRAFANA_PORT`, …), or use separate
machines. Separate machines are the boring, working answer.

---

## 6. Troubleshooting

| Symptom | Cause | Fix |
|---------|-------|-----|
| Deploy job never starts, run sits queued | No online runner carries the environment's label | `gh api repos/<owner>/<repo>/actions/runners --jq '.runners[]\|"\(.name) \(.status) \([.labels[].name]\|join(","))"'`. On the box: `sudo systemctl status 'actions.runner.*'`. |
| **Services die seconds after a green deploy** | The Actions runner's post-job cleanup kills every process it can trace to the job via `RUNNER_TRACKING_ID` | Host mode already handles this: `deploy-local.sh` starts binaries with `RUNNER_TRACKING_ID= setsid nohup …`. Any process you add to a deploy step needs the same treatment. Containers are immune — they are children of the Docker daemon. |
| Run cancelled with *"Canceling since a higher priority waiting request exists"* | `concurrency: cd-<environment>` with `cancel-in-progress` — a newer push/dispatch for the same environment superseded this one | Expected. Dispatch **after** pushing, and watch that exact run id. A `develop` push will cancel your in-flight `dev` dispatch. |
| `deploy` fails: `environment secret X is not set` | Secret missing or empty on that Environment (not the repo!) | `gh secret list --env <env>`. Repo-level secrets do **not** satisfy an environment-scoped read. |
| Smoke fails at `enter world`: *no available server for map map_01* | Nothing in the Redis registry. The server self-registers, so this means it could not reach Redis (`REDIS_ADDR` wrong/unset) or it is not running | `docker exec rpg-redis redis-cli HGETALL servers:id:gs-dotnet-map_01` — expect a hash with `TTL` between 1 and 15. Empty? Check the server log for `Registered <id> in Redis` or a registry warning. Do NOT write the key by hand: it will be overwritten or expire. |
| Smoke passes on the VPS, real clients cannot join | `GAMESERVER_PUBLIC_ADDR` is listen-style (`:9200`). The Go smoketest rewrites a hostless address to its own loopback and so passes; real clients get the value verbatim and cannot dial it (a C# `TcpClient` throws) | Set it to `<public-host>:<port>` and redeploy. The game server warns at startup when the advertised address has no host part. |
| `bind: address already in use` | Host-mode processes still hold the ports the containers want (or another service does) | Containers mode stops host services first; if it persists: `ss -tlnp \| grep -E '8000\|9200'`. |
| Container images pull 404 from `ghcr.io` | GHCR packages are **private by default**, even in a public repo | Make the package public (package → Settings → Change visibility), or add a pull secret. Dev/staging avoid this entirely by building images on the runner. |
| Grafana password change had no effect | `GF_SECURITY_ADMIN_PASSWORD` is only read when Grafana *creates* the admin user, and `lgtm-data` persists that DB | See `MONITORING.md` § rotation procedure. |
| Dashboard panels empty while targets read UP | C# metric names arriving with dots | `metric_name_escaping_scheme: underscores` in `monitoring/prometheus.yaml`; verify the config was actually reloaded (a bind-mount change needs an lgtm restart — CD does this automatically). |
| `gameserver` scrape target DOWN in containers mode | Expected — the host-facing job hits the named-prefix listener with the wrong `Host` header | Use `gameserver-container`; it is authoritative. |
| Grafana reachable from the internet despite `ufw deny` | Docker's `DOCKER-USER` chain is traversed before ufw's INPUT | Set `GRAFANA_BIND=127.0.0.1` (best), or keep the `DOCKER-USER` rule `bootstrap-vps.sh` adds — note it is not reboot-persistent without `iptables-persistent`. |
| Everything is odd and the box is WSL, not a VPS | WSL/Docker Desktop specifics — `docker` is a shim for `docker.exe`, absolute `/mnt/*` paths break, k3d/testcontainers do not work | See `K3S.md` § Troubleshooting and `RUNBOOK-local-dev.md`. |

### Rollback

Fastest is re-running the last good CD run (Actions → CD → previous success →
*Re-run all jobs*); artifacts are kept 14 days. Machine-local options and the
image-tag rollback for containers mode are in `CICD.md` §7.
