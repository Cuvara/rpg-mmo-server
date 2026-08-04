# Shared Module — API Reference

Public symbols other modules code against. Signatures here are authoritative.

## `jwt`

```go
func Sign(userID, secret string, expiry time.Duration) (string, error)
func SignWithServer(userID, serverID, secret string, expiry time.Duration) (string, error)
func Verify(token, secret string) (Claims, error)

type Claims struct {
    UserID   string `json:"sub"`
    ServerID string `json:"sid,omitempty"`
    IssuedAt int64  `json:"iat"`
    ExpireAt int64  `json:"exp"`
}
func (c Claims) IsExpired() bool
```

`Verify` enforces, in order: 3-segment format → header check (`alg == "HS256"` **and**
`typ == "JWT"`) → HMAC signature → `exp`. Tokens carrying `alg: "none"`, `HS512`, `RS256`,
a missing/odd `typ`, or a non-base64 header are rejected before any signature work.
The API is unchanged — callers need no edits.

## `storage` — interfaces and in-memory implementations

```go
var ErrNotFound = errors.New("not found")   // wrapped by Redis impls; test with errors.Is
```

### Interfaces

```go
type PlayerStore interface {
    SavePlayer(ctx context.Context, state *PlayerState) error
    LoadPlayer(ctx context.Context, userID string) (*PlayerState, error)
    DeletePlayer(ctx context.Context, userID string) error
}

type SessionStore interface {
    Set(ctx context.Context, key string, value []byte, ttl time.Duration) error
    Get(ctx context.Context, key string) ([]byte, error)
    Delete(ctx context.Context, key string) error
    Refresh(ctx context.Context, key string, ttl time.Duration) error   // NEW
}

type ServerRegistry interface {
    Register(ctx context.Context, info ServerInfo) error
    Deregister(ctx context.Context, serverID string) error
    FindByMapID(ctx context.Context, mapID string) ([]ServerInfo, error)
    UpdatePlayerCount(ctx context.Context, serverID string, count int) error
    Heartbeat(ctx context.Context, serverID string) error               // NEW
    GetServer(ctx context.Context, serverID string) (ServerInfo, error) // NEW
}

type EventStream interface {
    Publish(ctx context.Context, stream string, event Event) error
    Subscribe(ctx context.Context, stream string, handler func(Event)) error
    Close() error
}
```

`Subscribe` is non-blocking in every implementation: it registers/starts delivery and
returns. Durable implementations ACK a message only after `handler` returns.

### In-memory implementations (dev, tests, single-node)

```go
func NewMemoryPlayerStore() *MemoryPlayerStore
func NewMemorySessionStore() *MemorySessionStore
func NewMemoryServerRegistry() *MemoryServerRegistry                              // no heartbeat expiry
func NewMemoryServerRegistryWithTTL(ttl time.Duration) *MemoryServerRegistry      // NEW, expiry like Redis
func NewMemoryEventStream() *MemoryEventStream
```

`NewMemoryServerRegistry()` keeps entries forever (ttl 0) — existing behaviour is
unchanged. `NewMemoryServerRegistryWithTTL(constants.ServerHeartbeatTTL)` mirrors the
Redis liveness semantics without a Redis dependency.

## `storage/redisstore` — Redis implementations

Import path: `github.com/duycuong/rpg-mmo/shared/storage/redisstore`.
It lives in its own package so modules that still run in-memory never pull in
`go-redis` (their `go.sum` stays untouched until they import it).

```go
func NewRedisClient(addr, password string) *redis.Client

// SessionStore — keys used verbatim (build them with constants.SessionKeyPrefix + userID)
func NewSessionStore(addr, password string) *SessionStore
func NewSessionStoreWithClient(client redis.UniversalClient) *SessionStore
func (s *SessionStore) Set(ctx context.Context, key string, value []byte, ttl time.Duration) error
func (s *SessionStore) Get(ctx context.Context, key string) ([]byte, error)
func (s *SessionStore) Delete(ctx context.Context, key string) error
func (s *SessionStore) Refresh(ctx context.Context, key string, ttl time.Duration) error
func (s *SessionStore) Close() error

// ServerRegistry — heartbeat TTL defaults to constants.ServerHeartbeatTTL (15s)
func NewServerRegistry(addr, password string) *ServerRegistry
func NewServerRegistryWithClient(client redis.UniversalClient, ttl time.Duration) *ServerRegistry
func (r *ServerRegistry) Register(ctx context.Context, info storage.ServerInfo) error
func (r *ServerRegistry) Deregister(ctx context.Context, serverID string) error
func (r *ServerRegistry) FindByMapID(ctx context.Context, mapID string) ([]storage.ServerInfo, error)
func (r *ServerRegistry) UpdatePlayerCount(ctx context.Context, serverID string, count int) error
func (r *ServerRegistry) Heartbeat(ctx context.Context, serverID string) error
func (r *ServerRegistry) GetServer(ctx context.Context, serverID string) (storage.ServerInfo, error)
func (r *ServerRegistry) Close() error

// EventStream — Redis Streams consumer group
func NewEventStream(addr, password, group, consumer string) *EventStream
func NewEventStreamWithClient(client redis.UniversalClient, group, consumer string) *EventStream
func (s *EventStream) SetBlockTimeout(d time.Duration)   // call before Subscribe; default 500ms
func (s *EventStream) Publish(ctx context.Context, stream string, event storage.Event) error
func (s *EventStream) Subscribe(ctx context.Context, stream string, handler func(storage.Event)) error
func (s *EventStream) Close() error
```

