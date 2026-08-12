# Changelog — Shared Module

All notable changes to the shared module will be documented in this file.
Format based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Added
- **`SnapshotState.Apply` now rejects a keyframe that carries a bare interned
  handle, instead of resolving it against the outgoing interval's table.**
  To be plain about the status: this is **latent, not a live bug**. No sender in
  this repo can produce the triggering frame — `SnapshotDeltaState.EncodeFull`
  clears its handle table and restarts numbering at 1 *before* encoding, so every
  entity in a keyframe carries both `id` and `handle`, and `wire.proto` states
  the same contract. Nothing was breaking. This is defence against a future or
  third-party sender.
  It is worth guarding because of *how* it fails rather than how likely it is.
  Handles reset at every keyframe, so a bare handle on a keyframe still resolves
  — against bindings from the interval the keyframe is ending. The lookup
  succeeds, no error is raised, and the entity is silently rebound to whatever
  last held that number: one entity's updates rendered as another's, undetectable
  downstream. The guard therefore does **not** consult the table at all when
  `Full` is set; consulting it is the failure.
  Ordering is unchanged — the check still runs in the resolve pass, before any
  mutation, so a rejected keyframe leaves state untouched rather than clearing
  the world and refilling it a resync later. Returns `ErrUnknownHandle` (wrapped,
  so existing `errors.Is` recovery paths keep working) with a message naming the
  keyframe case.
  Raised by the Unity client team while auditing their implementation against
  `gameserver-dotnet/docs/API.md`; the doc was corrected in the same change and
  now describes this behaviour normatively.
  Covered by `TestKeyframeWithBareHandleIsRefusedNotResolvedAgainstStaleTable`,
  which sets up a *resolvable* stale binding specifically so the test fails if
  the guard is removed and the lookup is allowed to succeed.
- `GatewayKickChannel` constant (`"gateway:kick"`) in `constants/keys.go` for
  cross-gateway duplicate-login Pub/Sub coordination
- **MsgPing/MsgPong (type 11/12) heartbeat messages.** `PingMessage{timestamp}`
  carries the sender's monotonic clock; `PongMessage{timestamp, server_time}`
  echoes it back with the responder's wall clock. Both JSON and Protobuf
  encode/decode are implemented. Wire type numbers are frozen.

- **MsgKick (type 15) server→client forced disconnect.** `KickMessage{reason}`
  carries a machine-readable reason string. Defined reasons: `duplicate_login`,
  `server_shutdown`, `session_expired`, `rate_limited`.

- **JTI claim in join tokens.** `SignWithServer` now generates a UUID v4 `jti`
  claim when `serverID` is non-empty. Enables per-server replay protection.
- **5-second clock skew tolerance.** `Claims.IsExpired()` now accepts tokens up
  to 5 seconds past their `exp`, so small clock differences between gateway and
  game server do not cause spurious rejections. Constant: `jwt.ClockSkew`.

### Changed
- **BREAKING (Go API):** `Config.EffectiveJoinTokenSecret` removed. `JOIN_TOKEN_SECRET`
  is now mandatory; there is no fallback to `JWT_SECRET`.
- **MsgTransferMap / MsgTransferMapResp (types 13/14).** Wire messages for
  client-driven map transfer. `TransferMapRequest` carries a `map_id`;
  `TransferMapResponse` carries `ok` and `error`. Protobuf + JSON codec support
  added in `messages/` and `proto/wire.proto`.

### Added
- **Entity-id interning on the protobuf wire.** A realistic entity id costs ~17
  bytes on every mention; repeat mentions now carry a varint `handle` instead.
  Measured at ~51% of downstream bandwidth.

  Handles are per connection, allocated from 1, **reset at every keyframe**, and
  **never reused within an interval**. The id is sent once per interval, on the
  message that introduces the handle. `handle = 0` means "not interned", so JSON
  connections and any peer that does not implement this are unaffected.

- `ErrUnknownHandle`. `SnapshotState.Apply` now returns an error and **applies
  nothing** when a snapshot references a handle it has no binding for.
  Resolution happens before any mutation, because a half-applied snapshot is
  worse than an unapplied one — it looks like valid state. The caller's correct
  response is to request a keyframe, which re-introduces every binding.

### Changed
- **BREAKING (Go API):** `SnapshotState.Apply` returns `error`. Ignoring it means
  silently accepting a desynchronised stream.

