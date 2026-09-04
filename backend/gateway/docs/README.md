# Gateway Module

Custom Go binary — the entry point for Unity clients. Stateless (all state lives in
the configured stores), horizontally scalable.

Transport today is **TCP + length-prefixed JSON** (`shared/messages`); KCP/UDP is
available with `--transport=kcp` and swaps in behind the same handler code.

**The gateway is a redirector, not a proxy.** It answers "who am I?" and "which
server serves this map?", then gets out of the way — the client opens a second,
direct connection to the game server. No gameplay traffic transits this process.
Rationale and tradeoffs: `backend/docs/ARCHITECTURE-DECISIONS.md`, ADR-3.

## Architecture

```
Unity Client --[TCP|KCP]--> Gateway          (auth, map assignment)
     |                         |
     |                         +--> SessionStore   (memory | Redis)
     |                         +--> ServerRegistry (memory | Redis)
     |                         +--> EventStream    (memory | Redis Streams)
     |
     |   returns {ServerAddr, JoinToken}; client then dials the server itself:
     |
     +---[TCP|KCP]----------> Game Server (C# .NET 10)   <-- gameplay lives here
```

## Features

| Feature | Description | Status |
|---------|-------------|--------|
| TCP transport | Length-prefixed JSON envelopes; KCP planned | ✅ |
| Session Manager | Local JWT verify, create / validate / refresh / destroy | ✅ |
| Server Registry | Live servers per map, capacity-checked pick: least-loaded normally, **lowest `ServerID`** when a map has more than one live server (ADR-2 violated — #203) | ✅ |
| Map Assignment | `MsgEnterWorld` → server addr + 30s join token | ✅ |
| Event Relay | Consumes the cross-server event stream (log-only sink — see DESIGN.md) | 🟡 |
| Allocator | Agones allocation when a map has **no** live server (a full map is refused, never given a second instance — ADR-2) | ⬜ stub |
| Dungeon transfer | Party → dungeon instance | ⬜ stub |

## Run

```bash
# In-memory backend (default — single process, dev/tests)
go run ./cmd/gateway/ --addr=:8000

# KCP/UDP instead of TCP for the realtime path (opt-in)
go run ./cmd/gateway/ --addr=:8000 --transport=kcp

# Redis backend (multi-instance; shared sessions, registry, event stream)
REDIS_ADDR=127.0.0.1:6379 go run ./cmd/gateway/
# or explicitly
go run ./cmd/gateway/ --backend=redis --instance-id=gw-1
```

### Flags

| Flag | Default | Description |
|------|---------|-------------|
| `--addr` | `GATEWAY_ADDR` (`:8000`) | Listen address; overrides the env value |
| `--transport` | `GATEWAY_TRANSPORT` (`tcp`) | Realtime transport: `tcp` or `kcp` (KCP/UDP). Overrides the env value |
| `--backend` | auto (see below) | `memory` or `redis` |
| `--instance-id` | hostname | Consumer name inside the `gateway` event-stream consumer group |
| `--allocator` | `ALLOCATOR` (`none`) | `none` or `agones` — allocate a GameServer when **no** live server serves a map. A map whose servers are all full is refused, not expanded |
| `--allocator-namespace` | `rpg-realtime` | Namespace holding the Agones fleets |
| `--allocator-fleet-map` | `map-servers-dotnet-dev` | Fleet used for map allocations. Must name a fleet that exists — the allocator does not validate it, so a wrong value fails at the first allocation, not at start-up |
| `--allocator-fleet-dungeon` | *(none)* | Fleet used for dungeon allocations. No default: no dungeon fleet is deployed, so a dungeon allocation fails immediately with `no fleet configured for allocation kind` instead of a Kubernetes 404 |
| `--allocator-transport` | `ALLOCATOR_TRANSPORT` → `--transport` | Transport stamped on the allocation response. **Inert since 2026-08-17**: the transport announced to a client always comes from the pod's own registry entry |
| `--allocation-wait-timeout` | `ALLOCATION_WAIT_TIMEOUT` (`15s`) | How long to wait for an allocated pod to register itself before failing the join as retryable (`server is starting, retry shortly`). **Hard ceiling 20s** (`pongTimeout - pingInterval`): the wait blocks the connection's read loop, which is what records `MsgPong`, so a larger value lets the heartbeat disconnect the client mid-allocation — the gateway refuses to start above it |
| `--allocation-poll-interval` | `ALLOCATION_POLL_INTERVAL` (`250ms`) | Registry re-check interval during that wait |
| `--allocator-kubeconfig` | in-cluster → `$KUBECONFIG` → `~/.kube/config` | Credential source for the allocation API |
| `--transport-key` | `TRANSPORT_KEY` (empty) | Pre-shared key encrypting the KCP listener (32-byte hex recommended). Empty = plaintext, and a KCP listener logs a WARN |
| `--join-token-secret` | `JOIN_TOKEN_SECRET` → `JWT_SECRET` | HS256 secret for gateway→gameserver join tokens. Comma-separated to rotate |
| `--conn-rate-per-min` | `GATEWAY_CONN_RATE_PER_MIN` (`10`) | Accepted connections per minute per source IP. `0` disables |
| `--msg-rate-per-sec` | `GATEWAY_MSG_RATE_PER_SEC` (`60`) | Inbound frames per second per connection. `0` disables |

### Agones allocator

```bash
# Out of cluster (uses ~/.kube/config), against the dev fleets:
go run ./cmd/gateway/ --addr=:8000 \
  --allocator=agones --allocator-namespace=rpg-realtime \
  --allocator-fleet-map=map-servers-dotnet-dev
```

`MsgEnterWorld` for an unserved map POSTs a `GameServerAllocation`
(`allocation.agones.dev/v1`) and answers with the allocated `address:port`; the join
token's `sid` is the allocated GameServer name, which the pod also registers as
(`POD_NAME` → `--server-id`). An exhausted fleet answers `UnAllocated` and surfaces as
`registry.ErrNoCapacity`. In-cluster the gateway's ServiceAccount needs
`create` on `gameserverallocations.allocation.agones.dev` in that namespace.
See `docs/API.md` for the wire flow and `docs/DESIGN.md` for the rationale.

### Security

Three independent secrets, each with a dev-friendly default that is logged loudly:

```bash
# Production shape:
export JWT_SECRET="$(openssl rand -hex 32)"          # Nakama -> client -> gateway
export JOIN_TOKEN_SECRET="$(openssl rand -hex 32)"   # gateway -> game server
export TRANSPORT_KEY="$(openssl rand -hex 32)"       # KCP wire encryption
```

**Secret rotation.** `JWT_SECRET` and `JOIN_TOKEN_SECRET` accept a
comma-separated list. The first entry signs, every entry verifies:

```bash
export JWT_SECRET="new-secret,old-secret"   # 1. deploy — nobody is logged out
# 2. wait out the longest live token TTL (SessionTTL / JoinTokenTTL)
export JWT_SECRET="new-secret"              # 3. deploy — old tokens now rejected
```

`TRANSPORT_KEY` has **no** rotation window — KCP block crypto does not
negotiate, so both peers must be rolled together.

**Rate limiting.** Per-IP on accepts and per-connection on frames, both token
buckets. A connection that trips the message limit receives one
`{"ok":false,"error":"rate limited"}` frame and is then closed. Rejections
increment `gateway_rate_limited_total{reason}`. Both limiters are per process:
N replicas admit N x the limit (ADR-8).

⚠️ **KCP is not reachable end to end.** `gameserver-dotnet` is TCP-only, so
`--transport=kcp` and `TRANSPORT_KEY` cover the client→gateway hop only.

### Backend selection

Resolved in this order: `--backend` → `GATEWAY_BACKEND` → `redis` when `REDIS_ADDR` is
exported → `memory`. (`shared/config` always defaults `RedisAddr` to `localhost:6379`, so
only an *explicitly exported* `REDIS_ADDR` opts into Redis.)

| Backend | Sessions | Registry | Events |
|---------|----------|----------|--------|
| `memory` | `storage.MemorySessionStore` | `storage.MemoryServerRegistry` | `storage.MemoryEventStream` |
| `redis` | `redisstore.SessionStore` | `redisstore.ServerRegistry` (heartbeat TTL) | `redisstore.EventStream` (consumer group + ACK) |

All three Redis stores share one client/pool.

## Configuration

| Env | Default | Used for |
|-----|---------|----------|
| `GATEWAY_ADDR` | `:8000` | Listen address |
| `JWT_SECRET` | `dev-secret-change-me` | Client auth-token verification (shared with Nakama). Comma-separated list to rotate: `new,old` — first signs, all verify |
| `JOIN_TOKEN_SECRET` | *(empty → `JWT_SECRET`)* | Join-token signing (shared with gameserver-dotnet). Also rotatable. Unset logs a warning |
| `TRANSPORT_KEY` | *(empty)* | Pre-shared AES-256 key for the KCP listener. Empty = plaintext |
| `GATEWAY_CONN_RATE_PER_MIN` | `10` | Per-IP connection rate limit (`0` disables) |
| `GATEWAY_CONN_BURST` | `10` | Per-IP burst |
| `GATEWAY_MSG_RATE_PER_SEC` | `60` | Per-connection inbound message rate (`0` disables) |
| `GATEWAY_MSG_BURST` | `120` | Per-connection burst |
| `GATEWAY_TRANSPORT` | `tcp` | Realtime transport (`tcp` or `kcp`) |
| `REDIS_ADDR` | `localhost:6379` | Redis endpoint (also the auto backend switch) |
| `REDIS_PASSWORD` | — | Redis auth |
| `GATEWAY_BACKEND` | — | `memory` \| `redis` |
| `LOG_LEVEL` | `info` | slog level |

See `shared/config` for the full list.

## Logging

Structured JSON on stdout (`shared/logger`, slog). `LOG_LEVEL` selects the
level; the default is `info`.

**One client session produces three info lines** — that is the whole budget,
and it is deliberate. The gateway is not in the gameplay data path (ADR-3), so
every event worth a line happens once per *session*, not once per message:

```json
{"level":"INFO","msg":"auth ok","conn":1,"user":"85330f00-…","ip":"127.0.0.1","dur_ms":2}
{"level":"INFO","msg":"enter world assigned","conn":1,"user":"85330f00-…","map":"map_01","server":"gs-dotnet-map_01","server_addr":"127.0.0.1:9200","transport":"tcp","dur_ms":0}
{"level":"INFO","msg":"client disconnected","conn":1,"user":"85330f00-…","ip":"127.0.0.1","dur_ms":6}
```

`conn` is a process-local connection number, not a durable id. Its only job is
correlation: `grep '"conn":1'` returns one session's complete history,
including the debug lines.

| Event | Level | Frequency |
|-------|-------|-----------|
| `client connected` | debug | per TCP accept — anyone who can open a socket can mint it |
| `auth ok` | info | once per session |
| `auth failed` | warn (error for `reason=session_store`) | **first per connection**, then debug |
| `enter world assigned` | info | once per session |
| `enter world failed` | warn / error | once per attempt |
| `duplicate login detected`, `client evicted`, `published session supersede` | info | once per eviction |
| `session expired` | info | once per session (the identity is cleared with it) |
| `client disconnect` / `client disconnected` | info when the connection had a session, else debug | once per session |
| `unexpected message type` | warn | **first per connection**, then debug |
| ping / pong | *not logged at all* | per message |

Two rules produce that table:

- **Nothing a client can repeat gets a line.** Heartbeats are the only frame a
  connected client repeats, and at 200 clients on a 10s interval that is 20
  frames a second; they are silent. The two rejection lines a client *could*
  drive on demand — a socket looping bad tokens, or one sending gameplay frames
  to the gateway — are latched to the first occurrence per connection, because
  the message limiter's 60 frames/s default still permits 60 lines a second
  from a single socket.
- **Nothing an unauthenticated peer does reaches info.** A connect-and-say-
  nothing scanner is debug-only; a client only earns info lines by presenting a
  frame that verifies.

**Credentials are never logged.** Not the client JWT, not the issued join
token, not the signing secrets. Both tokens are bearer credentials: a log line
holding one is a log line that can be replayed into a session. Auth failures
carry a `reason` and the verifier's error (which says *why* a token was
rejected without quoting it) instead. `TestLogNeverContainsCredentials` scans
the log of a full handshake for all three and fails on a substring match.

