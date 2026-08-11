# RPG MMO Indie

Server-authoritative multiplayer RPG — mobile/PC client with open-world maps + instanced dungeons.

## Architecture

```
Unity Client
  ├── HTTPS/WebSocket ──→ Nakama (auth, economy, social, leaderboard)
  │
  ├── TCP/KCP ──→ Gateway          (auth + "which server?" ONLY)
  │                  │  returns {ServerAddr, JoinToken}
  │                  ├── Redis (sessions, registry, event streams)
  │                  └── PostgreSQL (meta DB via Nakama)
  │
  └── TCP/KCP ──→ Game Servers     (combat, movement, world state)
                     └── PostgreSQL (game state DB)
```

The gateway is a **redirector, not a proxy**: it authenticates the client and tells
it which game server to use, then the client opens a *second, direct* connection to
that server. Gameplay traffic never passes through the gateway.

📖 **[Architecture Decisions](backend/docs/ARCHITECTURE-DECISIONS.md)** — data ownership, sharding policy, gateway role, Redis roles, event delivery, crash recovery, and the benchmark plan. **Start here.**

📖 **[Core Flow](backend/docs/CORE_FLOW.md)** — end-to-end walkthrough: login→gameplay sequence, tick loop internals, cross-server events, deployment topology, extension seams. ⚠️ Written against the deleted Go game server; parts of it are stale — Architecture Decisions wins any conflict.

### Backend Modules

| Module | Path | Description |
|--------|------|-------------|
| **shared** | `backend/shared/` | Config, JWT, wire protocol, storage interfaces, error codes |
| **gateway** | `backend/gateway/` | TCP/KCP listener (auth + redirect, not a gameplay proxy), JWT auth, session manager, server registry, map assignment, event relay |
| **gameserver-dotnet** | `backend/gameserver-dotnet/` | C# .NET 10 game server — tick loop, combat, input validation, AOI, persistence. Shared game logic with Unity client |
| **nakama** | `backend/nakama/` | Nakama Go plugins — auth, economy, leaderboard, social (planned) |
| **deploy** | `backend/deploy/` | Docker, k3s/Agones manifests, CI/CD, dev observability (`grafana/otel-lgtm` — see `backend/deploy/docs/MONITORING.md`) |

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

### Run the whole backend — one command

```bash
cd backend/deploy
./stack.sh up        # build every image and start the whole stack
./stack.sh check     # prove the full client flow end to end
./stack.sh down      # stop it
```

This brings up Nakama, both PostgreSQL instances, Redis, the Go gateway and the
C# game server, wired with matching secrets, and waits until the game server has
registered itself so `MsgEnterWorld` can be answered. `check` runs
`backend/smoketest`, which walks the same path a Unity client walks — device
auth → `gateway_token` → `MsgAuth` → `MsgEnterWorld` → game-server join →
input/snapshot → clean disconnect — and prints a PASS line per step.

