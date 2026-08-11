# Runbook — Local Dev Meta Stack

Operational guide for the local Nakama + PostgreSQL + Redis stack defined in
`backend/deploy/docker-compose.yml`. Everything below runs from
`backend/deploy/` unless stated otherwise.

Scope: **backing services** — Nakama (auth/economy/social), PostgreSQL (meta DB),
Redis (sessions, server registry, event streams). Realtime services (gateway,
gameserver) still run on the host with `go run` — see the root `CLAUDE.md`.

## Prerequisites

- Docker Engine + `docker compose` v2 (`docker compose version`)
- BuildKit enabled (default in modern Docker; the Makefile sets `DOCKER_BUILDKIT=1`)
- `make`, `curl`

WSL note: if Docker is installed on Windows (Docker Desktop) rather than inside
the distro, enable WSL integration for this distro or `docker` will not be on
`PATH`. Verify with `docker compose version` before anything else. If WSL
integration is off, the Windows binary is still reachable as `docker.exe` — but
`docker.exe compose -f <abs-WSL-path>` breaks on path translation, so always run
compose from `backend/deploy/` and let it pick up the local `docker-compose.yml`.

> **On this project's dev machine WSL integration is OFF and `docker` is a shim
> to `docker.exe`.** That is deliberate and documented, with the reasoning and
> the (unflipped) switch-over plan, in
> [`CICD.md` §4a–4b](CICD.md#4a-the-dev-runner-is-wsl-and-docker-there-is-a-shim).
> The one rule you must follow when writing new commands: a bind-mount source
> must be a **literal relative path** — `-v ".:/x"` works, `-v "$PWD:/x"`
> silently mounts an **empty** directory and exits 0. `$PWD` is absolute, so it
> gets translated to a nonexistent `E:\mnt\…` path that Docker Desktop then
> helpfully creates. Prefer named volumes or `docker exec` stdio, which the
> `db/` scripts already do and which are immune to this.

Status: the whole path below (plugin build → stack up → `gateway_token` RPC →
gateway `MsgAuth`) has been executed end-to-end against nakama 3.40.0.

```bash
cp .env.example .env      # then edit if you want non-default credentials
```

## 1. Build the plugin

The Nakama runtime plugin lives in `backend/nakama` and imports
`backend/shared` via a `replace` directive, so the Docker build context must be
`backend/`.

```bash
make plugin
```

Equivalent raw command (from the **repo root**):

```bash
DOCKER_BUILDKIT=1 docker build \
  -f backend/deploy/nakama-plugin.Dockerfile \
  --build-arg NAKAMA_VERSION=3.40.0 \
  --target export \
  --output type=local,dest=backend/deploy/modules \
  backend/
```

This writes `backend/deploy/modules/nakama.so`, which the `nakama` service
bind-mounts at `/nakama/data/modules`.

To instead bake the plugin into an image (for k3s / CI):

```bash
docker build -f backend/deploy/nakama-plugin.Dockerfile \
  --target runtime -t rpg-mmo/nakama:3.40.0 backend/
# or: make image
```

### Version pinning rule

`heroiclabs/nakama-pluginbuilder:<V>` and `heroiclabs/nakama:<V>` **must be the
same `<V>`** (currently `3.40.0`). Go plugins are ABI-locked to the exact Go
toolchain and `nakama-common` version compiled into the server. A mismatch fails
at startup with:

```
plugin.Open("...nakama"): plugin was built with a different version of package ...
```

Fix: bump `NAKAMA_VERSION` in `.env` **and** the `ARG NAKAMA_VERSION` default in
`nakama-plugin.Dockerfile`, align `github.com/heroiclabs/nakama-common` in
`backend/nakama/go.mod` to the version shipped with that release, then
`make reset && make plugin && make up`.

Note: `backend/nakama/go.mod` declares `go 1.26.5`. `nakama-pluginbuilder:3.40.0`
ships exactly `go1.26.5` (verified: `docker run --rm
heroiclabs/nakama-pluginbuilder:3.40.0 version` — the image entrypoint is `go`),
so no toolchain workaround is needed. If a future pluginbuilder tag ships an
older toolchain the build fails with `go: go.mod requires go >= 1.26.5`; remedy
is a newer pluginbuilder tag or lowering the `go` directive (owned by
`agent-nakama` — do not edit from deploy). Do **not** paper over it with
`GOTOOLCHAIN=auto`: downloading a different toolchain than the server binary was
built with reintroduces the plugin ABI mismatch.

Verified build (17.2 MB `modules/nakama.so`, ~18 s compile) followed by
`docker compose restart nakama` yields these log lines:

```
"msg":"Found runtime modules","count":1,"modules":["nakama.so"]
"caller":"nakama/main.go:39","msg":"rpg-mmo nakama module loaded in 0ms","runtime":"go"
"msg":"Registered Go runtime RPC function invocation","id":"gateway_token"
"msg":"Registered Go runtime Before function invocation","id":"authenticateemail"
"msg":"Registered Go runtime After function invocation","id":"authenticatedevice"
"msg":"Registered Go runtime After function invocation","id":"authenticateemail"
```

## 2. Start / stop

```bash
make up        # docker compose up -d
make ps        # service status
make down      # stop, data volumes preserved
make reset     # stop + delete postgres-data, postgres-game-data & redis-data volumes + modules/nakama.so
```

The stack contains **two** PostgreSQL instances: `postgres` (Nakama meta DB,
host port 5432) and `postgres-game` (game state DB — `player_states`, host port
5433). They are deliberately separate; never point the gameserver at the meta DB.

Startup order is enforced: `nakama` waits for `postgres` to report healthy
(`pg_isready`), then runs `nakama migrate up` and only afterwards starts the
server (single entrypoint, `migrate && exec nakama`).

## 3. Verify

```bash
# a) Nakama HTTP API healthcheck — expects HTTP 200 and "{}"
curl -fsS http://localhost:7350/healthcheck && echo OK

# a2) Redis — expects PONG
docker compose exec -T redis redis-cli ping
# with REDIS_PASSWORD set: docker compose exec -T redis \
#   sh -c 'redis-cli -a "$REDIS_PASSWORD" --no-auth-warning ping'
#
# `make health` runs both (a) and (a2).

# a3) Game state DB — expects the player_states table to exist
docker compose exec -T postgres-game \
  psql -U game -d gamestate -c '\d player_states'
# or: make psql-game

# b) Container health status
docker compose ps         # all four services should read "healthy"

# c) Console login
open http://localhost:7351     # user: admin  password: password (from .env)
```

In the console, **Runtime Modules** must list `nakama.so` (or the module name
registered by `InitModule`). If it is missing, the plugin failed to load — check
`make logs-nakama` for a `plugin.Open` error.

```bash
# d) Plugin RPC smoke test — gateway_token requires an authenticated *user*
#    session, so `?http_key=...` is not enough (it returns code 16).
#    Note the Nakama quirk: the RPC body is a JSON-encoded *string*.
SESSION=$(curl -fsS -X POST \
  'http://localhost:7350/v2/account/authenticate/device?create=true' \
  -u defaultkey: -H 'Content-Type: application/json' \
  -d '{"id":"local-smoke-device-01"}' | python3 -c 'import json,sys;print(json.load(sys.stdin)["token"])')

curl -fsS -X POST http://localhost:7350/v2/rpc/gateway_token \
  -H "Authorization: Bearer $SESSION" -H 'Content-Type: application/json' \
  -d '"{\"server_id\":\"map_01-abc\"}"'
# -> {"payload":"{\"token\":\"eyJ…\",\"user_id\":\"fc10…\",\"expires_in\":3600}"}
```

Measured on the dev box (Docker Desktop / WSL2): device auth ~22 ms, RPC ~1-4 ms.

The returned `token` is a `shared/jwt` HS256 token (`sub` = Nakama user id,
optional `sid`, 3600 s TTL) and is accepted directly by the gateway: sending it
as `MsgAuth.token` over TCP to `:8000` returns `MsgAuthResp{ok:true, user_id:<same
uuid>}`. That is the full meta → realtime handshake.

The `AfterAuthenticateDevice` hook writes the starting profile on first login;
confirm with:

```bash
docker compose exec -T postgres psql -U nakama -d nakama -t \
  -c "select collection,key,value from storage where user_id='<uuid>';"
# -> player | profile | {"level": 1, "created_at": …, "display_name": "…"}
```

## 4. Ports

| Port | Service | Purpose |
|------|---------|---------|
| 5432 | postgres | Meta DB — Nakama (host-exposed for `psql` / GUI clients) |
| 5433 | postgres-game | Game state DB — `player_states` (`POSTGRES_GAME_PORT` override) |
| 6379 | redis | Sessions, server registry, event streams (`REDIS_PORT` override) |
| 7349 | nakama | gRPC API |
| 7350 | nakama | HTTP API + client socket, `/healthcheck` |
| 7351 | nakama | Console web UI |
| 9100 | nakama | Prometheus metrics |

## 5. Debug

```bash
make logs                # all services
make logs-nakama         # nakama only
docker compose logs postgres

make psql                # psql shell on the meta DB
make redis-cli           # redis-cli shell (auth applied if REDIS_PASSWORD set)
docker compose exec nakama /nakama/nakama --version
```

Useful Redis inspection (inside `make redis-cli`):

```
KEYS session:*            # session store (dev only — never KEYS in production)
XINFO STREAM <stream>     # event stream state
XINFO GROUPS <stream>     # consumer groups + pending/ACK lag
INFO keyspace
```

Common failures:

| Symptom | Cause | Fix |
|---------|-------|-----|
| `plugin was built with a different version of package` | pluginbuilder tag ≠ nakama tag | Align versions, rebuild plugin |
| Console lists no runtime modules | `modules/nakama.so` missing | `make plugin` then `make up` |
| `dial tcp: connect: connection refused` on migrate | postgres not ready | Healthcheck should prevent this; check `docker compose logs postgres` |
| Gateway rejects Nakama-issued tokens | secret mismatch | `JWT_SECRET` in `.env` must equal `JWT_SECRET` exported for gateway/gameserver |
| Port already allocated | host process on 5432/6379/7350 | Change `POSTGRES_PORT` / `REDIS_PORT` in `.env` or stop the host service |
| `NOAUTH Authentication required` from gateway/gameserver | `REDIS_PASSWORD` set in `.env` but not exported to the host process | Export the same `REDIS_PASSWORD` before `go run` |
| `dial tcp [::1]:6379: connect: connection refused` | Redis not up, or `REDIS_PORT` remapped | `make ps`; set `REDIS_ADDR=localhost:$REDIS_PORT` |

Nakama logs at `DEBUG` level in this stack (`--logger.level DEBUG`); lower it in
`docker-compose.yml` if the output is too noisy.

## 6. Reset the database

```bash
make reset        # drops postgres-data + redis-data volumes AND the built plugin
make plugin && make up
```

Volume-only reset:

```bash
docker compose down
docker volume rm rpg-mmo-meta_postgres-data       # meta DB
docker volume rm rpg-mmo-meta_postgres-game-data  # game state DB (player_states)
docker volume rm rpg-mmo-meta_redis-data        # sessions / registry / streams
docker compose up -d
```

Flush Redis without touching Postgres:

```bash
docker compose exec -T redis redis-cli FLUSHALL
```

Migrations re-run automatically on next start.

## 7. Wiring to the realtime services

Nakama signs client session tokens (HS256) with `session.encryption_key`, which
this stack sets from `JWT_SECRET`. The gateway verifies those tokens locally with
the same secret — no roundtrip to Nakama. Run realtime services with a matching
secret:

Gateway and gameserver also use Redis (session store, server registry, event
streams — `go-redis` v9) via `REDIS_ADDR` / `REDIS_PASSWORD` from
`backend/shared/config` (defaults `localhost:6379` and empty). The compose stack
publishes Redis on the host, so the defaults work as-is.

```bash
export JWT_SECRET=dev-secret-change-me      # must equal .env JWT_SECRET
export JOIN_TOKEN_SECRET=dev-join-secret-change-me   # must equal .env; NOT JWT_SECRET
export REDIS_ADDR=localhost:6379            # match REDIS_PORT if you changed it
export REDIS_PASSWORD=                      # must equal .env REDIS_PASSWORD
export META_DB_URL='postgres://nakama:localdev@localhost:5432/nakama?sslmode=disable'
# Game state DB — omit to keep the in-memory player store (state lost on restart).
export GAME_DB_URL='postgres://game:localdev@localhost:5433/gamestate?sslmode=disable'

cd backend/gameserver && go run ./cmd/gameserver/ --addr=:9000 --map-id=map_01
cd backend/gateway    && go run ./cmd/gateway/    --addr=:8000
```

Container-to-container the address is `redis:6379` (service name); only host
processes use `localhost`. If you later containerize gateway/gameserver into
this compose file, set `REDIS_ADDR=redis:6379` and add
`depends_on: redis: condition: service_healthy`.

The gameserver applies the `player_states` schema itself on boot
(`pgstore.Migrate`, idempotent), so it works against an existing volume too;
`backend/deploy/db/init-gamestate.sql` only covers the first-boot case of an
empty volume. It logs `using postgres player store` with a password-redacted
DSN, or `using in-memory player store` when `GAME_DB_URL` is unset, and exits
non-zero if the DSN is set but unreachable. `--game-db-url` overrides the env
var. Container-to-container the DSN host is `postgres-game:5432`.

Redis persistence is AOF (`appendfsync everysec`) plus an RDB snapshot rule, on
the `redis-data` volume — sessions and registry entries survive a restart.
`make reset` wipes it along with the DB.

## 8. Running the realtime services as containers

Everything above runs gateway and game server on the host. The `realtime`
compose profile runs them as containers instead — the same thing CD does when an
environment sets `DEPLOY_MODE=containers` (see `docs/CICD.md` §3b).

```bash
cd backend/deploy
# build first — contexts differ from this directory:
docker build -f docker/Dockerfile.gateway           -t rpg-mmo/gateway:dev ..
docker build -f docker/Dockerfile.gameserver-dotnet -t rpg-mmo/gameserver-dotnet:dev ..

docker compose --profile realtime --profile monitoring up -d
```

Points that bite:

- **Stop the host processes first.** With the CD defaults the containers publish
  the same `:8000` / `:9200`; two owners of one port means the second bind
  fails. `scripts/deploy-local.sh stop` clears the host side.
- **The game server registers itself.** There is no manual step: it publishes its
  registry entry on startup and heartbeats it every 5s, so `MsgEnterWorld` works
  as soon as the container is listening. What still matters is
  `GAMESERVER_PUBLIC_ADDR` in `.env` — the address clients are handed verbatim,
  which in containers mode is the PUBLISHED port (`:9200`), not the listen port
  (`:9000`). Get it wrong and joins fail even though the registry looks healthy.
- **Health probes come from the host**, because the gateway image is distroless
  (no shell, no curl): `curl http://127.0.0.1:9102/healthz` for the gateway and
  `curl http://127.0.0.1:9101/healthz` for the game server. No `Host:` header is
  needed — both bind wildcards.
- **Container-to-container addressing** is by service name: `redis:6379`,
  `postgres-game:5432`, `nakama:7350` — never `localhost`.

`docker compose --profile realtime down` (or simply dropping the profile and
running `up -d --remove-orphans`) puts you back on the host path.

## Security note

Every credential in `.env.example` (`localdev`, `admin/password`, `defaultkey`,
`dev-secret-change-me`) is a **local-dev-only** default. Beta tier and above must
source these from k8s Secrets — see `docs/README.md`.
