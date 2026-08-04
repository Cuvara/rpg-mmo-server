# Changelog — Shared Module

All notable changes to the shared module will be documented in this file.
Format based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Added
- `transport` package — pluggable realtime transport. `Listen(kind, addr)` /
  `Dial(kind, addr, timeout)` over `tcp` and `kcp`
  (`github.com/xtaci/kcp-go/v5`), plus `Normalize`/`Validate`/`Kinds`. KCP
  sessions get a documented game profile (nodelay 1/10/2/1, window 128/128,
  MTU 1350, stream mode, no FEC, no encryption — see `docs/DESIGN.md` for the
  production encryption TODO). Table-driven tests cover both kinds:
  round-trip through the `messages` codec, 50 sequential frames, 16 concurrent
  connections, a payload 8x the MTU (fragmentation), and dead-port dial
  semantics.
- `config`: `GatewayTransport` (`GATEWAY_TRANSPORT`) and `GameServerTransport`
  (`GAMESERVER_TRANSPORT`), both defaulting to `tcp`.
- `messages.EnterWorldResponse.Transport` — tells the client which transport
  the assigned game server speaks. `omitempty`; empty means `tcp`.
- `storage.ServerInfo.Transport` — the transport a registered game server
  listens with, persisted by `redisstore.ServerRegistry` as the `transport`
  hash field. `omitempty`; empty means `tcp`, so entries written by older game
  servers stay valid.


### Fixed
- `pgstore` container tests were flaky in CI: the postgres entrypoint runs
  initdb against a temporary unix-socket-only server before restarting the real
  one, so `pg_isready` reported ready while TCP clients still got
  "connection reset by peer". `newTestStore` now retries `NewPlayerStore` for up
  to 60s instead of failing on the first connect.
- Cross-server event stream name mismatch: gameserver published to
  "events:game" (double-prefixed to "events:events:game" by the store) while
  the gateway relay subscribed to "global" — events never arrived. Both sides
  now share constants.GameEventStream ("game", store adds the prefix once).

### Added
- `storage/pgstore` — PostgreSQL implementation of `storage.PlayerStore`
  (`PostgresPlayerStore`, pgx v5 / `pgxpool`) targeting the game state database:
  upsert on save (`ON CONFLICT (user_id) DO UPDATE`), `storage.ErrNotFound`
  mapping on a missing row, connect-time `Ping` so a bad DSN fails at boot, and
  an idempotent `Migrate(ctx)` that applies the embedded `schema.sql`
  (`player_states` + `player_states_map_id_idx`). `SchemaSQL()` exposes the SQL.
  New dependency: `github.com/jackc/pgx/v5` (isolated in its own package, like
  `redisstore`, so in-memory-only modules are unaffected).
- `config.Config.GameDBURL` from `GAME_DB_URL`, default empty — empty means "no
  PostgreSQL configured" and services fall back to their in-memory store.
- `pgstore` tests run against a real `postgres:16.4-alpine` started through the
  docker CLI on a random host port (save/load roundtrip, upsert overwrite +
  single-row assertion, delete, missing row → `ErrNotFound`, repeated `Migrate`,
  bad DSN). They `t.Skip` when no working docker CLI is available. A further test
  asserts `storage/pgstore/schema.sql` and
  `backend/deploy/db/init-gamestate.sql` stay byte-identical.

### Security
- `jwt.Verify` now validates the token header: `alg` must be `HS256` and `typ` must be
  `JWT`, checked before signature verification. Rejects `alg: none`, `HS512`, `RS256`,
  wrong/missing `typ`, and non-base64 headers. Public API unchanged.

### Added
- `storage/redisstore` package — Redis-backed implementations of the shared storage
  interfaces, kept out of `storage` so modules only pull in `go-redis` when they import it:
  - `redisstore.SessionStore` — `SET/GET/DEL/EXPIRE`, keys used verbatim
    (`constants.SessionKeyPrefix` + user id), TTL `constants.SessionTTL`
  - `redisstore.ServerRegistry` — server hash `servers:id:{server_id}` with
    `constants.ServerHeartbeatTTL` expiry + map index set `servers:map:{map_id}`;
    heartbeat-based liveness, lazy index pruning, Lua-guarded player-count updates
  - `redisstore.EventStream` — Redis Streams (`events:{stream}`) with consumer groups:
    `XADD` / `XGROUP CREATE MKSTREAM` / `XREADGROUP` / `XACK` after the handler returns
    (at-least-once delivery)
  - `redisstore.NewRedisClient(addr, password)` helper aligned with
    `config.Config.RedisAddr` / `RedisPassword`
- `storage.ErrNotFound` sentinel error, wrapped by the Redis implementations
  (`errors.Is`-testable)
- `SessionStore.Refresh(ctx, key, ttl)` — extend a session TTL without rewriting the value
- `ServerRegistry.Heartbeat(ctx, serverID)` and `ServerRegistry.GetServer(ctx, serverID)`
- `storage.NewMemoryServerRegistryWithTTL(ttl)` — in-memory registry with the same
  heartbeat-expiry semantics as Redis
- Table-driven tests for all Redis implementations using `miniredis` (TTL expiry asserted
  via `FastForward`, ACK asserted via `XPENDING`); tests for the new JWT header checks and
  the new in-memory methods
- `docs/API.md` and `docs/DESIGN.md`

### Changed
- `constants.ServerHeartbeatTTL` and `constants.EventStreamPrefix` are now actually used
  (registry liveness window, event stream key prefix)
- `MemoryServerRegistry` stores entries with a `lastSeen` timestamp. Expiry is opt-in:
  `NewMemoryServerRegistry()` keeps the previous never-expires behaviour
- Bump Go version to 1.26 (align with CI and gameserver)

### Dependencies
- Added `github.com/redis/go-redis/v9`
- Added `github.com/alicebob/miniredis/v2` (test-only)

### Added (earlier)
- Initial module setup with go.mod (`github.com/duycuong/rpg-mmo/shared`)
- CLAUDE.md agent instructions for Shared Architect role
