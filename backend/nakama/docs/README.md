# Nakama Module

Nakama Go runtime plugins for RPG MMO meta services. Runs inside the Nakama process.

## Services

| Service | Description |
|---------|-------------|
| Auth | Device, email, social authentication |
| Economy | Atomic transactions, wallet, inventory |
| Leaderboard | Sorted set rankings, season management |
| Social | Party (max 4), friends, chat, guild, presence |
| Matchmaking | Queue management, party-aware matching |
| Notifications | Real-time + persistent notifications |

## Build & Deploy

```bash
# Build plugin
go build -buildmode=plugin -o nakama_plugin.so .

# Or with Docker
docker build -f ../deploy/docker/Dockerfile.nakama -t rpg-mmo/nakama .
```

## Configuration

Environment variables — see `shared/config` for full list.

## Dependencies

- `github.com/duycuong/rpg-mmo/shared`
- Nakama Go runtime SDK
