# Gateway Module — Agent Instructions

**Role**: Gateway Engineer (`agent-gateway`)
**Module**: `github.com/duycuong/rpg-mmo/gateway`
**Depends on**: `shared`

## Responsibilities

Custom Go binary — the **entry point** for Unity clients. Standalone process, NOT a Nakama plugin.

> **The gateway is a redirector, not a router.** It authenticates a client, picks a
> game server for the requested map, and returns `{ServerAddr, JoinToken}`. The
> client then connects **directly** to that game server. No gameplay packet
> (`MsgInput`, `MsgSnapshot`) ever passes through this process — `handleMessage`
> only accepts `MsgAuth`, `MsgEnterWorld` and `MsgDisconnect`. See
> `backend/docs/ARCHITECTURE-DECISIONS.md`, ADR-3, for why, and for the tradeoffs
> of switching to proxy mode later.

### 1. TCP / KCP Transport (Drawio Pages 2, 3)
- Listen for client connections (TCP default, KCP/UDP via `--transport=kcp`)
- Session management (connection, disconnection, timeout)
- Route each client to the right *game server address* — an assignment, not packet forwarding

### 2. Session Manager (Drawio Page 2)
- Accept client connection with Session Token (JWT)
- JWT verify LOCAL using shared secret — NO roundtrip to Nakama
- Store session in Redis: `session:{user_id}` = connection info (with TTL)
- Heartbeat: refresh session TTL on KCP ping
- Handle disconnect: cleanup session from Redis

### 3. Server Registry (Drawio Page 3)
> MVP invariant: **one live game server per `map_id`** (ADR-2). Nothing enforces
> it; `FindServer` warns when a map resolves to more than one server. Selection is
> least-loaded with a deterministic `ServerID` tiebreak.
- Maintain registry of active Game Servers in Redis
- Data per server: `server_id`, `map_id`, `addr`, `capacity`, `player_count`, `health`
- Lookup: find available server for `map_id` (capacity check)
- Server registration on startup, deregistration on shutdown
- Health tracking via heartbeat from Game Servers

### 4. Map Assignment (Drawio Page 3)
- Client sends `EnterWorld(map_id)` via KCP
- Lookup server with capacity for that map
- If available: reserve slot, issue `join_token`, redirect client
- If the map has **no** live server: request Agones allocation, then wait (bounded
  by `--allocation-wait-timeout`) for that pod to register **itself**, and issue
  the `join_token` from its own entry. Timeout → `server is starting, retry
  shortly` (retryable), no token
- If the map's live servers are all **full**: refuse with `no server available for
  map`. Never allocate a second server for a `map_id` — that splits the world
  (ADR-2)
- Update player location in Redis: `player:{user_id}:location = server_id`

### 5. Event Relay — Redis Streams (Drawio Page 4)
- Consume Redis Streams events and fan them out to relevant clients
- Streams only (consumer group + ACK). Raw Redis pub/sub is not used — ADR-5
- ⚠️ Current state: the relay consumes and logs, but cannot push to clients yet —
  `shared/messages` has no `MsgEvent` type
- Event types: boss_killed, rare_drop, inventory_changed, player_offline
- Consumer group management for reliable delivery (XREADGROUP + XACK)

### 6. Server Transfer (Drawio Page 6)
- Handle dungeon transfer: save checkpoint -> update location -> redirect to dungeon server
- Handle return transfer: dungeon -> origin map server
- Coordinate with Agones for dungeon instance allocation

## Key Design Constraints
- Must handle thousands of concurrent client connections
- JWT verification is LOCAL (shared secret) — zero network calls to Nakama
- All state in Redis — Gateway itself is stateless (horizontally scalable)
- The hot path is auth + map assignment, NOT packet forwarding (ADR-3)
- Graceful shutdown: drain connections

## Performance Targets

> **⚠️ ESTIMATES — UNBENCHMARKED.** No load test exists. See ADR-7 for the
> benchmark plan. Because the gateway does not forward gameplay packets, its load
> scales with **login rate**, not with CCU.

- Connection handling: 2000+ concurrent clients per instance (unverified)
- Auth p99: < 100ms (proposed threshold, unverified)
- Memory: < 100MB per instance at 1000 CCU (unverified)
- Startup time: < 2s

## Integration Points
- **With Clients**: TCP/KCP, handshake with JWT, then `EnterWorld` → `{ServerAddr, JoinToken}`
- **With Game Servers (C# .NET 10)**: **no runtime connection.** The gateway mints a join token naming a server (`sid` claim); the client dials that server itself
- **With Redis**: Session store, server registry, event-stream (Streams) consumer
- **With Agones**: Allocation requests for new Game Server pods
- **With Nakama**: NONE at runtime (JWT shared secret only)

## Documentation Requirements
- `docs/README.md` — Module overview, network architecture, how to run
- `docs/API.md` — All KCP message types, handshake protocol, join_token format
- `docs/DESIGN.md` — Stateless design, scaling strategy, connection lifecycle
- `docs/RUNBOOK.md` — Deploy, scale, debug connection issues, monitor metrics
- `CHANGELOG.md` — Every change logged

## File Structure Target
```
gateway/
  go.mod
  CLAUDE.md
  CHANGELOG.md
  docs/
    README.md
    API.md
    DESIGN.md
    RUNBOOK.md
  cmd/
    gateway/
      main.go          # Entry point, config load, start server
  server/
    server.go          # Listener, accept loop, message handling
    connection.go      # Per-client connection handler
    # NOTE: no router.go — the gateway does not forward gameplay packets (ADR-3)
  session/
    manager.go         # Session lifecycle (create, refresh, destroy)
    jwt.go             # Local JWT verification
  registry/
    registry.go        # Server registry operations
    allocator.go       # Agones allocation requests
  transfer/
    map_assign.go      # EnterWorld flow
    dungeon.go         # Dungeon transfer flow
    join_token.go      # Token generation and validation
  events/
    relay.go           # Redis Streams consumer + relay
    consumer.go        # Consumer group management
  metrics/
    metrics.go         # Prometheus metrics export
```
