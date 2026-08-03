# GameServer Module

Custom Go binary (~50MB RAM/pod) — runs as Map Server or Dungeon Server. Managed by Agones on k3s.

## Modes

```bash
# Map Server mode (persistent open-world)
go run ./cmd/gameserver/ --mode=map --map-id=forest_01

# Dungeon Server mode (instanced per party)
go run ./cmd/gameserver/ --mode=dungeon --dungeon-id=boss_cave
```

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

## Dependencies

- `github.com/duycuong/rpg-mmo/shared`
- Agones Go SDK
