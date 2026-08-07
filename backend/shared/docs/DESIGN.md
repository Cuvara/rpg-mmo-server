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



## 2026-08-04 — KCP/UDP transport for the realtime path (`shared/transport`)

**Decision.** Add `shared/transport`, a `Listen(kind, addr)` / `Dial(kind, addr,
timeout)` abstraction over `tcp` and `kcp` (`github.com/xtaci/kcp-go/v5`), and
route gateway + game server listeners through it. TCP stays the default; KCP is
opt-in per service via a flag/env var. This closes the "Transport: TCP → KCP"
extension seam without touching a line of business logic.

**Why it is a zero-cost seam.** `messages.Encode/Decode` is a 4-byte length
prefix over `io.Reader`/`io.Writer`, and a tuned KCP session is a reliable,
ordered byte stream that satisfies `net.Conn`. Handlers, the tick loop, the
snapshot writer and the join-token handshake are byte-identical on both kinds —
only the two `net.Listen("tcp", …)` call sites changed.

**Why KCP at all.** TCP's recovery is tuned for throughput: a lost segment
stalls the stream for an RTO that grows on retry, which on a mobile link means a
multi-hundred-millisecond freeze of an authoritative 10-15Hz simulation. KCP's
ARQ retransmits after 2 duplicate ACKs at a fixed 10ms cadence, so the same loss
costs roughly one RTT. The trade is bandwidth (KCP is deliberately less polite);
that is acceptable for a small, near-constant realtime bitrate.

**Parameters and rationale** (constants in `shared/transport`, quoted with the
kcp-go names):

| Setting | Value | Why |
|---------|-------|-----|
| `SetNoDelay(1, 10, 2, 1)` | turbo | nodelay ARQ on; 10ms update tick matches a 10-15Hz server tick (a slower interval would add up to a full tick of jitter); fast retransmit after 2 dup ACKs; **congestion control off** — the realtime path carries a small near-constant bitrate, so a congestion window only ever delays state the client already needs |
| `SetWindowSize(128, 128)` | ~170 KB in flight | never the limiting factor on a bad mobile link, still bounds per-session memory (~350 KB both directions) at a few thousand CCU per pod |
| `SetMtu(1350)` | kcp-go default | under the common 1400-1500B path MTU (PPPoE/VPN/carrier), so KCP segments are never IP-fragmented |
| FEC `0/0` | disabled | FEC trades bandwidth for latency but needs per-game measurement; enabling it blind costs bandwidth for nothing. Revisit with client telemetry |
| encryption | opt-in PSK | `TRANSPORT_KEY` → `kcp.NewAESBlockCrypt` (AES-256). Empty = plaintext (dev default) and a KCP listener logs a WARN. See the 2026-08-06 entry below |
| `SetStreamMode(true)` | byte stream | what the length-prefixed codec expects; in message mode a write larger than the MTU would not reassemble the way `Decode` assumes |
| `SetWriteDelay(false)` | flush now | one less update interval of added latency per frame |
| socket buffers | 4 MiB | one UDP socket multiplexes every session on a listener, so it needs far more room than a per-connection TCP socket |

**Per-hop negotiation, not global.** The client's two hops (gateway, game
server) are independent. `storage.ServerInfo` gains `Transport`, set by the game
server at registration; the gateway copies it into the new
`EnterWorldResponse.Transport` field so the client knows what to dial for hop 2.
Both fields are `omitempty` and empty means `tcp`, so registry entries and
clients that predate the field keep working unchanged — verified by
`TestEnterWorld_LegacyRegistryEntryIsTCP`.

**Known UDP semantics the callers inherit.** There is no connection handshake:
dialing a dead KCP port succeeds and only fails on the first read, and a client
that drops its socket is invisible to the server until the reconnect hold
expires. Both are documented on the package API; clients send `MsgDisconnect`
before closing, and the existing hold window covers the rest.


---

## 2026-08-06 — Realtime-path security primitives

Three additions, all in `shared` because both the gateway and the Nakama plugin
need them and `shared` is the dependency root. Architectural rationale and the
threat model are in `backend/docs/ARCHITECTURE-DECISIONS.md`, ADR-8; this entry
covers the implementation choices.

### `transport` — KCP encryption via a pre-shared key

`Listen`/`Dial` gained a variadic `...Option` tail (so every existing call site
compiles untouched) and `WithKey` installs kcp-go's AES-256 `BlockCrypt`.

