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

# Redis backend (multi-instance; shared sessions, registry, event stream)
REDIS_ADDR=127.0.0.1:6379 go run ./cmd/gateway/
# or explicitly
go run ./cmd/gateway/ --backend=redis --instance-id=gw-1
```

### Flags

| Flag | Default | Description |
|------|---------|-------------|
| `--addr` | `GATEWAY_ADDR` (`:8000`) | Listen address; overrides the env value |
| `--backend` | auto (see below) | `memory` or `redis` |
| `--instance-id` | hostname | Consumer name inside the `gateway` event-stream consumer group |

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
