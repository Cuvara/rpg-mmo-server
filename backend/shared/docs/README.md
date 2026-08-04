# Shared Module

Foundation module for RPG MMO backend. Contains all shared definitions, types, and utilities used by nakama, gateway, and gameserver modules.

## Contents

| Package | Purpose |
|---------|---------|
| `proto/` | Protobuf definitions and generated Go code (planned) |
| `config/` | Shared configuration and env loading |
| `models/` | Database models (PostgreSQL) (planned) |
| `messages/` | Wire protocol — Envelope, message types, length-prefixed codec |
| `storage/` | Storage interfaces (player, session, registry, events) + in-memory impls |
| `storage/redisstore/` | Redis impls: session store, server registry (heartbeat TTL), event stream (Redis Streams + consumer group ACK) |
| `jwt/` | JWT sign/verify with shared secret (HS256 only, header-validated) |
| `logger/` | slog setup |
| `errors/` | Error codes shared across services |
| `constants/` | Tick rates, TTLs, Redis key patterns |

See `API.md` for exact signatures and `DESIGN.md` for the rationale behind the Redis key
layout and the streams-with-ACK model.

## Proto Generation

```bash
# Install protoc and Go plugins
go install google.golang.org/protobuf/cmd/protoc-gen-go@latest

# Generate
protoc --go_out=proto/gen --go_opt=paths=source_relative proto/*.proto
```

## Usage

```go
import "github.com/duycuong/rpg-mmo/shared/config"
import "github.com/duycuong/rpg-mmo/shared/redis"
import "github.com/duycuong/rpg-mmo/shared/models"
```

## Dependencies

No dependency on other backend modules — this is the dependency root.

External: `github.com/redis/go-redis/v9` (only linked by `storage/redisstore`) and
`github.com/alicebob/miniredis/v2` (tests only). A module that imports
`storage/redisstore` must run `go mod tidy` to pick up the go-redis `go.sum` entries;
modules that stay on the in-memory implementations are unaffected.