### Added
- **`EntityType` enum on the protobuf wire.** A string entity type costs 8 bytes
  ("player" = tag + length + 6 characters) for a value drawn from a set of two;
  the enum costs 2. Measured at ~15% of a whole 50-entity snapshot payload and
  ~19% of a packed entity.

  `EntitySnapshot.type_name` remains as a string fallback, used ONLY when the
  enum cannot express the value, so a simulation that grows a new entity kind
  before the schema does degrades to the old cost rather than dropping the type.
  Exactly one of the two fields is ever set.

  JSON is unchanged — the enum is a protobuf-wire optimisation, not a
  protocol-wide change of meaning, and a pre-enum client still parses
  `"type":"player"` as text.

  The Go mapping (`entityTypeToPB`) and the C# one (`EntityTypes`) are
  hand-mirrored and both pin the exact name set, because a name added to one and
  not the other would silently degrade that type to the string fallback in one
  language only.

### Added
- `ErrInvalidMsgType`. Message type 0 is now rejected both when an envelope is
  constructed and when one is decoded. This is a correctness guard, not a
  formality: sniffing narrows a body to one of two decoders but cannot tell a
  real Protobuf envelope from arbitrary bytes that happen to be valid Protobuf —
  a body beginning `0x12` parses cleanly as an `Envelope` carrying only field 2
  and leaves the type at 0. That previously produced a typeless envelope and **no
  error**, a silent half-parse. Decoding now fails closed. Rejecting type 0 at
  construction protects the other half of the invariant: proto3 elides a zero
  field 1, so a type-0 envelope would encode without the `0x08` prefix and be
  sniffed as the wrong encoding by the peer.
- **`shared/proto/wire.proto` — the single source of truth for the realtime wire
  format.** The Go bindings are generated into `shared/proto/gen` (package
  `wirepb`) by `shared/proto/generate.sh`, which also emits the C# side into the
  game server. Generated code is committed, so no CI runner needs `protoc`
  installed to build or test either module.
- The `messages` package now speaks **both JSON and Protobuf**. `Envelope` gained
  an `Enc` field — transport metadata, never serialized — and `Payload` is now
  `[]byte`; custom `MarshalJSON`/`UnmarshalJSON` keep the JSON wire shape
  byte-identical to what shipped before.
- Encoding is detected from the first byte of the frame body, not negotiated: a
  JSON body always starts with `{` (0x7B), a Protobuf `Envelope` always starts
  with 0x08 (the tag for field 1, `type`, which proto3 never elides because
  `type` is >= 1 for every real message). Those cannot collide, so there is no
  handshake, no version field, and the 4-byte length framing is unchanged.
- New API: `Encoding` (with `ParseEncoding`/`String`), `SniffEncoding`,
  `EncodeBody`, `DecodeBody`, `NewEnvelopeAs`, and `Envelope.Reply` — the last
  two are how a caller answers in the encoding it was addressed in.
- JSON remains the default. On its own this changes no bytes on the wire.

### Changed
- **BREAKING (Go API only, not the wire):** `messages.UnmarshalPayload(payload,
  v)` is gone as a free function and is now a method, `env.UnmarshalPayload(v)`.
  The encoding travels with the bytes it describes, so a payload can no longer be
  decoded with the wrong codec by forgetting to thread the encoding through the
  call. Every caller in the repo is updated.

### Added
- `redisstore.ClientOptions` + `NewRedisClientWithOptions` — explicit timeout,
  retry and pool configuration for every Redis client this package builds
  (`Default*` constants; the zero value of each field falls back to the default,
  and a negative value is preserved because go-redis reads it as "disabled").
  The client was previously built with only `Addr`/`Password`, so every call
  inherited go-redis defaults and an unreachable Redis could occupy a caller for
  tens of seconds. Verified against a stopped Redis container: a `Get` now fails
  in ~1.4s instead of hanging (DR audit **G5**)
- `redisstore.Ping` — liveness probe with a bounded timeout independent of the
  caller's context, for readiness handlers
- `redisstore.EventStream.SetLogger` / `GroupLosses` — observability for
  consumer-group recovery

### Fixed
- **Event relay could die silently after a Redis wipe.** `XREADGROUP` against a
  missing consumer group returns `NOGROUP`, which the consume loop treated as a
  generic transient error: it retried at 2Hz forever, logged nothing, and never
  re-created the group, so the process looked healthy while the relay was
  permanently dead. `NOGROUP` is now detected specifically, the group is
  re-created, and the event is counted (`GroupLosses`) and logged. Proven
  against a real Redis with `FLUSHALL` mid-subscription: recovery is automatic
  and delivery resumes (DR audit **G4**)
