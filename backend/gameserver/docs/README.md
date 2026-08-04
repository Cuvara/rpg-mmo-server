# GameServer Module

Custom Go binary (~50MB RAM/pod) — runs as Map Server or Dungeon Server. Managed by Agones on k3s.

## Modes

```bash
# Map Server mode (persistent open-world) — 30s reconnect hold
go run ./cmd/gameserver/ --mode=map --map-id=forest_01

# Dungeon Server mode (instanced per party) — 60s reconnect hold
go run ./cmd/gameserver/ --mode=dungeon --map-id=boss_cave

# Shared Redis registry + event stream (gateway can then find this server)
go run ./cmd/gameserver/ --mode=map --map-id=forest_01 --redis --redis-addr=localhost:6379

# Durable player state in PostgreSQL (postgres-game from backend/deploy/docker-compose.yml)
GAME_DB_URL='postgres://game:localdev@localhost:5433/gamestate?sslmode=disable' \
  go run ./cmd/gameserver/ --mode=map --map-id=map_01
```

On boot the server logs which player store is active:
`using postgres player store` (with a password-redacted DSN) or
`using in-memory player store`. With PostgreSQL it runs the idempotent
`player_states` migration before accepting connections and exits non-zero if
the database is unreachable.

## Flags

| Flag | Default | Description |
|------|---------|-------------|
| `--mode` | `map` | `map` or `dungeon`. Selects the reconnect hold window (30s / 60s). |
| `--addr` | `GAMESERVER_ADDR` | Listen address override. |
| `--map-id` | `map_01` | Map/dungeon id hosted by this instance; registered in the server registry. |
| `--server-id` | `gs-<mode>-<map-id>` | Unique server id. Join tokens must carry this value in their `sid` claim. |
| `--capacity` | `100` | Max players; the gateway's allocator filters on it. |
| `--agones` | `false` | Use the real Agones SDK instead of the noop SDK. |
| `--redis` | `false` | Use the Redis-backed server registry + event stream instead of in-memory. |
| `--redis-addr` | `REDIS_ADDR` | Redis address override (only meaningful with `--redis`). |
| `--game-db-url` | `GAME_DB_URL` | PostgreSQL DSN for game state. Empty = in-memory player store. |

## Environment

| Var | Default | Used for |
|-----|---------|----------|
| `GAMESERVER_ADDR` | `:9000` | Listen address |
| `TICK_RATE` | `10` | Simulation Hz (min 5, max 15) |
| `JWT_SECRET` | `dev-secret-change-me` | Join-token verification (shared with Nakama/Gateway) |
| `REDIS_ADDR` | `localhost:6379` | Registry + event stream when `--redis` is set |
| `REDIS_PASSWORD` | *(empty)* | Redis auth |
| `GAME_DB_URL` | *(empty)* | Game state PostgreSQL DSN. Empty = in-memory player store (state lost on restart). Example: `postgres://game:localdev@localhost:5433/gamestate?sslmode=disable` |
| `LOG_LEVEL` | `info` | slog level |

Timing constants come from `shared/constants`: `EntityHoldTTL` 30s, `DungeonHoldTTL` 60s,
`ServerHeartbeatTTL` 15s (heartbeat fires every TTL/3 = 5s), `JoinTokenTTL` 30s.

## Architecture

```
Tick Loop (10-15 Hz):
  1. Gather inputs (from KCP)
  2. Validate (anti-cheat)
  3. Update AI/NPC
  4. Apply combat/movement
  5. Broadcast snapshots (AOI-filtered, delta-compressed)
  
Async Workers:
  - Batch save (30-60s) -> PostgreSQL
  - Event publish -> Redis Streams
  - Reward grant -> Nakama internal RPC
```

## Features

| Feature | Description |
|---------|-------------|
| Tick Loop | Fixed 10-15Hz server-authoritative simulation |
| Combat | Damage calc, skills, status effects, death |
| AI/NPC | State machine, aggro, spawning, boss mechanics |
| Loot | Server-side roll, per-player assignment |
| Snapshots | AOI-based, delta-compressed broadcasts |
| Persistence | Async batch save, checkpoint, final save |
| Dungeon | Instanced per party, 4-phase lifecycle |
| Registry | Self-registration + heartbeat, player-count updates, deregister on shutdown |
| Reconnect | Entity held 30s (map) / 60s (dungeon) after disconnect; rejoin reattaches |
| Events | `player_death` / `boss_killed` published to `events:game` |

## Dependencies

- `github.com/duycuong/rpg-mmo/shared`
- Agones Go SDK
