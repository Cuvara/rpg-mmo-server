# RPG MMO Indie

Server-authoritative multiplayer RPG — mobile/PC client with open-world maps + instanced dungeons.

## Architecture

```
Unity Client
  ├── HTTPS/WebSocket ──→ Nakama (auth, economy, social, leaderboard)
  └── UDP/KCP ──→ Gateway ──→ Game Servers (combat, movement, world state)
                     │
                     ├── Redis (sessions, registry, pub/sub)
                     └── PostgreSQL (meta DB + game state DB)
```

📖 **[Core Flow](backend/docs/CORE_FLOW.md)** — canonical end-to-end walkthrough: login→gameplay sequence, tick loop internals, cross-server events, deployment topology, extension seams (with ✅/🟡/⬜ implementation status).

### Backend Modules

| Module | Path | Description |
|--------|------|-------------|
| **shared** | `backend/shared/` | Config, JWT, wire protocol, storage interfaces, error codes |
| **gateway** | `backend/gateway/` | TCP/KCP router, JWT auth, session manager, server registry, map assignment |
| **gameserver** | `backend/gameserver/` | Tick loop (10-15Hz), world state, combat, input validation, AOI snapshots, persistence |
| **nakama** | `backend/nakama/` | Nakama Go plugins — auth, economy, leaderboard, social (planned) |
| **deploy** | `backend/deploy/` | Docker, k3s/Agones manifests, CI/CD, monitoring (planned) |

### Connection Flow

```
1. Client → Gateway:    MsgAuth { JWT }
2. Gateway → Client:    MsgAuthResp { OK, UserID }
3. Client → Gateway:    MsgEnterWorld { MapID }
4. Gateway → Client:    MsgEnterWorldResp { ServerAddr, JoinToken }
5. Client → GameServer: MsgJoinToken { Token }
6. GameServer → Client: MsgJoinTokenResp { OK }
7. Client → GameServer: MsgInput { MoveX, MoveY, AttackTargetID }  (per tick)
8. GameServer → Client: MsgSnapshot { Tick, Entities[] }            (per tick)
```

## Quick Start

### Prerequisites

- Go 1.26+

### Run Tests

```bash
# All modules (each is its own Go module — cd first)
cd backend/shared && go test ./... -race
cd backend/gameserver && go test ./... -race
cd backend/gateway && go test ./... -race
cd backend/nakama && go test ./... -race
cd backend/integration_test && go test -v -race -timeout 120s
```

`-race` needs cgo (a C toolchain). Drop it if `gcc` is unavailable — the suites
are identical otherwise.

### Run Servers

In-memory mode (single process each, no external dependencies):

```bash
# Terminal 1 — Game Server
cd backend/gameserver
go run ./cmd/gameserver/ --addr=:9000 --map-id=map_01

# Terminal 2 — Gateway
cd backend/gateway
go run ./cmd/gateway/ --addr=:8000
```

Redis mode (shared server registry, sessions and event stream — required for
the gateway to see game servers running in other processes):

```bash
# Terminal 0 — Redis
redis-server --port 6379

# Terminal 1 — Game Server (--redis opts in; --redis-addr overrides REDIS_ADDR)
cd backend/gameserver
go run ./cmd/gameserver/ --addr=:9000 --map-id=map_01 --redis --redis-addr=localhost:6379

# Terminal 2 — Gateway (an exported REDIS_ADDR selects the redis backend;
# --backend=redis forces it explicitly)
cd backend/gateway
REDIS_ADDR=localhost:6379 go run ./cmd/gateway/ --addr=:8000 --backend=redis
```

### Test with netcat

```bash
# Connect to gateway, then send JSON messages manually
nc localhost 8000
```

## Design Principles

- **Server-authoritative**: all game logic validated server-side
- **Interfaces everywhere**: DB, Redis, transport behind interfaces — swap in-memory for production impls
- **Extensible**: TCP → KCP, JSON → Protobuf, in-memory → PostgreSQL/Redis — zero business logic changes

### Extension Seams

| Layer | MVP (current) | Production |
|-------|---------------|------------|
| Transport | TCP | KCP (`xtaci/kcp-go`) |
| Encoding | JSON structs | Protobuf |
| Player Store | In-memory | PostgreSQL (`pgx`) |
| Session Store | In-memory | Redis (`go-redis`) |
| Server Registry | In-memory | Redis hash |
| Event Stream | Go channels | Redis Streams |
| JWT | Custom HS256 | `golang-jwt/jwt/v5` |
| AOI | Brute-force | Spatial grid / quadtree |
| Orchestration | Manual | Agones on k3s |

## Test Results

| Module | Tests | Status |
|--------|-------|--------|
| shared | 39 | ✅ |
| gameserver | 30 | ✅ |
| gateway | 36 | ✅ |
| nakama | 11 | ✅ |
| integration (E2E) | 10 | ✅ |
| **Total** | **126** | **All green** |

Counts are top-level `go test` functions (most are table-driven with several
subtests each).

## Deployment Tiers

| Tier | Cost/mo | CCU |
|------|---------|-----|
| Dev/Alpha | $40-60 | < 200 |
| Beta | $80-150 | 200-500 |
| Soft Launch | $200-400 | 500-2000 |
| Growth | $400-1000+ | 2000-5000+ |

All open-source stack: Nakama, k3s, Agones, PostgreSQL, Redis — $0 license.

## CI/CD

GitHub Actions pipeline: test shared → test gameserver + gateway (parallel) → integration test → build binaries.

CD deploys run a post-deploy smoke test (`backend/smoketest`) that exercises the full login → gameplay flow against the freshly deployed stack; any broken step fails the deploy.

Alongside the binary bundle, CD builds distroless container images for gateway and gameserver (`backend/deploy/docker/`) and pushes them to `ghcr.io/dycuong03/rpg-mmo-{gateway,gameserver}` — production refs only, or `workflow_dispatch` with `build_images=true`. These are the images the Agones fleets pull. Locally: `scripts/build-all.sh --images`.

## Project Structure

```
backend/
├── shared/              # Foundation — no deps
│   ├── config/          # Env-based config
│   ├── constants/       # Tick rates, TTLs, Redis keys
│   ├── errors/          # Error codes
│   ├── jwt/             # HS256 sign/verify
│   ├── logger/          # slog wrapper
│   ├── messages/        # Wire protocol (Envelope + codec)
│   └── storage/         # Interfaces + in-memory impls
├── gameserver/          # Depends on shared
│   ├── cmd/gameserver/  # Entry point
│   ├── game/            # Entity, World
│   ├── server/          # Server lifecycle, tick loop, connections
│   ├── input/           # Input handler + validator
│   ├── combat/          # Damage calc, death
│   ├── snapshot/        # AOI + encoder
│   ├── persistence/     # Async batch saver
│   └── events/          # Event publisher
├── gateway/             # Depends on shared
│   ├── cmd/gateway/     # Entry point
│   ├── server/          # Gateway server, connections
│   ├── session/         # JWT verify, session manager
│   ├── registry/        # Server registry, allocator stub
│   ├── transfer/        # Join token, map assignment
│   └── events/          # Event relay stub
├── integration_test/    # E2E tests
├── nakama/              # Planned
└── deploy/              # Planned
```

## License

Private — all rights reserved.