User ids *are* logged, consistently with the game server, which prints them on
join — following one player across the two hops depends on it.

## Metrics

A second listener (separate from the realtime port) serves Prometheus metrics
and the health endpoints:

```bash
go run ./cmd/gateway/ --addr=:8000 --metrics-addr=:9102   # default is :9102
curl localhost:9102/metrics
curl localhost:9102/healthz     # 200 "ok" while the process is alive
curl localhost:9102/readyz      # 200 "ready", or 503 "not ready: redis"
```

`METRICS_ADDR` is the env equivalent; `off`, `none` or an explicitly empty
`METRICS_ADDR` disables the listener.

| Metric | Type | Labels |
|--------|------|--------|
| `gateway_connections_active` | gauge | — |
| `gateway_auth_total` | counter | `result=ok\|fail` |
| `gateway_enter_world_total` | counter | `result=ok\|fail` |
| `gateway_allocations_total` | counter | `result=ok\|fail` |
| `gateway_relay_events_total` | counter | — |
| `gateway_rate_limited_total` | counter | `reason=connection\|message` |
| `gateway_redis_up` | gauge | — |
| `gateway_relay_up` | gauge | — |
| `gateway_session_checks_total` | counter | `result=ok\|expired\|store_error` |
| `gateway_stream_group_loss_total` | counter | — |
| `gateway_kick_publish_total` | counter | `result=ok\|fail` |

