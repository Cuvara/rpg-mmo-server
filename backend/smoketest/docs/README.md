# Smoketest Module

Post-deploy smoke test — a headless client binary that exercises the **entire**
login → gameplay flow against a running stack (including the C# .NET 10 game
server) and exits non-zero on any failure.
The CD pipeline runs it right after the post-deploy healthcheck; a failing smoke
test fails the deploy.

## What it covers

| Step | What it does | Verifies |
|------|--------------|----------|
| `nakama_health` | `GET {NAKAMA_URL}/healthcheck` | Nakama HTTP is up |
| `device_auth` | `POST /v2/account/authenticate/device?create=true` with a random-suffix device id | Auth path + plugin-loaded Nakama accepts logins |
| `gateway_token_rpc` | `POST /v2/rpc/gateway_token` (Bearer session token, body `"{}"`) | RPC issues a JWT; verified **locally** with `shared/jwt` + `JWT_SECRET`; `sub` claim matches the Nakama user id |
| `gateway_auth` | `TRANSPORT` (tcp/kcp) to `GATEWAY_ADDR`: `MsgAuth` then `MsgEnterWorld map_01` | Gateway verifies the JWT locally, registry lookup returns `ServerAddr` + `JoinToken` |
| `gameserver_join` | Dials `ServerAddr` with the transport announced in `EnterWorldResponse.Transport`: `MsgJoinToken`, then ~10 `MsgInput` at 100 ms; requires ≥ 5 `MsgSnapshot` and the player entity moved as expected (X ≈ input count); clean `MsgDisconnect` | Join handshake, tick loop, input validation, AOI snapshot broadcast |
| `nakama_account` | `GET /v2/account` | The device login created a **durable** account: id matches the `gateway_token` subject, our device id is linked |
| `nakama_profile` | `POST /v2/storage` reading `player`/`profile` | The plugin's `AfterAuthenticate` hook wrote the profile, owned by the player, at `level == StartingLevel` |
| `gamestate_migrations` | `SELECT … FROM schema_migrations` | Non-empty, gap-free, checksummed, and at the version this binary expects |
| `gamestate_player_row` | Polls `player_states` for this run's user | The game server persisted the player: right map, moved position, HP intact, `updated_at` newer than the run start |
| `gamestate_reload` | Waits out the reconnect hold, rejoins | The persisted row is **read back** — the player respawns at the saved position, not the origin |

Every network operation has a timeout (default 10s) — the binary cannot hang CI.
Output is a per-step `PASS`/`FAIL` line with latency, plus a machine-readable
final line: `SMOKE=PASS` or `SMOKE=FAIL`.

```
--- smoke test summary ---
PASS  nakama_health               9ms  http://localhost:7350/healthcheck
PASS  device_auth                11ms  device_id=smoketest-4f2edf0c963d2262
PASS  gateway_token_rpc           2ms  user_id=a64ac680-…
PASS  gateway_auth                3ms  map=map_01 server=[::]:9200
PASS  gameserver_join          1.109s  snapshots=15 (keyframes=1 deltas=14) final_x=3.33 ack_tick=10
PASS  nakama_account              3ms  user=ff2f4dcb-… username=kLiTvRpZIl devices=1
PASS  nakama_profile              2ms  player/profile level=1 display_name=kLiTvRpZIl
PASS  gamestate_migrations        7ms  version=1 (001_init) applied=2026-08-05T07:34:08Z
PASS  gamestate_player_row    14.031s  map=map_01 x=3.3333 y=0.0000 hp=100/100 updated_at=… (15 polls)
PASS  gamestate_reload         19.14s  respawned at x=3.3333 y=0.0000 from persisted x=3.3333 y=0.0000
SMOKE=PASS
```

## Database checks

The realtime flow above proves the wire; it says nothing about whether either
database kept anything. Five steps close that gap.

**Nakama meta DB — via the public HTTP API, not SQL.** `nakama_account` and
`nakama_profile` reuse the session token the run already holds, so they need no
new credential, cost a few milliseconds, and work unchanged against a remote VPS
whose meta PostgreSQL the runner cannot reach. They always run.