`Close()` shuts the underlying client down **only** when the store created it
(`New*(addr, password)`); the `*WithClient` constructors leave a shared pool alone.

### Key layout

| Purpose | Key | Type | TTL |
|---------|-----|------|-----|
| Session | `session:{user_id}` (caller-built, `constants.SessionKeyPrefix`) | string | `constants.SessionTTL` (1h) |
| Server record | `servers:id:{server_id}` | hash (`server_id`, `map_id`, `addr`, `capacity`, `player_count`) | `constants.ServerHeartbeatTTL` (15s) |
| Map index | `servers:map:{map_id}` | set of server ids | none (pruned lazily) |
| Event stream | `events:{stream}` | stream | none |

### Usage

```go
client := redisstore.NewRedisClient(cfg.RedisAddr, cfg.RedisPassword)

sessions := redisstore.NewSessionStoreWithClient(client)
registry := redisstore.NewServerRegistryWithClient(client, constants.ServerHeartbeatTTL)
events   := redisstore.NewEventStreamWithClient(client, "gateway", podName)

// game server: heartbeat faster than the TTL or the entry disappears
go func() {
    t := time.NewTicker(constants.ServerHeartbeatTTL / 3)
    for range t.C {
        if err := registry.Heartbeat(ctx, serverID); errors.Is(err, storage.ErrNotFound) {
            _ = registry.Register(ctx, info) // re-register after an outage
        }
    }
}()

_ = events.Subscribe(ctx, "world", func(e storage.Event) { handle(e) }) // ACK after handler returns
```

## `storage/pgstore` — PostgreSQL implementations (game state DB)

Import path: `github.com/duycuong/rpg-mmo/shared/storage/pgstore`.
Own package for the same reason as `redisstore`: modules that stay in-memory
never pull in `pgx`. Targets the **game state** PostgreSQL instance, which is
separate from the Nakama meta DB.

```go
// PostgresPlayerStore implements storage.PlayerStore.
func NewPlayerStore(ctx context.Context, dsn string) (*PostgresPlayerStore, error) // connects + pings
func NewPlayerStoreWithPool(pool *pgxpool.Pool) *PostgresPlayerStore              // shared pool / tests
func (s *PostgresPlayerStore) Migrate(ctx context.Context) error                  // idempotent DDL
func (s *PostgresPlayerStore) SavePlayer(ctx context.Context, state *storage.PlayerState) error
func (s *PostgresPlayerStore) LoadPlayer(ctx context.Context, userID string) (*storage.PlayerState, error)
func (s *PostgresPlayerStore) DeletePlayer(ctx context.Context, userID string) error
func (s *PostgresPlayerStore) Ping(ctx context.Context) error
func (s *PostgresPlayerStore) Pool() *pgxpool.Pool
func (s *PostgresPlayerStore) Close()

func SchemaSQL() string // the embedded migration SQL
```

Semantics:

- `SavePlayer` is an **upsert** (`ON CONFLICT (user_id) DO UPDATE`), so the
  gameserver's batch saver never needs an existence check. `updated_at` is set
  to `now()` on every write.
- `LoadPlayer` returns an error wrapping `storage.ErrNotFound` when the row is
  missing — test with `errors.Is(err, storage.ErrNotFound)`.
- `DeletePlayer` on a missing row is a no-op (matches `MemoryPlayerStore`).
- `NewPlayerStore` pings before returning: a bad DSN or a down database fails at
  boot, not on the first save. It does **not** migrate — call `Migrate`
  explicitly.
- `Close()` shuts the pool down only when the store created it.

### Schema (`player_states`)

| Column | Type | Note |
|--------|------|------|
| `user_id` | `text` | primary key |
| `map_id` | `text` | indexed (`player_states_map_id_idx`) |
| `x`, `y` | `real` | matches `PlayerState.X/Y` (`float32`) |
| `hp`, `max_hp` | `integer` | |
| `updated_at` | `timestamptz` | set by the store on every write |

The DDL lives in `storage/pgstore/schema.sql` (embedded via `go:embed`) with a
byte-identical copy at `backend/deploy/db/init-gamestate.sql` mounted into the
`postgres-game` container's `/docker-entrypoint-initdb.d/`. A test asserts the
two files do not drift.

### Usage

```go
store, err := pgstore.NewPlayerStore(ctx, cfg.GameDBURL)
if err != nil { return fmt.Errorf("game db: %w", err) }
defer store.Close()
if err := store.Migrate(ctx); err != nil { return err }

var players storage.PlayerStore = store
```

## `config`

| Field | Env | Default |
|-------|-----|---------|
| `GameDBURL` | `GAME_DB_URL` | *(empty)* — empty means "no PostgreSQL configured"; services fall back to their in-memory store |

Example: `postgres://game:localdev@localhost:5433/gamestate?sslmode=disable`
(the `postgres-game` service in `backend/deploy/docker-compose.yml`).

## `constants`

`ServerHeartbeatTTL` (15s) is now wired: it is the default liveness window of
`redisstore.ServerRegistry`. `EventStreamPrefix` (`events:`) is now wired as the
Redis Streams key prefix.