- **Memory and Redis session stores disagreed on the "missing key" contract.**
  `redisstore` returned `storage.ErrNotFound` while `storage/memory.go` returned
  a bare `fmt.Errorf(... not found)`, so no `errors.Is` check could tell a
  missing key from an infrastructure failure on the memory backend. Every
  memory-store "not found" now wraps `storage.ErrNotFound`. This is what makes
  the gateway's Redis-blip fix work on both backends (DR audit **G6**)
- `errors.Is(err, code)` in `shared/errors` used a bare type assertion, so it
  returned false for any wrapped `GameError` — and the repo-wide convention is
  to wrap with `%w`. It now uses `errors.As`, so classification survives
  wrapping instead of silently falling through to the default branch
- `NewEventStream` gives its client a read timeout above the `XREADGROUP` block
  duration; with the new bounded defaults an equal-or-smaller read timeout would
  turn every idle poll into a spurious i/o timeout

### Added
- `ratelimit` package — shared token-bucket limiter. `Bucket` (lock-free, zero
  allocation, 10.8 ns/op — embed per connection) and `Limiter` (keyed per
  IP/user, mutex-guarded, TTL eviction with `StartCleanup`/`Stop`). A nil
  `*Limiter` and the zero `Bucket` both allow everything, so "disabled" needs no
  nil checks. Per-process scope; Redis-backed is the production upgrade (ADR-8)
- `jwt.Keyring` — secret rotation. `JWT_SECRET`/`JOIN_TOKEN_SECRET` accept a
  comma-separated list: the first entry signs, every entry verifies, so rotating
  a secret no longer invalidates every live token. The zero `Keyring` fails
  closed (rejects everything, refuses to sign). Adds `Claims.IsZero()`
- `transport.WithKey` / `WithLogger` / `Encrypted` / `DeriveKey` / `KeyEnvVar` —
  opt-in KCP encryption using kcp-go's AES-256 `BlockCrypt`. `TRANSPORT_KEY`
  takes 64 hex chars (used verbatim) or a passphrase (HKDF-SHA256 stretched).
  Empty = plaintext, and a KCP listener now logs a WARN when it starts
  unencrypted. Encryption fails closed: a peer with the wrong key never
  establishes a session
- `config`: `JoinTokenSecret` (`JOIN_TOKEN_SECRET`), `TransportKey`
  (`TRANSPORT_KEY`), `GatewayConnRatePerMin`/`GatewayConnBurst`/
  `GatewayMsgRatePerSec`/`GatewayMsgBurst`, and
  `Config.EffectiveJoinTokenSecret()`
- `messages.SnapshotMessage` gained three `omitempty` fields for the delta snapshot
  protocol: `ack_tick` (newest client input tick the server accepted for this player —
  the client's reconciliation anchor), `full` (this snapshot is a keyframe carrying
  the complete AOI set) and `removed` (entity IDs that left the AOI/world on a delta).
  All are omitted when default, so a keyframe-only stream is byte-identical to the
  previous wire format and pre-delta readers keep working.
- `messages.MsgResync` (type 10) — client → gameserver request for a full keyframe.
- `messages/snapshot_state.go` — `SnapshotState`, the Go reference implementation of
  the client-side keyframe/delta merge (keyframe replaces, delta upserts + removes,
  `tick`/`ack_tick` monotonic). Used by the smoke test and the integration tests so
  the merge rule is not reimplemented per consumer. C# mirror:
  `Shared.GameLogic.Systems.SnapshotMerger`. Wire reference:
  `backend/gameserver-dotnet/docs/API.md`.

### Changed
- `transport.Listen`/`Dial` take a variadic `...Option` tail. **Source
  compatible** — every existing call site compiles unchanged
- `storage/pgstore/schema.sql` — header comment only, no SQL change. The game-state
  schema is now owned by numbered migrations in `backend/deploy/db/migrations/gamestate/`
  applied by the C# gameserver; this package has been orphaned since that migration.
  The file is kept byte-identical to `deploy/db/init-gamestate.sql` because
  `TestSchemaMatchesDeployInitScript` compares them exactly.
- `Envelope.Payload` type changed from `[]byte` to `json.RawMessage` for C#
  interop — `System.Text.Json` on the gameserver-dotnet side expects a raw JSON
  object, not a base64-encoded byte array.

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