Only Docker is required. **A client connects to the gateway on `localhost:8100`
and to Nakama on `http://localhost:7350`**; it learns the game-server address at
runtime from `MsgEnterWorldResp` and dials it directly (ADR-3). Ports, secrets,
pointing a Unity client at it, running a second isolated stack, and
troubleshooting: **[`backend/deploy/docs/RUNBOOK-local-dev.md`
§0](backend/deploy/docs/RUNBOOK-local-dev.md#0-run-the-whole-thing-locally)**.

The sections below are for working *on* a single service, where you want host
processes and a debugger rather than containers.

### Prerequisites

- Docker + `docker compose` v2 (for the stack above)
- Go 1.26+ and .NET 10 SDK (to run services on the host, and for the tests)

### Run Tests

```bash
# Go modules (each is its own Go module — cd first)
cd backend/shared && go test ./... -race
cd backend/gateway && go test ./... -race
cd backend/nakama && go test ./... -race
cd backend/integration_test && go test -tags integration -v -race -timeout 300s

# C# game server (.NET 10)
cd backend/gameserver-dotnet && dotnet test
```

`-race` needs cgo (a C toolchain). Drop it if `gcc` is unavailable — the suites
are identical otherwise.

### Run Servers

In-memory mode (single process each, no external dependencies):

```bash
# Terminal 1 — Game Server (C# .NET 10)
cd backend/gameserver-dotnet
dotnet run --project GameServer/ -- --addr=:9000 --map-id=map_01

# Terminal 2 — Gateway (Go)
cd backend/gateway
go run ./cmd/gateway/ --addr=:8000
```

Redis mode (shared server registry, sessions and event stream — required for
the gateway to see game servers running in other processes):

```bash
# Terminal 0 — Redis
redis-server --port 6379

# Terminal 1 — Game Server (C# .NET 10)
cd backend/gameserver-dotnet
dotnet run --project GameServer/ -- --addr=:9000 --map-id=map_01 --redis --redis-addr=localhost:6379

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
| Transport | TCP default; KCP implemented (`xtaci/kcp-go`, `--transport=kcp`) | KCP everywhere |
| Encoding | **Protobuf + entity-type enum + id interning** (`shared/proto/wire.proto`, one schema -> Go + C#), **81% smaller than the JSON it replaced**; legacy JSON still accepted, distinguished by the first body byte (ADR-9) | Protobuf only, once no pre-Protobuf client remains |
| Player Store | In-memory default; PostgreSQL implemented (`GAME_DB_URL`) | PostgreSQL |
| Session Store | In-memory default; Redis implemented (`--backend=redis`) | Redis |
| Server Registry | In-memory default; Redis implemented (`--backend=redis`) | Redis hash |
| Event Stream | Go channels default; Redis Streams implemented (+ACK). C# publishes into a noop | Redis Streams end to end |
| JWT | Custom HS256 | `golang-jwt/jwt/v5` |
| AOI | Brute-force | Spatial grid / quadtree |
| Orchestration | Manual | Agones on k3s |

## Test Results

| Module | Tests | Status |
|--------|-------|--------|
| shared | 39 | ✅ |
| gameserver-dotnet | 30 | ✅ |
| gateway | 36 | ✅ |
| nakama | 11 | ✅ |
| integration (E2E) | 10 | ✅ |
| **Total** | **126** | **All green** |

Counts are top-level `go test` functions (most are table-driven with several
subtests each).

## Deployment Tiers

**The players-per-game-server ceiling is currently unknown.** This section used
to state 150, measured when snapshot JSON serialisation was the bottleneck. That
bottleneck no longer exists: Protobuf, the entity-type enum and entity-id
interning removed **81%** of the wire (ADR-9), which is the constraint the 150
came from. Quoting it now would be quoting a number about a system that no
longer runs.

What *is* measured, and reproducible to 0.3% across six runs:

| Metric | Measured |
|--------|----------|
| Downstream bandwidth | **45.9 KB/s per client at 200 players** — inside ADR-7's `< 50 KB/s` mobile threshold |
| RAM per game server | **~30 MiB idle → ~82 MiB at 200 players** — inside the 128Mi pod limit |
| Wire encoding | Protobuf + type enum + id interning, **81% smaller than the original JSON** |

A replacement ceiling cannot be measured on the current hardware: the load
generator shares a host with the server under test and uses more CPU than it, and
tick p99 swung **3.3×** on that alone. Every tick figure from that host is a lower
bound of unknown tightness. The unblock is a separate machine for the load
generator and nothing else — tracked in
[ADR-7](backend/docs/ARCHITECTURE-DECISIONS.md). Full report:
[BENCHMARK.md](backend/docs/BENCHMARK.md).

> **⚠️ Costs and tier CCU below are estimates** — no VPS load test has been run.

| Tier | Cost/mo ⚠️ | CCU ⚠️ |
|------|---------|-----|
| Dev/Alpha | $40-60 | < 200 |
| Beta | $80-150 | 200-500 |
| Soft Launch | $200-400 | 500-2000 |
| Growth | $400-1000+ | 2000-5000+ |

The **"Game servers @ 150" column has been removed.** It divided tier CCU by a
retracted figure, propagating it into the one place someone sizing a fleet would
actually read.

All open-source stack: Nakama, k3s, Agones, PostgreSQL, Redis — $0 license.

## CI/CD

GitHub Actions pipeline: test shared → test gameserver-dotnet + gateway (parallel) → integration test → build binaries.

CD deploys run a post-deploy smoke test (`backend/smoketest`) that exercises the full login → gameplay flow against the freshly deployed stack; any broken step fails the deploy.

Alongside the binary bundle, CD builds container images for gateway (distroless) and gameserver-dotnet (NativeAOT) (`backend/deploy/docker/`) and pushes them to `ghcr.io/dycuong03/rpg-mmo-{gateway,gameserver}` — production refs only, or `workflow_dispatch` with `build_images=true`. These are the images the Agones fleets pull. Locally: `scripts/build-all.sh --images`.

A separate `ci-dotnet.yml` workflow handles the C# game server: `dotnet build` + `dotnet test` on each push.

## Deploy

🚀 **[VPS Setup](backend/deploy/docs/VPS-SETUP.md)** — bring a new machine online as a deploy target, start to finish: bootstrap the box, create the GitHub Environment (complete secret/variable reference), first deploy, verification checklist, moving an environment between machines, troubleshooting. **Start here for anything deployment-related.**

Two commands, no code changes:

```bash
sudo RUNNER_TOKEN=<token> ./scripts/bootstrap-vps.sh --labels staging   # on the VPS
./scripts/setup-github-env.sh staging --generate                        # anywhere with gh
```

Supporting docs: [CICD.md](backend/deploy/docs/CICD.md) (pipeline internals, deploy modes, rollback) · [MONITORING.md](backend/deploy/docs/MONITORING.md) (Grafana/Prometheus) · [RUNBOOK-local-dev.md](backend/deploy/docs/RUNBOOK-local-dev.md) (laptop stack) · [K3S.md](backend/deploy/docs/K3S.md) (k3s + Agones tier).

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
├── gameserver-dotnet/   # C# .NET 10 game server (NativeAOT)
│   ├── GameServer/      # Entry point, tick loop, Arch ECS world, AOI, persistence
│   ├── Shared.GameLogic/  # Deterministic sim, shipped to Unity as a UPM package
│   └── GameServer.Tests/  # xUnit suite, incl. the golden-vector conformance gate
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
