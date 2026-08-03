# Gateway Module

Custom Go binary — UDP/KCP router between Unity clients and Game Servers. Stateless, horizontally scalable.

## Architecture

```
Unity Client --[KCP/UDP]--> Gateway --[KCP/UDP]--> Game Server
                              |
                              +--> Redis (session, registry, events)
```

## Features

| Feature | Description |
|---------|-------------|
| KCP Transport | UDP-based reliable transport for realtime gameplay |
| Session Manager | JWT local verification, Redis session store |
| Server Registry | Track active Game Servers, capacity, health |
| Map Assignment | Route players to correct Map/Dungeon server |
| Event Relay | Forward Redis Streams events to clients/servers |

## Run

```bash
go run ./cmd/gateway/ --config=config.yaml
```

## Configuration

Environment variables — see `shared/config` for full list.

## Dependencies

- `github.com/duycuong/rpg-mmo/shared`
- KCP-Go library
