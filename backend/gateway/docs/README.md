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
| Server Registry | Live servers per map, least-loaded pick with capacity check | ✅ |
| Map Assignment | `MsgEnterWorld` → server addr + 30s join token | ✅ |
| Event Relay | Consumes the cross-server event stream (log-only sink — see DESIGN.md) | 🟡 |
| Allocator | Agones allocation on "no server with capacity" | ⬜ stub |
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
| `--allocator` | `ALLOCATOR` (`none`) | `none` or `agones` — allocate a GameServer when no live server serves a map |
| `--allocator-namespace` | `rpg-realtime` | Namespace holding the Agones fleets |
| `--allocator-fleet-map` | `map-servers-dev` | Fleet used for map allocations |
| `--allocator-fleet-dungeon` | `dungeon-servers-dev` | Fleet used for dungeon allocations |
| `--allocator-transport` | `ALLOCATOR_TRANSPORT` → `--transport` | Transport the allocated fleet's game servers listen with. Must match the fleet manifest's `--transport` argument |
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
  --allocator-fleet-map=map-servers-dev
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

## Metrics

A second listener (separate from the realtime port) serves Prometheus metrics
and a liveness endpoint:

```bash
go run ./cmd/gateway/ --addr=:8000 --metrics-addr=:9102   # default is :9102
curl localhost:9102/metrics
curl localhost:9102/healthz     # 200 "ok" while the process is alive
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