`gateway_kick_publish_total` counts duplicate-login supersede events published to
the `events:kick` stream (ADR-20). A `fail` is a duplicate login whose old
game-server connection will NOT be kicked — the new login proceeds regardless —
so `rate(gateway_kick_publish_total{result="fail"}[5m]) > 0` is alert-worthy.

### Liveness vs readiness

**`/healthz` is liveness and never checks a dependency. `/readyz` is readiness
and does.** Point the k8s `livenessProbe` at `/healthz` and the
`readinessProbe` at `/readyz`; do not swap them.

Kubernetes restarts a container that fails liveness, but only takes it out of
service on a readiness failure. If Redis gated liveness, a single Redis outage
would fail liveness on every gateway pod simultaneously and trigger a
fleet-wide rolling restart — dropping the connections of players whose
gameplay never touches Redis (the gateway is not in the gameplay data path,
ADR-3) and hitting a recovering Redis with a reconnect storm. A restart cannot
fix a sick dependency. Readiness is the correct lever: stop routing *new*
logins to a gateway that cannot reach Redis, leave the process and its existing
connections alone.

`/readyz` returns only the *names* of failing checks (`not ready: redis`), never
the underlying error text, which carries internal addresses.

### Degraded operation

The gateway is designed to survive Redis being unavailable rather than
crash-loop:

| Redis state | Gateway behaviour |
|-------------|-------------------|
| Down at boot | Listener still binds and serves. The event relay retries in the background (1s → 30s backoff) and attaches when Redis returns. `gateway_relay_up` is 0 meanwhile |
| Blips while running | Live players are **not** de-authenticated. A store error is distinguished from an expired session and fails open; `gateway_session_checks_total{result="store_error"}` increments |
| Wiped / restored | The event-stream consumer group is re-created automatically on `NOGROUP`; `gateway_stream_group_loss_total` increments |

Alerting starting points: `gateway_redis_up == 0`, `gateway_relay_up == 0` for
more than a few minutes, any increase in `gateway_stream_group_loss_total`, and
a rising `rate(gateway_session_checks_total{result="store_error"}[5m])`.

Scraped by the dev stack in `backend/deploy` (`make monitoring-up`) — see
`backend/deploy/docs/MONITORING.md`.

## Test

```bash
go test ./...          # memory + miniredis-backed paths
go vet ./...
```

## Dependencies

- `github.com/duycuong/rpg-mmo/shared`
- `github.com/redis/go-redis/v9` (via `shared/storage/redisstore`)
- `github.com/alicebob/miniredis/v2` (tests)
- KCP-Go library (planned)
