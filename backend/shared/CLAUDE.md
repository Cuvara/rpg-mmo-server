# Shared Module — Agent Instructions

**Role**: Shared Architect (`agent-shared`)
**Module**: `github.com/duycuong/rpg-mmo/shared`
**Priority**: Foundation — build first, other modules depend on this.

## Responsibilities

### 1. Protocol Buffers / FlatBuffers
- Define ALL wire-format messages shared between client and server
- Input commands (move, skill, attack)
- Snapshot messages (world state, delta, entity updates)
- Event messages (boss_killed, rare_drop, inventory_changed, season_ended, player_offline)
- RPC request/response for Nakama custom RPCs
- Auth/session handshake messages

### 2. Common Types & Constants
- Game entity types (player, NPC, mob, item, projectile)
- Error codes enum (shared between all services)
- Status codes for economy transactions
- Redis key patterns as constants (e.g., `SessionKey = "session:{user_id}"`)
- TTL constants (session, entity hold, dungeon reconnect)
- Tick rate constants (10-15Hz simulation, 60fps render)

### 3. Database Models
- Schema definitions for PostgreSQL (meta DB + game state DB)
- Migration files (numbered, idempotent)
- Query interfaces (sqlc recommended or repository pattern)
- Tables: accounts, player_states, inventory, wallet, leaderboard_archive, dungeon_checkpoints

### 4. Redis Helpers
- Connection pool wrapper with health check
- Session store interface (SET/GET/DEL with TTL)
- Server registry interface (register, deregister, lookup by map_id)
- Redis Streams wrapper (XADD, XREADGROUP, XACK) for cross-server events
- Pub/Sub wrapper for simple notifications

### 5. Configuration
- Shared config struct (DB, Redis, Nakama, ports, tick rates)
- Environment variable loader with validation
- Separate config profiles: dev, beta, launch, growth

### 6. Utilities
- JWT helper (sign/verify with shared secret — used by Gateway)
- Idempotency key generator and validator
- Rate limiter interface
- Health check endpoint helper

## Key Design Constraints
- Zero dependency on nakama/gateway/gameserver modules
- Keep imports minimal — this is the dependency root
- Protobuf generated code goes to `proto/gen/` subdirectory
- All public types need GoDoc comments

## Documentation Requirements
- `docs/README.md` — Module overview, how to generate proto
- `docs/API.md` — All proto message types and their usage context
- `docs/DESIGN.md` — Schema design decisions, key pattern rationale
- `CHANGELOG.md` — Every change logged

## File Structure Target
```
shared/
  go.mod
  CLAUDE.md
  CHANGELOG.md
  docs/
    README.md
    API.md
    DESIGN.md
  proto/
    auth.proto
    gameplay.proto
    economy.proto
    events.proto
    gen/           # generated Go code
  config/
    config.go
    env.go
  models/
    player.go
    inventory.go
    wallet.go
    leaderboard.go
  redis/
    client.go
    session.go
    registry.go
    streams.go
  jwt/
    jwt.go
  errors/
    codes.go
  constants/
    tick.go
    ttl.go
    keys.go
```