**Why HKDF and not PBKDF2/scrypt for passphrases.** The 64-hex-char form is the
recommended input and is used verbatim — no derivation at all. The passphrase
path exists only so a dev can type something memorable, and it is stretched
with `crypto/hkdf` (stdlib as of Go 1.24, so no new dependency) under a
domain-separated info string. HKDF is deliberately *fast*: it is a key
derivation function, not a password hash. That is the right call here because
the key is read once at start-up from an environment variable, never from user
input, and slowing it down would protect nothing — an attacker brute-forcing a
weak passphrase does so offline against captured traffic, where a 100ms KDF
costs them nothing at scale. The honest mitigation is "use the hex form", which
the docs say and the warning nudges toward.

**Why the empty key is legal.** Making encryption mandatory would break every
existing dev workflow and the TCP default path, and would have forced the same
change into `gameserver-dotnet` (out of scope, and it has no KCP at all). The
compromise is: legal, but a KCP listener says so loudly on every start.

**Failure mode is silence, by design.** kcp-go has no crypto negotiation. A
peer with the wrong key emits datagrams that decrypt to garbage and are dropped
as malformed segments — no handshake, no error, no downgrade. That is what
makes "encrypted listener + plaintext dialer" impossible rather than merely
discouraged, and it is why the roundtrip test asserts on a *timeout* for the
mismatch cases.

### `jwt.Keyring` — rotation without a logout event

An ordered secret list: first signs, all verify. The alternative designs were a
`kid` header claim (proper JWKS-style key identification) and a versioned token
prefix. Both are better at scale and both require a wire-format change plus a
coordinated Unity-client update; the ordered list needs neither, costs one extra
HMAC per verification during the rotation window only, and is entirely
config-driven. For a rotation that happens a handful of times a year, that trade
is right.

Two details worth pinning:

- **Expiry short-circuits.** A signature that matched but whose `exp` passed
  returns immediately rather than being retried under the remaining keys — the
  answer cannot change, and the retry is pure cost.
- **The zero `Keyring` fails closed.** It rejects everything and refuses to
  sign. A service started with no secret must not silently accept tokens signed
  with the empty string.

### `ratelimit` — one limiter, two shapes

`Bucket` (bare struct, no lock, no map) and `Limiter` (keyed, mutex-guarded,
TTL-evicted) are separate types rather than one type with a "keyed" mode
because their cost profiles differ by an order of magnitude and they are used in
different places: `Bucket` is embedded per connection and runs in the gateway's
per-frame read path (10.8 ns/op, 0 allocs — benchmarked, not assumed), while
`Limiter` runs once per accept or per RPC where a mutex and a map lookup are
free in comparison.

**Why TTL eviction is safe.** Evicting an idle bucket recreates it full on the
next request — but an un-evicted bucket would have refilled to full anyway,
because the TTL is required to exceed `burst/rate`. So eviction cannot be used
as a bypass, which `TestLimiterCleanupDoesNotGrantFreeReset` pins.

**Why `nil *Limiter` allows everything.** "Limiting disabled" and "no limiter
configured" are the same thing to every call site, and making the nil case
permissive removes a nil check from each of them. The same reasoning makes the
zero `Bucket` unlimited: `Rate: 0` reads naturally as "no rate limit".

**Testability.** Every decision function has an `...At(now time.Time)` variant.
The tests drive time explicitly instead of sleeping, so the whole suite is
deterministic and runs in milliseconds.

## Redis client defaults (2026-08-06)

`NewRedisClient` previously set only `Addr` and `Password`, so every store
inherited go-redis' defaults. That is not a safe base for a request path: with a
5s dial timeout and 3 unbounded retries, one `Get` against a black-holed Redis
can occupy a caller for tens of seconds, and a login burst then piles up stuck
goroutines until the process is effectively wedged.

`ClientOptions` makes timeouts, retries and pooling explicit
(`redisstore/client.go`). The numbers are picked so a healthy Redis never
approaches them and an unhealthy one is detected in about a second: 2s
dial/read/write, 3 retries between 16ms and 256ms, pool of 32 with 4 warm idle
connections. Measured against a stopped Redis container, a `Get` now fails in
~1.4s.

**Zero means default, negative means disabled.** go-redis reads a negative
timeout as "no timeout", which is a meaningful choice a caller may want, so
`withDefaults` substitutes only exact zeros. `TestClientOptionsOverrides` pins
this — the obvious `if o.X <= 0` implementation would silently override the
caller.

**Blocking reads are the exception.** `XREADGROUP` with `Block` legitimately
holds the socket for the block duration, so a read timeout at or below it makes
every idle poll look like an i/o timeout and buries real errors. `NewEventStream`
therefore widens `ReadTimeout` to `block + DefaultReadTimeout`. This is the kind
of interaction that only appears once timeouts are set at all, which is why it
is called out here rather than left as a comment.