The one trap worth knowing: Nakama's `ReadStorageObjects` answers **HTTP 200 with
an empty object list** when the record does not exist. A check that trusted the
status code would pass against a Nakama with our Go plugin missing — which is
exactly the regression `nakama_profile` targets — so the emptiness assertion is
the load-bearing one.

**Game-state DB — direct SQL, because there is no alternative.** Nothing exposes
`player_states` over HTTP, and ADR-1 keeps the two PostgreSQL instances separate,
so `gamestate_*` needs `GAME_DB_URL`. Without it those three steps report **SKIP**
with the reason, never PASS: "the check did not run" and "the check passed" must
not look the same. `--require-db` turns a skip into a failure.

**Timing.** `gamestate_player_row` polls instead of sleeping. The game server never
writes on the hot path — a row lands on the `AsyncSaver` sweep (30s) or when the
reconnect hold expires and the entity is evicted (another 30s), so arrival spans a
~60s window. `gamestate_reload` then *must* wait out that hold before rejoining:
inside the window a reconnect reattaches to the still-resident in-memory entity
(`GameServer.cs`, the `existing != null` branch) and the restored position would
prove nothing about the database. Together they add ~35s; the default path with
`GAME_DB_URL` unset is unchanged apart from ~5ms of Nakama checks.

```bash
# Full run including persistence, strict about it
JWT_SECRET=dev-secret-change-me \
GAME_DB_URL='postgres://game:localdev@localhost:5433/gamestate?sslmode=disable' \
  bin/smoketest --require-db

# Realtime only — the pre-existing fast path
JWT_SECRET=dev-secret-change-me bin/smoketest --skip-db
```

## Configuration

All endpoints are overridable via env and/or flags (flags win):

| Env | Flag | Default | Meaning |
|-----|------|---------|---------|
| `NAKAMA_URL` | `--nakama-url` | `http://localhost:7350` | Nakama HTTP base URL |
| `NAKAMA_SERVER_KEY` | `--server-key` | `defaultkey` | Nakama socket server key |
| `GATEWAY_ADDR` | `--gateway-addr` | `:8000` | Gateway address (listen-style addrs are normalized to loopback) |
| `TRANSPORT` | `--transport` | `tcp` | Transport for the **gateway** hop: `tcp` or `kcp` |
| `JWT_SECRET` | `--jwt-secret` | — (**required**) | Shared HS256 secret for local JWT verification |
| `SMOKE_MAP_ID` | `--map-id` | `map_01` | Map to enter |
| `SMOKE_TIMEOUT` | `--timeout` | `10s` | Per-operation network timeout |
| — | `--inputs` | `10` | Number of `MsgInput` frames |
| — | `--input-interval` | `100ms` | Delay between inputs |
| — | `--min-snapshots` | `5` | Minimum `MsgSnapshot` count to pass |
| `GAME_DB_URL` | `--game-db-url` | *(unset)* | Game-state DSN. Unset ⇒ the three `gamestate_*` checks **SKIP** |
| `SMOKE_DEVICE_ID` | `--device-id` | *(random)* | Pin the Nakama device id (reuses the account) instead of generating one |
| `SMOKE_SKIP_DB` | `--skip-db` | `false` | Skip every persistence check |
| `SMOKE_REQUIRE_DB` | `--require-db` | `false` | Fail instead of skipping when a persistence check cannot run |
| `SMOKE_EXPECT_MIGRATION` | `--expect-migration-version` | `1` | Required `schema_migrations` version — **bump with every new migration** |
| `SMOKE_DB_POLL_TIMEOUT` | `--db-poll-timeout` | `75s` | Deadline for the `player_states` row |
| `SMOKE_DB_POLL_INTERVAL` | `--db-poll-interval` | `1s` | Gap between polls |
| `SMOKE_HOLD_TTL` | `--hold-ttl` | `30s` | Game server reconnect hold, waited out before the reload check |
| `SMOKE_STRICT_ADDR` | `--strict-addr` | `false` | Fail instead of rewriting when the advertised **game server** address is listen-style |

