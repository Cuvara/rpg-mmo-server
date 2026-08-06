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

### `Keyring` — secret rotation

`JWT_SECRET` and `JOIN_TOKEN_SECRET` accept a **comma-separated list**
(`"current,previous"`). The first entry signs; every entry verifies, in order.
This is what makes a secret rotation non-disruptive: deploy `new,old`, wait out
the longest token TTL, then deploy `new`.

```go
func ParseKeyring(spec string) (Keyring, error)   // "new, old" -> 2 secrets
func NewKeyring(secrets ...string) (Keyring, error)

func (k Keyring) Len() int          // >1 means a rotation is in progress
func (k Keyring) Valid() bool
func (k Keyring) Signing() string   // the first secret
func (k Keyring) Sign(userID string, expiry time.Duration) (string, error)
func (k Keyring) SignWithServer(userID, serverID string, expiry time.Duration) (string, error)
func (k Keyring) Verify(token string) (Claims, error)
func (c Claims) IsZero() bool
```

Notes:

- **An empty spec is an error.** The zero `Keyring` rejects every token and
  refuses to sign — a service booted with no secret fails closed rather than
  accepting tokens signed with `""`.
- **Expiry short-circuits.** A token whose signature matched but whose `exp`
  passed returns immediately (with its claims populated, for logging) instead of
  being retried against the remaining keys.
- Verification is O(number of secrets) HMAC-SHA256 ops in the worst case, so a
  keyring is a rotation window, not an archive. Two entries is the intended size.

## `ratelimit` — token-bucket limiter

Shared by the gateway (per-IP accepts, per-connection messages) and the Nakama
plugin (per-user RPCs). Two types, split by allocation profile:

```go
// Single bucket: no lock, no map, zero allocations. Embed by value.
type Bucket struct { Rate, Burst float64 }   // Rate <= 0 disables limiting
func NewBucket(rate, burst float64) Bucket
func (b *Bucket) Allow() bool
func (b *Bucket) AllowAt(now time.Time) bool  // tests drive time through this
func (b *Bucket) Enabled() bool

// Keyed set of buckets (per IP / per user), mutex-guarded, TTL-evicted.
func NewLimiter(rate, burst float64, idleTTL time.Duration) *Limiter
func (l *Limiter) Allow(key string) bool
func (l *Limiter) AllowAt(key string, now time.Time) bool
func (l *Limiter) Cleanup(now time.Time) int
func (l *Limiter) StartCleanup(every time.Duration)
func (l *Limiter) Stop()
func (l *Limiter) Len() int
func (l *Limiter) Enabled() bool

const DefaultIdleTTL = 10 * time.Minute
```

Semantics: the bucket holds up to `Burst` tokens and refills at `Rate`/second;
`Allow` consumes one. A short burst passes in full, sustained traffic is
throttled to `Rate`. `Bucket`'s zero value and a **nil `*Limiter`** both allow
everything, so "disabled" needs no nil checks at the call site.

`BenchmarkBucketAllow`: **10.8 ns/op, 0 allocs/op** — which is what makes it
safe in the gateway's per-message read path.

**Scope caveat:** the limiter is per process and in memory. N replicas admit
N x `Rate` per key. Accepted for the MVP; the production upgrade is a
Redis-backed counter keyed identically.

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

## `transport` — pluggable realtime transport

Listen/dial abstraction for the realtime path. The wire codec in
`shared/messages` is a 4-byte length prefix over an `io.Reader`/`io.Writer`, so
it works on any `net.Conn`; this package only decides which `net.Conn` the
servers get.

| Function | Signature | Notes |
|----------|-----------|-------|
| `Listen` | `Listen(kind, addr string, opts ...Option) (net.Listener, error)` | `kind` is `"tcp"`, `"kcp"` or `""` (= tcp) |
| `Dial` | `Dial(kind, addr string, timeout time.Duration, opts ...Option) (net.Conn, error)` | `timeout` bounds the TCP handshake only |
| `WithKey` | `WithKey(key string) Option` | pre-shared KCP encryption key; `""` = plaintext |
| `WithLogger` | `WithLogger(*slog.Logger) Option` | logger for the unencrypted-listener warning |
| `Encrypted` | `Encrypted(opts ...Option) bool` | whether a given option set encrypts |
| `DeriveKey` | `DeriveKey(key string) ([]byte, error)` | 32-byte AES-256 key; validate config at start-up |
| `KeyEnvVar` | `= "TRANSPORT_KEY"` | env var name |
| `Normalize` | `Normalize(kind string) string` | lowercases; `""` → `"tcp"` |
| `Validate` | `Validate(kind string) error` | `""`/`tcp`/`kcp` are valid |
| `Kinds` | `Kinds() []string` | `["tcp", "kcp"]` |