## NOGROUP is not a transient error (2026-08-06)

The stream consumer backed off and retried on every non-`redis.Nil` error. That
is right for a connection reset and wrong for `NOGROUP`: the consumer group no
longer exists, so every subsequent `XREADGROUP` fails identically, forever. The
observed failure mode after a Redis wipe or a restore from an older backup was a
relay spinning at 2Hz, logging nothing, with the process reporting itself
healthy — a dead subsystem that looked fine.

`NOGROUP` is now classified separately, the group is re-created via the same
`ensureGroup` path used at subscribe time, and the recovery is both logged and
counted (`GroupLosses`). Entries published while the group was missing are
genuinely lost — they were never delivered to any consumer — which is why this
is a loud warning rather than a silent heal.

## One "not found", two stores (2026-08-06)

`redisstore` returned `storage.ErrNotFound` for a missing key; `storage/memory.go`
returned a bare `fmt.Errorf("session %s not found", key)`. Both read fine at a
call site doing `if err != nil`, and both were wrong the moment a caller needed
to tell "absent" from "store broken" — on the memory backend every `errors.Is`
check answered false, so a missing session looked like an infrastructure
failure. Every memory-store miss now wraps the sentinel.

Relatedly, `errors.Is(err, code)` in `shared/errors` used a bare type assertion
and so returned false for any wrapped `GameError` — while `TEAM.md` mandates
wrapping with `%w` everywhere. It now uses `errors.As`. Both bugs are the same
shape: a classification helper that silently reports "no" instead of failing
loudly, which makes every caller's default branch the accidental behaviour.

## Protobuf on the wire, with JSON detected by its first byte (2026-08-07)

`shared/proto/wire.proto` is now the single source of truth for the realtime
wire format. Go bindings are generated into `shared/proto/gen`, C# bindings into
`gameserver-dotnet/GameServer/Net/Generated`, both by `shared/proto/generate.sh`,
and both are committed so no CI runner needs `protoc`.

### Why now

Not speculative. [`docs/BENCHMARK.md`](../../docs/BENCHMARK.md) measured that
snapshot construction plus `JsonSerializer` was ~80% of tick cost against ~20%
for the brute-force AOI scan, and that ADR-7's own `< 50 KB/s per client`
threshold broke at ~41 players — less than a third of the tick-budget ceiling of
150. Bandwidth was the binding constraint and JSON was the whole of it.

### The migration decision: sniffing, not negotiation

The alternatives were a hard cutover, a version field in the envelope, or a
negotiated handshake. All three were rejected in favour of **detecting the
encoding from the first byte of the frame body**:

| Encoding | First body byte |
|---|---|
| JSON `{"type":…}` | `0x7B` (`{`) |
| Protobuf `Envelope` | `0x08` — tag for field 1 (`type`, varint) |

`type` is `>= 1` for every real message, so proto3 never elides field 1 and a
protobuf `Envelope` *always* begins with `0x08`. The two values cannot collide,
so one byte classifies a frame with no negotiation, no version field, and no
extra round trip.

That buys three things a version handshake would not:

1. **Independent deploys.** The gateway, the game servers and the Unity client
   ship on different cadences and, in Agones, are literally different pods
   rolling at different times. A server that accepts both and *replies in
   whatever it was addressed in* has no ordering requirement at all. There is no
   flag day and no window where a mismatched pair is broken.
2. **Sticky per connection, not per process.** Encoding is latched per
   connection (`ClientConn.enc` in the gateway, `Connection.Encoding` in the game
   server), so one binary serves a legacy JSON client and a Protobuf client
   simultaneously — the actual mid-rollout state, and what
   `TestDotnetInterop_MixedEncodingsOnOneServer` pins.
3. **A controlled benchmark.** Because the server answers in the client's
   encoding, `loadtest -encoding json|proto` A/B-tests one unchanged server
   binary. The before/after is a single-variable comparison instead of one
   spanning two builds.

Framing is untouched — still `[4-byte big-endian length][body]` — so
`shared/transport` and the KCP/TCP layer required no changes and cannot observe
the difference.

The cost of sniffing is that a corrupt frame whose first byte is not `{` is
reported as a protobuf parse failure rather than as "unknown encoding". That is
acceptable: both are fatal for the frame and both close the connection.

### Why the Go domain structs did not become the generated types

`messages.SnapshotMessage` and friends stay plain Go structs; `proto.go`
converts them to and from the generated types and is the only place the two
representations meet. Generated types carry state that makes them awkward as
domain values (they are not cheap to copy or compare, and `SnapshotState` keeps a
map of them), and every existing caller across gateway, smoketest, loadtest and
integration_test already speaks the plain structs. The conversion is confined to
one file, so a schema change surfaces there as a compile error rather than as a
silently unset field.

