# Gateway Module

Custom Go binary — router between Unity clients and Game Servers. Stateless
(all state lives in the configured stores), horizontally scalable.

Transport today is **TCP + length-prefixed JSON** (`shared/messages`); KCP/UDP is the
production target and swaps in behind the same handler code.

## Architecture

```
Unity Client --[TCP (KCP planned)]--> Gateway --> (join token) --> Game Server
                                        |
                                        +--> SessionStore   (memory | Redis)
                                        +--> ServerRegistry (memory | Redis)
                                        +--> EventStream    (memory | Redis Streams)
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
| `--allocator-kubeconfig` | in-cluster → `$KUBECONFIG` → `~/.kube/config` | Credential source for the allocation API |

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
| `JWT_SECRET` | `dev-secret-change-me` | Local auth-token + join-token signing (shared with Nakama and gameserver) |
| `GATEWAY_TRANSPORT` | `tcp` | Realtime transport (`tcp` or `kcp`) |
| `REDIS_ADDR` | `localhost:6379` | Redis endpoint (also the auto backend switch) |
| `REDIS_PASSWORD` | — | Redis auth |
| `GATEWAY_BACKEND` | — | `memory` \| `redis` |
| `LOG_LEVEL` | `info` | slog level |

See `shared/config` for the full list.

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
