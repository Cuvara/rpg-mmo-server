# GameServer Module — Agent Instructions

**Role**: GameServer Engineer (`agent-gameserver`)
**Module**: `github.com/duycuong/rpg-mmo/gameserver`
**Depends on**: `shared`

## Responsibilities

Custom Go binary (~50MB RAM/pod) — runs as Map Server or Dungeon Server depending on launch config. Managed by Agones on k3s.

### 1. Tick Loop — Server Authoritative (Drawio Page 4)
- Fixed tick rate: 10-15 Hz (configurable)
- Per tick: gather inputs -> validate -> update world state -> broadcast snapshots
- Tick must NOT block on I/O (DB saves are async)
- World state: entity positions, HP, buffs, cooldowns, projectiles

### 2. Input Validation & Anti-Cheat
- Validate every client input: cooldown, range, speed, legal actions
- Reject invalid inputs silently (log for analytics)
- Speed hack detection: distance/tick threshold
- Cooldown enforcement: server-side cooldown tracking

### 3. Combat & Skill System (Drawio Page 4)
- Damage calculation: attacker stats vs defender stats
- Skill execution: validate cooldown -> check range -> apply effect -> broadcast
- Status effects: buff/debuff with duration tracking
- Death handling: drop loot, respawn timer, notify clients

### 4. AI / NPC Behavior (Drawio Page 4)
- NPC state machine: idle, patrol, chase, attack, flee
- Aggro system: threat table, aggro range, leash distance
- Spawn management: spawn points, respawn timers, wave spawning
- Boss mechanics: phase transitions, special abilities, wipe check

### 5. World State & Snapshots (Drawio Page 4)
- AOI (Area of Interest) based snapshot broadcast
- Delta compression: only send changed state
- Full snapshot on request (after lag spike)
- Entity lifecycle: spawn, update, despawn events

### 6. Loot System (Drawio Page 4)
- Server-side loot roll ONLY (never trust client)
- Loot tables: drop rate, rarity weights, guaranteed drops
- Per-player loot assignment (no ninja-looting)
- Grant rewards via internal RPC to Nakama

### 7. Map Server Specifics (Drawio Page 3)
- Open-world map hosting (persistent)
- Player join/leave with capacity management
- Heartbeat to Redis: player_count, health, load
- Register/deregister with server registry
- Entity hold on disconnect: 30s reconnect window

### 8. Dungeon Server Specifics (Drawio Page 6)
- Instance per party (isolated)
- 4 phases: allocate -> transfer party -> gameplay -> cleanup
- Reconnect window: 60s (party waiting)
- Boss AI with mechanics, wipe check
- On clear: roll loot per player, grant rewards via Nakama RPC, submit clear_time
- On fail (wipe/timeout/abandon): dungeon failed notification
- Cleanup: final save -> update location to origin map -> transfer back -> deregister -> shutdown pod (5min idle reclaim)

### 9. State Persistence (Drawio Page 4)
- Async batch save every 30-60s (HP, position, inventory) — does NOT block tick
- Checkpoint save before dungeon transfer
- Final save on player disconnect (after 30s hold expires)
- Save to PostgreSQL (game state DB)

### 10. Cross-Server Events
- XADD to Redis Streams: boss_killed, rare_drop, player_offline
- Consumer for incoming events: inventory_changed (refresh player data)

## Key Design Constraints
- Tick loop MUST be deterministic and fast (< 66ms at 15Hz)
- NO synchronous DB/network calls in tick loop
- All I/O (save, RPC, events) via goroutine workers with channels
- Memory budget: ~50MB per pod at 100 players
- Binary must work as both Map Server and Dungeon Server (launch flag)

## Performance Targets
- Tick processing: < 20ms for 100 entities
- Memory: < 50MB per pod
- Snapshot size: < 2KB delta per tick per client (AOI filtered)
- Save latency: async, < 100ms batch

## Integration Points
- **With Gateway**: Receive forwarded KCP packets, join_token validation
- **With Redis**: Heartbeat, server registry, Redis Streams (pub/sub)
- **With PostgreSQL (game state)**: Async batch saves, checkpoint, final save
- **With Nakama**: Internal RPC for reward granting (signed, no external network)
- **With Agones**: SDK health ping, ready/shutdown signals

## Documentation Requirements
- `docs/README.md` — Module overview, how to run (map vs dungeon mode), architecture
- `docs/API.md` — All input message types, snapshot format, event types
- `docs/DESIGN.md` — Tick loop design, AOI algorithm, loot table format, AI state machine
- `docs/RUNBOOK.md` — Deploy via Agones, debug tick performance, investigate cheats
- `CHANGELOG.md` — Every change logged

## File Structure Target
```
gameserver/
  go.mod
  CLAUDE.md
  CHANGELOG.md
  docs/
    README.md
    API.md
    DESIGN.md
    RUNBOOK.md
  cmd/
    gameserver/
      main.go            # Entry point, mode flag (map/dungeon), config
  server/
    server.go            # Server lifecycle (init, run, shutdown)
    tick.go              # Main tick loop
    world.go             # World state container
  combat/
    damage.go            # Damage calculation
    skill.go             # Skill execution and validation
    status_effect.go     # Buff/debuff system
    death.go             # Death handling, respawn
  ai/
    state_machine.go     # NPC behavior states
    aggro.go             # Threat table, aggro system
    spawner.go           # Spawn management
    boss.go              # Boss mechanics
  movement/
    movement.go          # Position update, collision
    validation.go        # Speed hack detection
  snapshot/
    aoi.go               # Area of Interest filtering
    delta.go             # Delta compression
    encoder.go           # Snapshot serialization
  loot/
    loot.go              # Loot roll logic
    tables.go            # Loot table definitions
  input/
    handler.go           # Input message processing
    validator.go         # Anti-cheat validation
  persistence/
    saver.go             # Async batch save worker
    checkpoint.go        # Dungeon checkpoint
  dungeon/
    instance.go          # Dungeon instance lifecycle
    phase.go             # Phase management (allocate -> gameplay -> cleanup)
    transfer.go          # Party transfer in/out
  events/
    publisher.go         # Redis Streams XADD
    consumer.go          # Redis Streams XREADGROUP
  agones/
    sdk.go               # Agones SDK integration
  metrics/
    metrics.go           # Prometheus metrics
```