```go
ln, err := transport.Listen("kcp", ":9000")   // net.Listener
conn, err := transport.Dial("kcp", addr, 2*time.Second) // net.Conn

// Encrypted (both peers need the SAME key):
ln, err := transport.Listen("kcp", ":9000", transport.WithKey(os.Getenv("TRANSPORT_KEY")))
conn, err := transport.Dial("kcp", addr, 2*time.Second, transport.WithKey(key))
```

`opts` is variadic, so every pre-existing call site compiles unchanged.

### KCP encryption (`TRANSPORT_KEY`)

`WithKey` installs kcp-go's AES-256 `BlockCrypt`, which encrypts **every UDP
datagram** below the KCP ARQ — including the datagrams carrying the join token.

Key formats accepted by `DeriveKey`:

| Input | Handling |
|-------|----------|
| 64 hex chars (`openssl rand -hex 32`) | decoded verbatim as 32 raw bytes — **recommended** |
| anything else | HKDF-SHA256 stretched to 32 bytes (spreads the entropy it has; it cannot create entropy a short passphrase lacks) |
| `""` / whitespace only | plaintext; a KCP **listener logs a WARN** on every start |

There is no negotiation and no downgrade path, so the failure mode is
fail-closed and silent: a peer with the wrong key (or no key) produces
datagrams that decrypt to noise and are dropped as malformed KCP segments —
no session is ever established and no error is returned. Verified by
`TestKCPEncryptionRoundtrip`, which covers matching-key success and all three
mismatch cases (encrypted↔plaintext both ways, and two different keys).

TCP ignores the key entirely; use TLS or the cluster network there.

KCP is `github.com/xtaci/kcp-go/v5` with a game profile applied to every
session (exported as constants so callers can log/inspect them):

| Constant | Value | Meaning |
|----------|-------|---------|
| `KCPNoDelay` / `KCPInterval` / `KCPResend` / `KCPNoCongestion` | `1 / 10 / 2 / 1` | kcp-go "turbo" profile |
| `KCPSendWindow` / `KCPRecvWindow` | `128 / 128` | packets in flight per direction |
| `KCPMTU` | `1350` | stays under common path MTUs |
| `KCPDataShards` / `KCPParityShards` | `0 / 0` | FEC disabled |
| `KCPSocketBuffer` | 4 MiB | shared UDP socket buffer |

Sessions also get `SetStreamMode(true)`, `SetWriteDelay(false)` and
`SetACKNoDelay(true)`. Rationale in `shared/docs/DESIGN.md`.

**Behaviour difference callers must know:** KCP runs over UDP and has no
connection handshake, so `Dial("kcp", deadAddr, …)` *succeeds*. Liveness only
surfaces as a read timeout on the first application reply. Equally, a dropped
KCP client is invisible until the reconnect hold expires — well-behaved clients
send `MsgDisconnect` first.

## `config`

| Field | Env | Default |
|-------|-----|---------|
| `GameDBURL` | `GAME_DB_URL` | *(empty)* — empty means "no PostgreSQL configured"; services fall back to their in-memory store |
| `GatewayTransport` | `GATEWAY_TRANSPORT` | `tcp` — realtime transport the gateway listens with (`tcp` or `kcp`) |
| `GameServerTransport` | `GAMESERVER_TRANSPORT` | `tcp` — realtime transport the game server listens with (`tcp` or `kcp`) |
| `JWTSecret` | `JWT_SECRET` | `dev-secret-change-me` — client auth token. Comma-separated list to rotate (`new,old`) |
| `JoinTokenSecret` | `JOIN_TOKEN_SECRET` | *(empty)* — gateway→gameserver join token. Empty means "reuse `JWT_SECRET`" (with a start-up warning) |
| `TransportKey` | `TRANSPORT_KEY` | *(empty)* — pre-shared KCP encryption key, 32-byte hex recommended. Empty = plaintext |
| `GatewayConnRatePerMin` | `GATEWAY_CONN_RATE_PER_MIN` | `10` — accepted connections/min per source IP (`0` disables) |
| `GatewayConnBurst` | `GATEWAY_CONN_BURST` | `10` |
| `GatewayMsgRatePerSec` | `GATEWAY_MSG_RATE_PER_SEC` | `60` — inbound frames/s per connection (`0` disables) |
| `GatewayMsgBurst` | `GATEWAY_MSG_BURST` | `120` |

`Config.EffectiveJoinTokenSecret() (spec string, sharedWithAuth bool)` resolves
the join-token secret and reports whether the `JWT_SECRET` fallback was taken,
so each service emits the "secrets are not split" warning exactly once.

Example: `postgres://game:localdev@localhost:5433/gamestate?sslmode=disable`
(the `postgres-game` service in `backend/deploy/docker-compose.yml`).

## `constants`

`ServerHeartbeatTTL` (15s) is now wired: it is the default liveness window of
`redisstore.ServerRegistry`. `EventStreamPrefix` (`events:`) is now wired as the
Redis Streams key prefix.