The game server address is **not** configured — it comes from the
`EnterWorldResponse`, exactly like a real client. Neither is the game server
*transport*: the runner dials whatever `EnterWorldResponse.Transport` announces
(empty = `tcp`), so a gateway on TCP in front of KCP game servers works without
any extra flag.

### Strict address mode (`--strict-addr`)

By default a listen-style `ServerAddr` — `:9000`, `0.0.0.0:9000`, `[::]:9200` —
is rewritten to `127.0.0.1:<port>` before dialing. That is correct for host-mode
deploys and local dev, where a bare `:9000` genuinely *is* where clients connect,
and the C# side agrees on which addresses count as listen-style
(`GameServer/Program.cs`, `IsHostlessAddr`).

Under Kubernetes it is a trap. With Agones and `portPolicy: Dynamic` the game
server must learn its scheduler-assigned address from the sidecar and register
*that*. If it does not, it advertises the hostless `:9000`, the gateway forwards
it to the client verbatim, and no real client can dial it — but the smoke test's
rewrite would connect to whatever sits on port 9000 of the local host (quite
possibly an unrelated compose-run game server), collect snapshots, and report
**PASS**. The run would prove nothing while the real client fails.

**Turn it on for any run whose purpose is to prove that a Kubernetes/Agones-
allocated game server is reachable**, i.e. every allocation or fleet verification
run. Then a listen-style `ServerAddr` fails the `gateway_auth` step outright:

```
FAIL  gateway_auth  ...  error: enter world: strict address mode: game server advertised ":9000",
a listen-style address no client can dial; the game server never learned its externally-dialable
address — under Agones that is the sidecar GameServer status read (allocated address + dynamic
port), otherwise set GAMESERVER_PUBLIC_ADDR to the host:port clients reach
```

Strictness applies to the **game-server hop only**. `GATEWAY_ADDR` is
operator-supplied local config (`:8000` by default), not an address a server
advertised, so it keeps the loopback rewrite in both modes. Strict mode also
rejects *only* listen-style addresses: a loopback address the server deliberately
advertised (`127.0.0.1:9000`, plausible under k3d port-forwarding) passes through
untouched.

```bash
# Proving an Agones-allocated server is really reachable
JWT_SECRET=dev-secret-change-me GATEWAY_ADDR=127.0.0.1:8000 \
  bin/smoketest --strict-addr
```

```bash
# Full flow with both hops on KCP
JWT_SECRET=dev-secret-change-me TRANSPORT=kcp GATEWAY_ADDR=127.0.0.1:8200 \
  SMOKE_MAP_ID=map_kcp bin/smoketest
```

## Run locally

Bring up the dev stack (meta stack via `backend/deploy`, gateway + gameserver-dotnet
with a shared Redis registry), then:

```bash
scripts/build-all.sh --skip-tests          # produces bin/smoketest
JWT_SECRET=dev-secret-change-me GATEWAY_ADDR=:8000 bin/smoketest
```

## Run in CI

The CD workflow (`.github/workflows/cd.yml`) stages `bin/smoketest` into the
deployment bundle, installs it to `$RPG_DEPLOY_DIR/bin/smoketest`, and runs it
in the "Post-deploy smoke test" step with env sourced from
`$RPG_DEPLOY_DIR/deploy/.env` (the same file the services read). Secrets are
never echoed.

The persistence checks need **no CD change**: cd.yml already writes
`GAME_DB_URL=${{ vars.GAME_DB_URL }}` into that same `deploy/.env`, and the
`db-migrate` job runs `--migrate-only` against that DSN from the runner, so it is
reachable from where the smoke test runs. Environments that leave the
`GAME_DB_URL` var unset get SKIP lines instead of silent passes; set the var to
turn them on, and add `--require-db` to the step once every environment has it.

## Layout

```
smoketest/
  cmd/smoketest/main.go   # thin entry point, exit code
  smoke/helpers.go        # pure helpers: config/env parsing, addr normalization, result formatting
  smoke/runner.go         # the actual flow (HTTP + TCP wire protocol via shared/messages)
  smoke/helpers_test.go   # table-driven tests for the pure helpers
```
