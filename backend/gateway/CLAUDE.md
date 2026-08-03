# Gateway Module — Agent Instructions

**Role**: Gateway Engineer (`agent-gateway`)
**Module**: `github.com/duycuong/rpg-mmo/gateway`
**Depends on**: `shared`

## Responsibilities

Custom Go binary — UDP/KCP router between Unity clients and Game Servers. Standalone process, NOT a Nakama plugin.

### 1. KCP/UDP Transport (Drawio Pages 2, 3)
- Listen on UDP port for KCP connections from Unity clients
- KCP session management (connection, disconnection, timeout)
- Packet routing: client <-> appropriate Game Server
- Connection multiplexing (many clients, many servers)

### 2. Session Manager (Drawio Page 2)
- Accept client connection with Session Token (JWT)
- JWT verify LOCAL using shared secret — NO roundtrip to Nakama
- Store session in Redis: `session:{user_id}` = connection info (with TTL)
- Heartbeat: refresh session TTL on KCP ping
- Handle disconnect: cleanup session from Redis

### 3. Server Registry (Drawio Page 3)
- Maintain registry of active Game Servers in Redis
- Data per server: `server_id`, `map_id`, `addr`, `capacity`, `player_count`, `health`
- Lookup: find available server for `map_id` (capacity check)
- Server registration on startup, deregistration on shutdown
- Health tracking via heartbeat from Game Servers

### 4. Map Assignment (Drawio Page 3)
- Client sends `EnterWorld(map_id)` via KCP
- Lookup server with capacity for that map
- If available: reserve slot, issue `join_token`, redirect client
- If not available: request Agones allocation, wait for new server, then redirect
- Update player location in Redis: `player:{user_id}:location = server_id`

### 5. Pub/Sub Events (Drawio Page 4)
- Forward Redis Streams events to relevant clients/servers
- Event types: boss_killed, rare_drop, inventory_changed, player_offline
- Consumer group management for reliable delivery (XREADGROUP + XACK)

### 6. Server Transfer (Drawio Page 6)
- Handle dungeon transfer: save checkpoint -> update location -> redirect to dungeon server
- Handle return transfer: dungeon -> origin map server
- Coordinate with Agones for dungeon instance allocation

## Key Design Constraints
- Must handle thousands of concurrent KCP connections
- JWT verification is LOCAL (shared secret) — zero network calls to Nakama
- All state in Redis — Gateway itself is stateless (horizontally scalable)
- Packet forwarding must be low-latency (< 1ms overhead)
- Graceful shutdown: drain connections, notify servers

## Performance Targets
- Connection handling: 2000+ concurrent clients per instance
- Packet forwarding latency: < 1ms
- Memory: < 100MB per instance at 1000 CCU
- Startup time: < 2s

## Integration Points
- **With Clients**: KCP/UDP (realtime), initial handshake with JWT
- **With Game Servers**: KCP/UDP forwarding, join_token protocol
- **With Redis**: Session store, server registry, pub/sub relay
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
    server.go          # KCP listener, accept loop
    connection.go      # Per-client connection handler
    router.go          # Packet routing to Game Servers
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
