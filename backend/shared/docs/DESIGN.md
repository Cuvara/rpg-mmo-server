# Shared Module — Design Decisions

Dated, append-only log of decisions that other modules inherit.

## 2026-08-04 — JWT header validation

**Decision**: `jwt.Verify` decodes the header segment and requires `alg == "HS256"` and
`typ == "JWT"` before checking the signature.

**Why**: the previous implementation only recomputed the HMAC over `header.payload`, so
the header was authenticated but never *interpreted*. That is safe only as long as this
package stays the sole verifier. Making the constraint explicit closes the classic
algorithm-confusion family (`alg: none`, `alg: RS256` with the public key as HMAC secret)
at the point where a future change — a second verifier, a library swap, a key-id lookup —
would otherwise reintroduce it. The check is ~1µs and the public API is unchanged.

**Rejected**: pulling in `golang-jwt/jwt`. The shared module is the dependency root; a
60-line HS256 implementation with an explicit allow-list of one algorithm has a smaller
attack surface than a general-purpose library, and every service already links it.

## 2026-08-04 — Redis client: `github.com/redis/go-redis/v9`

**Decision**: `go-redis/v9` for all Redis access; `github.com/alicebob/miniredis/v2` for tests.

**Why**: go-redis is the maintained upstream (the former `go-redis/redis`), covers
Streams/consumer groups/Lua/Cluster, and its `redis.UniversalClient` interface lets every
store accept a shared pool. miniredis is a pure-Go in-process Redis: no server, no Docker,
no `-race`-unfriendly cgo, and — critically — `FastForward(d)` makes TTL expiry a
*deterministic* assertion instead of a `time.Sleep` flake. `rueidis` was faster in
benchmarks but has a smaller ecosystem and no equivalent test double.

**Rejected**: hand-rolled RESP client (Streams + consumer groups is real protocol work),
`redigo` (no first-class Streams API, no context plumbing).

## 2026-08-04 — Redis code lives in `storage/redisstore`, not `storage`

**Decision**: the Redis implementations sit in the subpackage
`shared/storage/redisstore`; `shared/storage` keeps the interfaces, data types and
in-memory implementations and stays dependency-free.

**Why**: `shared/storage` is imported by every module. Had go-redis been imported there,
gateway/gameserver/integration_test would all have failed to build until each ran
`go mod tidy` — CI red for modules that changed nothing and still run in-memory. With the
split, a module pays for the dependency exactly when it opts in by importing
`redisstore`. It also keeps a hard architectural line: the interface package can never
grow a transport dependency.

## 2026-08-04 — Server registry: hash + TTL, set as index

**Decision**:

```
servers:id:{server_id}   HASH  fields: server_id, map_id, addr, capacity, player_count
                               EXPIRE = constants.ServerHeartbeatTTL (15s)
servers:map:{map_id}     SET   server ids on that map, no TTL
```

The hash is the source of truth; the set is a lookup index pruned lazily (`SREM` for ids
whose hash is gone) during `FindByMapID`.

**Why**: liveness has to be *passive*. Pods die by OOM, node drain, or a severed network
— none of which run a `Deregister`. Redis key expiry is the only mechanism that reaps
those without a sweeper process, so the server record itself carries the TTL and
`Heartbeat` is just `EXPIRE`. `constants.ServerHeartbeatTTL` was declared but unread
until now; this wires it. Game servers must heartbeat at roughly TTL/3 (~5s) and
re-`Register` if `Heartbeat` returns `storage.ErrNotFound`.

A TTL cannot be attached to individual set members, hence the split: index membership is
cheap and harmless when stale, and every read path re-validates against the hash — a
disappeared hash simply drops out of the result. `UpdatePlayerCount` runs as a Lua script
(`EXISTS` then `HSET`) so a late writer cannot recreate an expired server as an immortal,
TTL-less key.

**Rejected**: one hash per map with all servers inside (no per-server expiry, and
concurrent field writes fight); sorted set scored by heartbeat timestamp (needs a sweeper
and a wall-clock the tests cannot fast-forward).

