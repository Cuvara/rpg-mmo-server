# Runbook — Local Dev Stack

Operational guide for the local stack defined in
`backend/deploy/docker-compose.yml`. Everything below runs from
`backend/deploy/` unless stated otherwise.

Two ways to run it:

- **[§0 Run the whole thing](#0-run-the-whole-thing-locally)** — one command,
  everything in containers including the gateway and the C# game server. This is
  what you want to connect a client to something, and what a newcomer should
  read first.
- **§1–§7** — backing services only (Nakama, PostgreSQL, Redis) with the
  realtime services run on the host via `go run` / `dotnet run`. This is the
  interactive-development path: instant rebuilds and a debugger.

---

## 0. Run the whole thing locally

```bash
cd backend/deploy
./stack.sh up        # build every image, start everything, wait for readiness
./stack.sh check     # prove the full client flow end to end
```

`up` is idempotent — re-run it any time. On a cold cache the first run takes a
while: it compiles the Nakama Go plugin and does a NativeAOT publish of the C#
game server. Afterwards Docker layer caching makes it fast.

That brings up six processes, wired with matching secrets:

| Service | Host port | Role |
|---|---|---|
| gateway | `8100` (`GATEWAY_CONTAINER_PORT`) | auth + map assignment. **The client connects here first.** |
| game server (C#) | `9200` (`GAMESERVER_CONTAINER_PORT`) | the gameplay connection, dialed *after* `MsgEnterWorldResp` |
| Nakama | `7350` HTTP, `7351` console | device auth, `gateway_token` RPC, profile/economy |
| PostgreSQL (meta) | `5432` | Nakama's database |
| PostgreSQL (game state) | `5433` | `player_states`, written only by the game server |
| Redis | `6379` | sessions, **server registry**, event streams |

Other subcommands (all take the same flags):

```bash
./stack.sh health    # probe every health endpoint AND the server registry
./stack.sh ps        # service status
./stack.sh logs      # tail gateway + game server
./stack.sh down      # stop, keep data volumes
./stack.sh down --wipe   # stop and delete the data volumes
./stack.sh up --no-build # skip image builds, use what is already built
```

`make flow-up` / `flow-check` / `flow-down` / `flow-health` are wrappers around
the same script. They are a convenience only — **`make` is not installed on
every dev box here**, so nothing in the documented path requires it.

### What `check` proves

`./stack.sh check` runs `backend/smoketest`, which walks exactly the path a
Unity client walks and prints a PASS/FAIL line per step (this transcript is a
real `--scratch` run, hence the offset ports):

```
--- smoke test summary ---
PASS  nakama_health               3ms  http://localhost:8350/healthcheck
PASS  device_auth                18ms  device_id=smoketest-38dddaf2d1c30537
PASS  gateway_token_rpc           7ms  user_id=b167f0b2-d0a1-4fe3-836a-3fcf972e9889
PASS  gateway_auth                5ms  transport=tcp map=map_01 server=:9300 (tcp)
PASS  gameserver_join          1.112s  snapshots=15 (keyframes=2 deltas=13) final_x=3.33 ack_tick=10
PASS  nakama_account             10ms  user=… username=VetOPcvJMk devices=1
PASS  nakama_profile              5ms  player/profile level=1 display_name=VetOPcvJMk
PASS  gamestate_migrations        8ms  version=1 (001_init) applied=…
PASS  gamestate_player_row    20.035s  map=map_01 x=3.3333 y=0.0000 hp=100/100 (21 polls)
PASS  gamestate_reload        13.095s  respawned at x=3.3333 from persisted x=3.3333
SMOKE=PASS
```

The last three steps wait out real timers (the 30s persistence sweep and the
reconnect hold), so a full `check` takes about a minute. `SMOKE_FLAGS=-skip-db
./stack.sh check` skips them and returns in seconds.

To push traffic through the same path instead of a single client:

```bash
cd backend/loadtest
set -a; . ../deploy/.env; set +a
go run ./cmd/loadtest -join=gateway -gateway-addr=:8100 -players=5 -duration=15s
```

### Pointing a Unity client at it

The client needs **one address and one secret**, and nothing else:

- **Gateway address** — `localhost:8100` (the `GATEWAY_CONTAINER_PORT` row
  above). This is the only address the client configures. The game server
  address arrives at runtime in `MsgEnterWorldResp.ServerAddr`; the client must
  dial it directly and must never assume it (ADR-3 — the gateway is a
  redirector, not a proxy, so the client holds two connections).
- **Nakama** — `http://localhost:7350`, server key `defaultkey`. The client
  authenticates here (device auth) and calls the `gateway_token` RPC to get the
  JWT it then sends in `MsgAuth`.
- **Secrets** — read them out of `backend/deploy/.env`. `JWT_SECRET` is shared
  by Nakama, the gateway and the game server; `JOIN_TOKEN_SECRET` is shared by
  the gateway and the game server only. A client never sees either: it receives
  a signed JWT from Nakama and an opaque join token from the gateway.

`GAMESERVER_PUBLIC_ADDR` is handed to clients **verbatim** in
`MsgEnterWorldResp.ServerAddr`, so it must be an address the client can dial.
Whenever ports are published — which is the case for this compose stack — that
means host-qualified: `GAMESERVER_PUBLIC_ADDR=127.0.0.1:9200` in `.env`. A bare
`:9200` is **not** portable: nothing in the protocol promises normalization, and
only some clients rewrite a hostless address to loopback themselves. The Go
smoketest does (`backend/smoketest/smoke/helpers.go`, `NormalizeDialAddr`) which
is why it can pass against a misconfigured stack, but a C# `TcpClient` throws on
it outright — so a Unity client fails the second hop while the smoke test looks
green. A bare `:port` is only correct for host-mode deploys, where the listen
address already is what clients reach.

If the client runs on a phone or another machine, use this host's LAN address:
`GAMESERVER_PUBLIC_ADDR=<this-host-ip>:9200`, then restart the game server. The
server logs a warning at startup when the advertised address has no host part.

### Two stacks side by side

`./stack.sh up --scratch` runs a second, fully isolated stack: its own compose
project, its own container names (`rpgs-*`), its own volumes, and every
published port offset — gateway `9000`, game server `9300`, Nakama `8350`,
console `8351`, Redis `7379`, Postgres `6432`/`6433`. Use it to test a change
without touching a stack someone else is using. Pass `--scratch` to every
subcommand of that stack, including `down`.

> **Trap this protects you from.** Without `--scratch`, running `up` in a
> directory while another checkout has the same compose project up does not
> fail — compose **adopts and recreates** the running containers, with your
> `.env` values. It prints a normal, successful-looking recreate while silently
> replacing someone else's environment.

### Troubleshooting

**`MsgEnterWorld` returns "no available server for map map_01"** — the game
server did not register itself. Registration is the game server's own job: it
writes `servers:id:<server_id>` / `servers:map:<map_id>` into Redis on boot and
heartbeats every 5s (TTL 15s). Check with `./stack.sh health`, whose `registry`
line reads the set directly. The usual cause is `REDIS_ADDR` unset on the game
server, which makes it start happily and stay invisible.

**The join token is rejected by the game server** — the token's `sid` claim
comes from the registry entry the game server wrote, and the game server checks
it against its own `--server-id`/`GAMESERVER_ID`. If those disagree the
handshake fails at the last hop. Both come from `GAMESERVER_ID` in `.env`, so
they only diverge if something overrides one of them.

**Either binary exits immediately with a non-zero status** — `JOIN_TOKEN_SECRET`
is mandatory on both the gateway and the game server, they must match, and both
refuse to start without it. `.env.example` sets a dev default.

**Exported environment variables seem to be ignored by compose** — on this
project's dev box `docker` is a shell shim to the Windows `docker.exe`
(`CICD.md` §4a). WSL only forwards an environment variable to a Windows process
if it is listed in `$WSLENV`, so `export COMPOSE_PROJECT_NAME=… ; docker compose
up` reaches compose with the variable **unset** and operates on the default
project. This is silent. That is why `stack.sh` passes configuration with
`--env-file` and `-p` rather than through the environment; do the same in any
new script here.

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

> This section is the manual form of what [§0](#0-run-the-whole-thing-locally)
> automates. Prefer `./stack.sh up` unless you specifically need to drive the
> steps yourself; the script additionally waits for the game server to appear in
> the registry, which is the difference between a usable stack and a confusing
> one.

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
