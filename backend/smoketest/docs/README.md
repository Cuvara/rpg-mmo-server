# Smoketest Module

Post-deploy smoke test — a headless client binary that exercises the **entire**
login → gameplay flow against a running stack and exits non-zero on any failure.
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

Every network operation has a timeout (default 10s) — the binary cannot hang CI.
Output is a per-step `PASS`/`FAIL` line with latency, plus a machine-readable
final line: `SMOKE=PASS` or `SMOKE=FAIL`.

```
--- smoke test summary ---
PASS  nakama_health               9ms  http://localhost:7350/healthcheck
PASS  device_auth                11ms  device_id=smoketest-4f2edf0c963d2262
PASS  gateway_token_rpc           2ms  user_id=a64ac680-…
PASS  gateway_auth                3ms  map=map_01 server=[::]:9200
PASS  gameserver_join          1.009s  snapshots=10 final_x=10.00
SMOKE=PASS
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

The game server address is **not** configured — it comes from the
`EnterWorldResponse`, exactly like a real client. Neither is the game server
*transport*: the runner dials whatever `EnterWorldResponse.Transport` announces
(empty = `tcp`), so a gateway on TCP in front of KCP game servers works without
any extra flag.

```bash
# Full flow with both hops on KCP
JWT_SECRET=dev-secret-change-me TRANSPORT=kcp GATEWAY_ADDR=127.0.0.1:8200 \
  SMOKE_MAP_ID=map_kcp bin/smoketest
```

## Run locally

Bring up the dev stack (meta stack via `backend/deploy`, gateway + gameserver
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

## Layout

```
smoketest/
  cmd/smoketest/main.go   # thin entry point, exit code
  smoke/helpers.go        # pure helpers: config/env parsing, addr normalization, result formatting
  smoke/runner.go         # the actual flow (HTTP + TCP wire protocol via shared/messages)
  smoke/helpers_test.go   # table-driven tests for the pure helpers
```