**In-memory parity**: `MemoryServerRegistry` grew the same semantics, but expiry is
opt-in — `NewMemoryServerRegistry()` never expires (existing tests and single-node dev
runs rely on that), `NewMemoryServerRegistryWithTTL(ttl)` mirrors Redis.

## 2026-08-04 — Events: Redis Streams with consumer groups, ACK after handler

**Decision**: `EventStream` over Redis Streams — `XADD` to publish,
`XGroupCreateMkStream` + `XREADGROUP` to consume, `XACK` **after** the handler returns.
Key prefix `constants.EventStreamPrefix` (`events:`).

**Why**: cross-server events carry money and progress (`rare_drop`, `boss_killed`,
`season_ended`). Pub/Sub is fire-and-forget: a subscriber that is restarting during the
publish loses the message with no trace. Streams persist the entry, a consumer group
splits the load across gateway/gameserver replicas, and an entry that was delivered but
never ACKed stays in the Pending Entries List where `XPENDING`/`XCLAIM` can recover it
after a crash.

ACK-after-handler gives **at-least-once** delivery: a consumer that dies mid-handler
leaves the entry pending and it will be redelivered. Consumers must therefore be
idempotent (the same `idempotency_key` discipline the economy already uses). At-most-once
(ACK first) was rejected — silently losing a loot grant is worse than granting it twice
behind an idempotency guard.

**Scope kept minimal**: no `XAUTOCLAIM` reaper, no dead-letter stream, no `MAXLEN`
trimming yet. Those are operational policy and belong with the deploy module once real
retention numbers exist; the interface does not change when they land.

**Blocking model**: `XREADGROUP` blocks for `SetBlockTimeout` (default 500ms) per call
rather than indefinitely, so `Close()` — which cancels the consumer context and waits for
in-flight handlers — returns promptly instead of hanging on a blocked socket.

## 2026-08-04 — PostgreSQL player persistence (`storage/pgstore`)

**Decision.** `PostgresPlayerStore` (pgx v5 / `pgxpool`) implements
`storage.PlayerStore` against a `player_states` table in the **game state**
PostgreSQL instance — separate from the Nakama meta DB, as in the target
architecture. It lives in its own package (`storage/pgstore`) so modules that
still run in-memory never pull `pgx` into their `go.sum`, mirroring the
`redisstore` split.

**Upsert, not read-modify-write.** `SavePlayer` is a single
`INSERT ... ON CONFLICT (user_id) DO UPDATE`. The gameserver's batch saver
writes the full authoritative state every 30-60s and has no interest in what was
there before; a check-then-write would double the round trips and open a race
between two servers holding the same player during a map transfer. Last write
wins, which is correct because the world is server-authoritative and only one
server owns a player at a time.

**Migrations: embedded and idempotent, no version table.** The whole schema is a
single `schema.sql` embedded with `go:embed` and applied by `Migrate(ctx)` on
every boot; every statement is `IF NOT EXISTS`. There is no migration-version
table yet — with one table and additive changes, a golang-migrate style ledger
buys ordering guarantees the schema does not need. The trade-off is that
destructive changes (column drops/renames) cannot be expressed and will force a
real migration tool later; that is the intended trigger to adopt one.

**Duplicated SQL file, guarded by a test.** `backend/deploy/db/init-gamestate.sql`
is a byte-identical copy of `schema.sql`, mounted into the compose container's
`/docker-entrypoint-initdb.d/` so a fresh volume is usable before any gameserver
connects. `go:embed` cannot reach outside its package directory, so the copy is
unavoidable; `TestSchemaMatchesDeployInitScript` fails the build if the two
drift.

**Tests run against a real PostgreSQL.** Unlike Redis (miniredis) there is no
credible in-process fake for `ON CONFLICT`, `real` column rounding or
`timestamptz`. The tests start `postgres:16.4-alpine` through the docker CLI on a
random host port and skip cleanly when no working docker CLI is present, so a
docker-less dev box still gets a green `go test ./...`.

**`ErrNotFound` mapping.** `pgx.ErrNoRows` is translated to a wrapped
`storage.ErrNotFound` so callers stay backend-agnostic — the same
`errors.Is(err, storage.ErrNotFound)` check works against memory, Redis and
PostgreSQL.

