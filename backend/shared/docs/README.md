# Shared Module

Foundation module for RPG MMO backend. Contains all shared definitions, types, and utilities used by nakama, gateway, and gameserver modules.

## Contents

| Package | Purpose |
|---------|---------|
| `proto/` | Protobuf definitions and generated Go code |
| `config/` | Shared configuration and env loading |
| `models/` | Database models (PostgreSQL) |
| `redis/` | Redis client wrappers (session, registry, streams) |
| `jwt/` | JWT sign/verify with shared secret |
| `errors/` | Error codes shared across services |
| `constants/` | Tick rates, TTLs, Redis key patterns |

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

None — this is the dependency root. All other backend modules depend on this.