The C# side made the **opposite** choice — there the generated types *are* the
only message classes — because the game server is where the per-tick cost lives
and an extra domain-to-generated conversion per entity per client per tick is
exactly what this change exists to remove.

### `UnmarshalPayload` became a method

`messages.UnmarshalPayload(payload, v)` was a free function taking raw bytes,
which made it possible to decode a Protobuf payload as JSON by forgetting to
thread the encoding through. It is now `env.UnmarshalPayload(v)`: the encoding
travels with the bytes and the mistake is unrepresentable. This is a Go API
break, not a wire break.

### Deliberately left on the table

`EntitySnapshot.type` is still a string and `id` is still a full string. An enum
for `type` (~8 bytes to ~2) and interned entity handles would both shrink the
hottest message further, but each changes what the field *means*, whereas this
change is a pure re-encoding whose correctness is checkable by round-tripping.
Mixing them in would also make the before/after impossible to attribute.

**This follow-up is not optional polish, and it is probably larger than this
change was.** The measurement in [BENCHMARK.md](../../docs/BENCHMARK.md) Part II
says Protobuf moved the mobile bandwidth ceiling from ~41 to ~93 players against
a tick ceiling of 300 — so bandwidth is *still* the binding constraint, and the
absolute gap actually widened. **Measured: 61%** of a packed `EntitySnapshot` is
string data — 17.0 bytes of `id` and 8.0 of `type` against a 41.2-byte marginal
cost per entity. (An earlier revision of this note said ~40%; that counted only
the `id` and was wrong.) No encoding can compress that away. Interning
those is the only remaining lever on the wire itself; after that it is a question
of sending less (AOI radius, distance-tiered update rates), not of encoding.

## Entity-id interning: handles scoped to a keyframe interval (2026-08-07)

A realistic entity id (`lt-000000000042`) costs ~17 bytes on every mention, 41%
of a packed `EntitySnapshot` and the single largest term once the type became an
enum. Interning replaces repeat mentions with a varint handle.

**This is protocol state, not a re-encoding.** Everything before it — Protobuf,
the type enum — could be validated by round-tripping a single message, because a
message meant the same thing in isolation. A handle does not: it means whatever
the two ends agree it means. So the correctness risk lives entirely in them
disagreeing, and a happy-path test proves nothing.

### The lifecycle

- Handles are allocated per connection, from 1, and **reset at every keyframe**.
- An entity's id is sent **once per interval**, on the message that introduces
  its handle. Later mentions carry the handle alone.
- A handle is **never reused within an interval**, even after the entity
  despawns.
- `handle = 0` means "not interned", so a peer that does not implement this — or
  a JSON connection, which has no handle field — is unaffected.

### Why the keyframe is the reset point

It is a synchronisation point both ends *already* agree on, and it was already
self-sufficient: a keyframe replaces the entity set outright. Making it also
reset the handle space means every binding is re-introduced there, so **any
divergence repairs itself within one keyframe interval whether or not either
side noticed it**. No new agreement, no new message type, no negotiated
handshake — the recovery path is the one that already existed (`MsgResync`).

It also keeps handles small. Bounded by the entities seen in one interval, they
stay inside a one- or two-byte varint rather than growing without limit over a
long session.

### Why handles are not reused

Reuse would keep them smaller still. It is rejected because of *how* it fails: a
receiver that missed a despawn would silently attribute an update to the wrong
entity. That is **wrong state, not absent state** — it looks entirely valid,
renders as a real entity in the wrong place, and nothing detects it. An
unresolvable handle, by contrast, is loud and recoverable.

The same asymmetry runs through the loadtest's validity gate: prefer the failure
a human can see.

### The receiver must refuse, not guess

`SnapshotState.Apply` returns `ErrUnknownHandle` when a snapshot references a
handle it has no binding for, and **applies nothing** — resolution happens before
any mutation, so a partially-resolvable snapshot leaves no partial state. A
half-applied snapshot is worse than an unapplied one because it looks like valid
state.

The caller's correct response is to request a keyframe. `backend/loadtest` does
exactly this and counts the resyncs, so a client that is quietly resyncing every
tick shows up as a number rather than as an inexplicably small bandwidth figure.

### What is deliberately not interned

`SnapshotMessage.removed` still carries ids. Removals are comparatively rare, the
client keys its reconstructed world by id, and interning them would mean the
despawn path depends on the same table it is tearing down. Worth revisiting with
a measurement, not before.
