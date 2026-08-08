# Changelog

All notable changes to the GameServer .NET module will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Added
- **Graceful drain notification on shutdown.** On SIGTERM, the server now sends
  `MsgDisconnect(reason="server_shutdown")` to all connected clients before
  closing connections. A 2s grace period lets TCP drain the send buffer so
  clients receive the notification and can reconnect to another server instead
  of timing out. Adds `WireProtocol.NewEnvelope` for `DisconnectMessage` (with
  reason) and the corresponding JSON serializer in `WireJson`
- **JTI replay protection.** `JtiTracker` rejects consumed join-token JTIs for
  60 seconds (2x the 30s token TTL). A replayed token returns "Token already used".
- **5-second clock skew tolerance.** `JwtValidator` now accepts tokens up to 5
  seconds past their `exp`. Constant: `JwtValidator.ClockSkewSeconds`.

### Changed
- **`JOIN_TOKEN_SECRET` is now mandatory (fatal if unset).** `Program.cs` exits
  with code 2 when the env var is empty. `EffectiveJoinTokenSecret` (fallback to
  `JWT_SECRET`) has been removed from `ServerOptions`.
- **Mandatory server ID check.** The game server now rejects join tokens with an
  empty `sid` claim or a `sid` that does not match `ServerId`. The previous
  double-empty bypass has been removed.
- **Map transfer handler (MsgTransferMap).** A connected player can request
  transfer to a different map. The server validates the target map, saves state
  via `AsyncSaver.SavePlayerAsync`, responds with `TransferMapResponse`, removes
  the entity (no reconnect hold), and closes the connection. The client then
  follows the existing `MsgEnterWorld` flow with the gateway.
- JSON codec for `TransferMapRequest` / `TransferMapResponse` (write + read),
  `NewEnvelope` overloads, and `GetPayload` cases for both types.
- xUnit tests: `TransferMapTests` (3 cases: success, same-map rejection,
  empty-map rejection) and `WireProtocolTests` round-trip tests for transfer
  messages.
- **Heartbeat loop (MsgPing/MsgPong) on player connections.** Each accepted
  connection sends MsgPing every 10 s after join. If no MsgPong is received
  within 30 s the connection is closed. Incoming MsgPing from a client is
  answered with a MsgPong echoing the sender's timestamp plus the server's
  wall clock. Heartbeat runs as a third task alongside read/write loops.

- **MsgKick support.** `WireProtocol.NewEnvelope` overload for `KickMessage`;
  `JsonWriter.Write(KickMessage)` and `JsonReader.ReadKickMessage` for JSON
  encoding; Protobuf encoding via generated `Wire.cs`.

### Fixed
- **`ObjectDisposedException` out of `ShutdownAsync` when `Close()` raced
  `Dispose()`.** Under Agones that means terminate throws instead of draining.

  `Close()` guarded itself with a single flag and `Dispose()` used that guard as
  a barrier. It is not one: the early return means "another thread STARTED
  closing", never "another thread FINISHED closing". So `Dispose()` could free
  the `CancellationTokenSource` while the other thread sat between its CAS and
  its `Cancel()`.

  Replaced with a three-state lifecycle (open → closing → closed); `Dispose()`
  waits for *closed* before disposing the CTS. A spin rather than a lock because
  `CancellationTokenSource.Cancel` runs registered callbacks inline and a lock
  across arbitrary callback code invites a deadlock. The state transition to
  *closed* is in a `finally`, so a throwing callback cannot strand it and spin
  `Dispose()` forever.

  **`KcpSession` had the identical shape and was also broken** — verified by
  reproducing the same exception against the unfixed file. Fixed at the same
  time rather than waiting for a KCP deployment to find it.

  This is the third appearance of one pattern: the 2026-08-06 blocker
  (`GameServerHost._cts?.Cancel()`, where `?.` guarded null but not disposed) was
  fixed at the one call site that threw, and the pattern was not swept. All six
  `CancellationTokenSource` sites in the module have now been audited — see the
  PR for the per-site verdict.

### Changed
- `MetricsEndpoint.DisposeAsync` is now idempotent. Single-owner today, so this
  is hardening rather than a fix, but an unguarded Cancel-then-Dispose is the
  exact shape that has now thrown twice.

### Added
- **`gameserver_resyncs_total`** (counter, `map_id`) — keyframes requested by a
  client via `MsgResync`. Expected value is approximately zero.

  This is the only field-visible signal that entity-id interning has gone wrong.
  A client sends `MsgResync` only when it cannot reconstruct state from the delta
  stream, and the likeliest cause is a snapshot referencing an entity handle it
  has no binding for — the two ends disagreeing about the interning table.
  Interning is backward compatible by construction and by test, but had no way to
  be observed failing in production; now it does.

  Counts client-initiated resyncs **only**, never the periodic keyframe. Folding
  the routine one in would bury the signal under a constant background rate.
  `docs/METRICS.md` says what a rising rate means and what it invalidates.

  No gateway-side equivalent exists, deliberately: `MsgResync` goes client to
  game server directly (ADR-3), so a gateway counter would always read zero —
  worse than absent, because a permanently-zero series looks healthy.

### Added
- **Entity-id interning**, gated on the connection's encoding. `SnapshotDeltaState`
  keeps a per-connection handle table reset at every keyframe, writes the id only
  on the message that introduces a handle, and never reuses a handle within an
  interval — reuse would let a client that missed a despawn attribute an update
  to the wrong entity, which is wrong state rather than absent state.

  `Encode` takes `intern:`; `TickLoop` passes `conn.Encoding == WireEncoding.Proto`.
  JSON has no handle field, so interning there would emit entities with an empty
  id and silently break every pre-interning client.

### Added
- **`GameServer/Net/EntityTypes.cs`** — maps the simulation's string entity types
  to the wire enum and back, the C# mirror of Go's `entityTypeToPB`. Unrecognised
  names travel in `EntitySnapshot.TypeName` instead of being dropped, so a new
  entity kind cannot silently break an older client.

### Changed
- The snapshot encoders set the entity type through `EntityTypes.SetType`, which
  writes the enum when the name is known (2 bytes) and the string only when it is
  not. The JSON codec still emits and parses the string form, so the legacy wire
  is byte-identical.

### Changed
- **The generated `wire.proto` types are now the server's only message classes.**
  The hand-written C# mirrors of the Go structs are deleted, which removes exactly
  the two-definitions drift `wire.proto` exists to prevent. They are imported as
  explicit global `Using` aliases so `RpgMmo.Wire.V1.Envelope` (the protobuf
  message) never collides with `GameServer.Net.Envelope` (the framing envelope
  that carries the encoding metadata).
- `Connection` latches the encoding of the client's first frame and every reply
  uses it, so a single binary serves JSON and Protobuf clients side by side and
  the server never chooses an encoding of its own.
- The JSON path no longer round-trips its own freshly serialized payload through
  `JsonDocument.Parse` just to nest it inside the envelope — pure waste that sat
  on the per-tick snapshot path.
- `SnapshotMessage.Removed` is a protobuf `RepeatedField`, so it is now empty
  rather than `null` when there are no removals. The JSON on the wire is
  unchanged: the field is still omitted when empty.

### Added
- Legacy JSON stays fully supported through a hand-written
  `Utf8JsonWriter`/`Utf8JsonReader` codec (`GameServer/Net/WireJson.cs`) that
  reproduces Go's `omitempty` rules byte for byte. Protobuf's own `JsonFormatter`
  was evaluated and rejected: it emits camelCase and drives descriptor
  reflection, so it matches neither this wire format nor NativeAOT.
- `Google.Protobuf` 3.29.3. `dotnet publish -c Release` with `PublishAot`
  succeeds with **zero trim/AOT warnings** — the generated serializers are used,
  not the reflection-based ones.

### Fixed
- **`DecodeBody` half-parsed garbage as a typeless envelope.** A body beginning
  `0x12` is valid Protobuf (field 2, length-delimited) and parsed cleanly with
  the type left at 0, so arbitrary bytes became a well-formed envelope with no
  error. Type 0 is now rejected on decode and at construction, and both decoders
  fail closed. Pinned by a 1..255 sweep of the prefix invariant rather than by a
  comment.
- **Replies fell back to legacy JSON after the join handshake.** The handshake
  runs on a throwaway `Connection` and the session `Connection` was then
  constructed fresh over the same socket, dropping the encoding the client had
  already demonstrated. The handshake now hands its latched encoding to the
  session connection. Caught by the new mixed-encoding integration test.
- **Entities leaked when a join was aborted.** `gameserver_players_online` returned to
  0 while `gameserver_entities` stayed at its peak indefinitely — 200 entities with 0
  players, still there minutes later, reproduced below.

  The hold mechanism was not at fault and the gauge was not lying. `AddEntity` has
  exactly one call site (the join path) and `RemoveEntity` only ever runs from the
  reconnect-hold task, so an entity whose hold is never *scheduled* is unreachable
  forever. `OnPlayerDisconnected` was called on the happy path only, at the end of the
  `try`. Any throw after the entity was attached — most easily the `WriteOneAsync` that
  sends `JoinTokenResp`, against a client that gave up during the handshake — skipped
  it entirely.

  The asymmetry in the symptom is what identified the path: `players_online` is an
  independent counter incremented *after* that write, so an abort before it leaves the
  player count correct and only the entity count wrong. That is exactly what was
  observed.

  Teardown now runs from a `finally` block, guarded by whether the entity was actually
  attached. A second flag tracks whether `PlayerJoined()` was recorded, so an aborted
  join cannot decrement a counter it never incremented and corrupt the count for
  players who really are online.

  Also in the same path, all reachable from an aborted or racing join:
  - A superseded hold's `CancellationTokenSource` was neither cancelled nor disposed,
    leaving a live timer for a removal the newer hold already owns.
  - The expiry task claimed its removal non-atomically. It now uses
    `TryRemove(KeyValuePair)`, which only succeeds while *its* hold is still registered,
    and additionally refuses to remove an entity that has a live connection — a
    reconnect during the pre-removal save must not have its entity deleted underneath
    it.
  - An unexpected exception in the expiry task was swallowed, silently leaking the
    entity it was responsible for. It now logs and removes anyway.
  - `holdCts` is disposed on every path.

  `GameServerHost.EntityCount` / `PendingHolds` are exposed so tests assert the number
  an operator sees rather than a parallel count that could agree while the gauge lies.

- **Keyframe stampede: per-connection keyframe counters are now staggered.** Every
  connection started its counter at zero, so clients that joined on the same tick
  keyframed on the same tick afterwards, forever, serializing full state for the whole
  cohort at once.

  `SnapshotDeltaState` takes a phase derived from the user id (FNV-1a, not
  `string.GetHashCode()`, which is randomized per process). Deterministic on purpose:
  a random offset would spread load equally well but make a replay of the same session
  produce different frames — the same reasoning that puts cooldowns on tick counts
  rather than wall clock.

  The phase is applied **once**, right after the join keyframe, shortening a single
  cycle. A permanent offset would shorten this client's cycle forever and hand it more
  keyframes, and more bandwidth, than everyone else. The parameterless constructor is
  unstaggered, so existing callers are unaffected.

  Note: no end-to-end latency improvement is claimed — see the PR. The dev box's
  run-to-run variance swamps the effect; the unit tests prove the keyframes are spread.

### Added
- **KCP transport for the gameplay hop (`--transport kcp` / `GAMESERVER_TRANSPORT`).**
  Until now this flag only selected what got *advertised*: the C# server had no KCP
  and always bound TCP, so a "KCP deployment" was half a deployment — the Go side
  shipped KCP for the client→gateway hop while the gameplay hop stayed TCP. The
  server now really listens with KCP over UDP, wire-compatible with
  `backend/shared/transport` (`github.com/xtaci/kcp-go/v5`).
  - `GameServer/Net/Transport/` — a port of kcp-go's protocol subset: the ARQ
    (`Kcp.cs`), kcp-go's crypt framing (`KcpCrypto.cs`), the UDP listener with
    per-endpoint session demultiplexing (`KcpListener.cs`, `KcpSession.cs`), and a
    `Stream` adapter (`KcpStream.cs`) so the length-prefixed JSON codec rides on
    top unchanged. kcp2k (Mirror's C# KCP) was evaluated and rejected: its
    handshake/cookie layer is not on the wire kcp-go speaks. Rationale and the
    interop evidence: `docs/DESIGN.md`, 2026-08-07.
  - Tuning matches the Go constants exactly (nodelay 1, interval 10ms, resend 2,
    congestion control off, 128/128 windows, MTU 1350, stream mode, FEC off).
  - `Connection` and `GameServerHost` now take an `ITransportConnection` /
    `ITransportListener` instead of `TcpClient` / `TcpListener`. TCP remains the
    default and its behaviour is unchanged; the `Connection(string, TcpClient,
    ILogger)` constructor is kept.
  - `interop/kcpprobe` — a Go client harness that dials through
    `backend/shared/transport` and completes a real join, so interoperability is
    asserted against the actual kcp-go implementation rather than a C#-to-C#
    loopback. Interop tests skip when no Go toolchain is present.

### Fixed
- **Cross-map position bleed on join.** `player_states` holds one row per player,
  overwritten by whichever server hosts them, and the join path restored its
  `x`/`y` unconditionally. A player who last stood at (480, 12) on `map_02` and
  then joined `map_01` was recreated at (480, 12) *on `map_01`* — a different
  place entirely. The row never converged either: each join wrote back the stale
  base plus whatever they walked, so the drift compounded.

  Placement now goes through `PlayerSpawn.Resolve`, which reuses saved
  coordinates only when the row's `map_id` matches the map being joined and
  otherwise places the player at that map's spawn point. HP and max HP carry
  across unchanged — they belong to the character, not to the ground under it.
  An empty `map_id` counts as a mismatch rather than a wildcard, because the
  column defaults to `''` and such a row has unknown provenance. The row
  converges on the next save with no extra write, since `AsyncSaver` already
  uses the hosting server's own `MapId`.

  Rationale and the full decision table: `docs/DESIGN.md` — "Position is
  map-scoped; carried stats are not". Policy is a pure function rather than
  inline join-handler code, so every branch is testable without a database, a
  socket or a running server.

  Covered by `PlayerSpawnTests` (policy) and `MapIdReloadIntegrationTests`
  (real PostgreSQL + real TCP join handshake, `[SkippableFact]` per the
  dependency-gating convention). Three of the four integration tests fail
  against the pre-fix join path — `Expected: 0 … Actual: 137.5` — so they
  pin the regression rather than decorating it.

  Not fixed here: `player_states` has no `dead` column, so a player persisted
  at `hp = 0` reloads with `Hp = 0` and `Dead = false`. That needs respawn rules
  and a schema change; this change preserves the existing HP behaviour exactly.
- The realtime transport published into the registry is now what the server
  actually listens with, so `EnterWorldResponse.Transport` tells clients the truth.
  It was previously whatever `--transport` said, regardless of the TCP listener
  underneath — a client that honoured the field would have dialled KCP at a TCP
  socket and simply hung.
- **The last three soft skips in the test suite now report as real skips.** Tests
  gated on an external dependency used to `Console.WriteLine("[SKIP] ...")` and
  `return`, which xUnit records as **PASSED** — so a run without the dependency
  reported the same totals as a full run and absence of coverage was
  indistinguishable from coverage. The postgres/redis fixtures were converted to
  `Skip.IfNot` earlier; these three were missed:
  - `MigratorTests.EmbeddedMigrations_MatchDeployCopies` and
    `MigratorTests.InitGamestateSql_MatchesFirstMigration` — gated on the deploy
    SQL being reachable in the repo tree; now `[SkippableFact]` + `Skip.If`.
  - `PostgresPlayerStoreTests.Save_AfterDatabaseGoesAway_SurfacesErrorAndIncrementsMetric`
    — its *dedicated* throwaway container (the one it kills mid-test) could fail to
    start after the shared-fixture gate had already passed, silently voiding the
    test; now `Skip.If`.
  No assertion was weakened and nothing skips unconditionally. Verified both
  directions: docker up → `Passed: 287, Skipped: 0`; docker off `PATH` →
  `Passed: 261, Skipped: 26`.
- Test convention documented in `CLAUDE.md` (§ Testing) so the soft-skip pattern is
  not reintroduced: dependency-gated tests must skip, never silently pass.

### Security
- **KCP traffic can be encrypted with the same pre-shared key as the Go side
  (`TRANSPORT_KEY`).** AES-256 is applied per datagram below the ARQ, so the join
  token and every snapshot are covered. Key derivation matches
  `shared/transport/crypto.go`: 64 hex characters verbatim, anything else stretched
  with HKDF-SHA256 under the info string `rpg-mmo/transport/kcp/aes-256` — asserted
  against the real Go implementation, because a silent derivation drift would look
  exactly like a network fault.
  - There is no negotiation and no downgrade: a peer without the key produces
    datagrams that fail the checksum and are dropped, so "encrypted server +
    plaintext client" fails closed rather than falling back to cleartext.
  - A KCP listener with no key logs a start-up WARNING mirroring the Go wording.
    `TRANSPORT_KEY` set with `--transport tcp` is ignored and warned about — TCP has
    no packet encryption here.
  - Scope, and what is still *not* covered end to end (the client↔gateway hop is a
    separate setting; a PSK gives no forward secrecy and no protection from a peer
    that holds the key): `docs/DESIGN.md`, 2026-08-07.

- **Join tokens are verified with `JOIN_TOKEN_SECRET`, not `JWT_SECRET`.** The join
  secret is distributed to every game-server pod; the Nakama auth secret is not.
  Sharing them meant one compromised pod could mint auth tokens for any user. This
  is the C# half of the split already merged on the Go side — until now, enabling
  `JOIN_TOKEN_SECRET` on the gateway alone would have broken **every** join, because
  this server only knew `JWT_SECRET`.
  - New config: `--join-token-secret` / `JOIN_TOKEN_SECRET`. Unset falls back to
    `JWT_SECRET` (pre-split behaviour) and logs the same start-up warning the
    gateway logs, so the two halves cannot silently drift. The fallback lives in
    `ServerOptions.EffectiveJoinTokenSecret`, mirroring Go's
    `config.Config.EffectiveJoinTokenSecret`.
  - `JwtKeyring` (`GameServer/Server/JwtKeyring.cs`) — secret rotation. Both
    secrets accept a comma-separated `"current,previous"` list: the gateway signs
    with the first entry, every entry verifies here, so a rotation drains the old
    population over the join-token TTL instead of logging everyone out. Port of
    Go's `shared/jwt.Keyring`, including whitespace trimming, dropping empty
    entries, failing **closed** on an empty keyring, and short-circuiting on an
    expired token instead of retrying the remaining keys.
  - `JwtValidator.Verify` gained a `VerifyStatus` overload (Ok / Invalid /
    BadSignature / Expired) so the keyring can tell "wrong key, try the next" from
    "right key, dead token" — the distinction the Go short-circuit depends on. The
    existing two-argument overload is unchanged.
  - Verified against the real Go gateway on high ports: matching secrets → join
    accepted; deliberately mismatched secrets → join rejected; gateway signing with
    the rotated key against a `"previous,current"` keyring → join accepted.

### Added
- **Server self-registration and heartbeat (`GameServer/Registry/`).** The server now
  publishes its own entry into the Redis registry the Go gateway reads, refreshes it
  every 5s against a 15s TTL, updates `player_count` on join/leave, and deregisters on
  graceful shutdown. Wire-compatible with `shared/storage/redisstore/registry.go` —
  same keys (`servers:id:{id}` hash + `servers:map:{map}` set index), same field
  names, same `constants.ServerHeartbeatTTL`. **No gateway change was needed**;
  verified end to end with the real smoke test.
  - `RedisServerRegistry` — StackExchange.Redis implementation. `UpdatePlayerCount`
    uses the same Lua `EXISTS`-guard as the Go side, so a late writer cannot
    resurrect an expired entry as a TTL-less immortal one.
  - `RegistrationService` — **every heartbeat is also a repair.** When the entry is
    missing (Redis wiped, failover onto an empty replica, TTL lapsed during an
    outage) the next heartbeat re-registers it rather than just logging. That is what
    makes a Redis outage self-heal in one heartbeat interval instead of requiring a
    human to run a script. Registry failures never touch gameplay: every call is
    wrapped and retried, and the connection uses `AbortOnConnectFail=false` so the
    server boots and keeps serving even with Redis down.
  - New config: `--redis`/`REDIS_ADDR`, `--redis-password`/`REDIS_PASSWORD`,
    `--transport`/`GAMESERVER_TRANSPORT`, and `--public-addr`/`GAMESERVER_PUBLIC_ADDR`
    — the address advertised to CLIENTS, which is **not** the listen address when a
    container maps ports (listens `:9000`, published `:9200`). Falls back to the
    listen address, which is correct in host mode.
  - Replaces `scripts/register-gameserver.sh` (deleted), which wrote the entry once at
    deploy time with a 3600s TTL and nothing to refresh it. Closes G1 and G2 in
    `backend/deploy/docs/DISASTER-RECOVERY.md`.
  - `StackExchange.Redis` 3.1.11 added. NativeAOT publish verified clean (zero IL trim
    warnings) and the published binary exercised against a real Redis.
- Registry test suite against a **real Redis** in a throwaway container
  (`GameServer.Tests/Registry/`): exact hash/index shape the gateway reads, TTL
  re-arming, real expiry, deregistration, the player-count resurrection guard, and
  two self-healing tests — a wiped key repaired by the next heartbeat, and a full
  container stop/start proving the service survives an outage and re-registers.
- `GameServer.Tests/Infrastructure/TestDocker.cs` — docker plumbing shared by the
  postgres and redis fixtures instead of duplicated.
- `GameServer.Tests/Observability/MetricsEndpointTests.cs` — starts the real endpoint
  and scrapes it over HTTP: wildcard (`:port`, `0.0.0.0:port`, `*:port`) and named
  (`localhost:port`) binds both serve `/healthz` and `/metrics`, empty address
  disables, plus a `ParseAddr` normalization table. The three wildcard cases fail with
  `Assert.NotNull() Failure: Value is null` against the unfixed code. The wildcard
  cases scrape whichever authority actually got bound: on Windows the `+` prefix needs
  an admin URL ACL, so `TryStart` falls back to `localhost` and `HttpListener` answers
  `400` to a `127.0.0.1` Host header that matches no registered prefix. On Linux — CI
  and the production target — the test additionally asserts the bind really is `+`, so
  the fallback can never quietly become the normal path there.
- `InternalsVisibleTo` for `GameServer.Tests` so tests can assert on internal helpers
  without widening the public API.
- **Delta snapshots.** Each connection now receives a full keyframe on join, on
  `MsgResync` (type 10) request, and every `--keyframe-interval` snapshots (default
  30 ≈ 2s at 15Hz); every other snapshot carries only entities whose visible state
  changed plus an explicit `removed[]` despawn list. Measured on 1 moving player +
  8 stationary mobs over 100 ticks: **592.2 → 126.6 bytes/tick/client (−78.6%)**.
  New `SnapshotDeltaState` (per `Connection`) holds the last-sent state; its scratch
  collections are reused across ticks and the entity list is allocated lazily, so an
  unchanged tick allocates only the message itself. `--keyframe-interval 0` disables
  delta encoding entirely (full snapshot every tick, the pre-delta wire shape).
- **Input acknowledgement on the wire.** `SnapshotMessage.ack_tick` carries the
  newest input tick accepted for the receiving player's own entity — the anchor a
  predicting client rewinds to. `EntityState.LastInputTick` was already tracked but
  never serialized, which made client-side reconciliation impossible.
- `Shared.GameLogic.Systems.SnapshotMerger` — the normative client-side merge of the
  keyframe/delta stream, shared with the Unity client (Go mirror:
  `messages.SnapshotState`).
- `MsgType.Resync` (10) — client asks for a full keyframe on the next tick.
- `--keyframe-interval` / `GAMESERVER_KEYFRAME_INTERVAL`.
- `docs/API.md` — precise wire reference for the Unity client: framing, every
  message, the delta/keyframe semantics, the normative merge algorithm and the
  reconciliation procedure.

### Changed
- **Docker-dependent tests now report a REAL xUnit skip instead of passing silently.**
  `PostgresFixture.SkipIfUnavailable` returned early and xUnit recorded the test as
  PASSED, so a machine without docker reported exactly the same totals as a full run —
  absence of coverage was indistinguishable from coverage, and per-test duration was
  the only honest signal. `SkipUnlessAvailable` now uses `Skip.IfNot`
  (`Xunit.SkippableFact`), and the affected tests are `[SkippableFact]`/
  `[SkippableTheory]`. With docker: `250 passed, 0 skipped`. Without:
  `224 passed, 26 skipped`. The summary can no longer lie.
- **Combat cooldowns are now tick-based, not wall-clock.** `EntityState.CooldownUntilTicks`
  (a `DateTime.Ticks` value) became `CooldownUntilTick` (a `ulong` simulation tick);
  `CombatLogic.ValidateAttack` and `ValidationLogic.ValidateInput` take the current
  tick instead of `nowTicks`. Length comes from `GameConstants.AttackCooldownTicks(tickRate)`
  = `ceil(500ms × tickRate / 1000)` = 8 ticks (533ms) at 15Hz — rounded up so the
  cooldown is never shorter than the wall-clock one it replaced. The simulation now
  has a single clock, so replaying an input sequence always yields the same outcome;
  a wall-clock gate could not guarantee that, which blocked both client prediction
  and server-side replay of disputed sequences.
  **Breaking for in-flight callers:** `InputHandler.ProcessInput`/`ProcessInputLocked`
  take a `currentTick` parameter before `applyMovement`.
- `SnapshotData` (Unity-facing mirror) gained `ack_tick`, `full` and `removed`.

### Fixed
- **SIGTERM never shut the server down gracefully.** Termination was wired through
  `AppDomain.CurrentDomain.ProcessExit`, which cancels the token but does **not** wait
  for `Main` to unwind — the runtime terminates the process while shutdown is still in
  flight. So on SIGTERM the final save never ran (losing up to `SaveInterval` = 30s of
  player position/HP) and connections were never drained. Only SIGINT (Ctrl-C, via
  `Console.CancelKeyPress`) shut down properly — the one signal production never
  sends, since Docker, Kubernetes and an Agones drain all send SIGTERM. Both signals
  now go through `PosixSignalRegistration` with `Cancel = true`, which suppresses the
  runtime's terminate-now behaviour so shutdown actually completes. Found while
  verifying registry deregistration, which was silently not happening for the same
  reason.
- **The metrics/health endpoint never started on Linux with a wildcard address.**
  `METRICS_ADDR=:9101` (the default, and the deployed value) becomes the HttpListener
  prefix `http://+:9101/`. OpenTelemetry builds its own prefix as
  `new UriBuilder("http", Host, Port).Uri`, and `UriBuilder` rejects `+`/`*` with
  `UriFormatException: Invalid URI: The hostname could not be parsed`, thrown inside
  the `PrometheusHttpListener` constructor — so `/metrics` **and** `/healthz` silently
  failed to bind on every Linux deployment. Windows masked it by falling back to
  `localhost`. The exporter is now given a `UriBuilder`-safe placeholder host, and the
  real wildcard prefix is installed on the listener through `ConfigureHttpListener`,
  which runs before `Start()`. `backend/deploy` can now drop its
  `GAMESERVER_METRICS_ADDR=gameserver-dotnet:9101` workaround and go back to `:9101`
  (owner: agent-devops).
  Found by the E2E integration suite the first time it was actually executed.
- **`GameServerHost.ShutdownAsync` is now idempotent and concurrency-safe.** It is
  called from two places on essentially every termination: `RunAsync` invokes it at
  its tail when the run token is cancelled, and the process owner (SIGTERM handler,
  Agones drain, a test harness) invokes it directly. Both racers walked the entity
  hold table with `foreach (var kvp in _holds)`, so one could call `Cancel()` on a
  `CancellationTokenSource` the other had already `Dispose()`d — a pod that should
  have drained cleanly threw `ObjectDisposedException` out of `RunAsync` instead.
  This surfaced as an intermittent `PlayerPosition_SurvivesServerRestart` failure
  (~2 runs in 3) but the defect was in the server, not the test. The first caller now
  wins an `Interlocked.Exchange` and performs the teardown; every other caller awaits
  that same teardown and observes the same outcome, so "shutdown returned" always
  means "the final save finished". Holds are drained by `TryRemove` so each CTS has
  exactly one owner (the reconnect path races for the same entries), and the linked
  `CancellationTokenSource` is disposed only in `DisposeAsync`, once the run loop is
  guaranteed done with it.
- Player state is now persisted when a reconnect hold expires, before the entity is
  removed from the world. Previously `OnPlayerDisconnected` removed the entity
  without saving, so once it left the world the periodic `AsyncSaver` sweep could no
  longer see it and everything the player did since the last 30s tick was discarded.
  New `AsyncSaver.SavePlayerAsync(userId)` saves a single entity by id.
  See `backend/docs/ARCHITECTURE-DECISIONS.md`, ADR-6.
- Removed a dead conditional that selected `NoopAgonesSdk` in **both** branches of
  the `--agones` / `AGONES_ENABLED` flag. The flag never had any effect; it now
  logs a warning saying so, instead of implying that Agones health reporting works.
  No real Agones SDK client exists for the C# server yet (ADR-6 follow-up).

### Added
- `GameServer.Persistence.Migrator` — numbered, checksummed, transactional schema
  migrations. Scripts live in `GameServer/Persistence/Migrations/NNN_*.sql` and are
  embedded as assembly resources (read via `GetManifestResourceStream`, which is
  NativeAOT-safe), so the binary carries its own schema history.
  - Each pending script commits in its own transaction together with its
    `schema_migrations` row, so a failing migration leaves no partial schema and
    no version record and can simply be fixed and re-run.
  - Checksums of already-applied migrations are verified on every run; editing a
    shipped migration fails loudly with `MigrationDriftException`. Checksums cover
    statements, not comments, so rewording a comment is safe.
  - Concurrent runners are serialised by a PostgreSQL advisory lock — a whole
    fleet can boot at once. A database ahead of the binary warns instead of
    failing, so rollbacks still start.
- `--migrate-only` / `GAMESERVER_MIGRATE_ONLY=true` — apply pending migrations and
  exit without listening (exit 0 applied/current, 1 failure, 2 no DSN). CD uses it
  to migrate at a deterministic point before restarting servers.
- `001_init.sql` — the existing `player_states` schema as the first migration.
  Ops copies live in `backend/deploy/db/migrations/gamestate/`; tests assert the
  embedded scripts, the ops copies and `db/init-gamestate.sql` all agree.

### Changed
- `PostgresPlayerStore.MigrateAsync` now runs the migration set instead of a single
  hardcoded `CREATE TABLE IF NOT EXISTS` block, and returns a `MigrationResult`.
  The `SchemaSql` constant is gone — schema lives in migration files now.

- `GameServer.Persistence.PostgresPlayerStore` — PostgreSQL-backed `IPlayerStore`
  restoring the player-state persistence that was lost in the Go -> C# migration
  (ported from the now-orphaned Go `shared/storage/pgstore`). Saves are upserts on
  `user_id` refreshing `updated_at`; loading a missing player returns `null`,
  matching `MemoryPlayerStore`. Pooling via `NpgsqlDataSource`, explicit timeout on
  every command, and idempotent schema migration on boot mirroring
  `backend/deploy/db/init-gamestate.sql` (a test asserts the two stay in sync)
- `--game-db-url` / `GAME_DB_URL` selects the postgres store; unset keeps the
  in-memory store. The active store is logged at startup and DSN passwords are
  masked in every log line. A configured-but-unreachable database is fatal at boot
  (exit 1) rather than a silent degrade to memory
- `Npgsql` 10.0.3 dependency — used through raw commands and explicitly typed
  parameters only, keeping the NativeAOT publish reflection-free
- Persistence tests run against a real PostgreSQL in an ephemeral
  `postgres:16.4-alpine` container on a random free port, and skip cleanly when
  docker is unavailable. Coverage: save/load roundtrip, upsert overwrite,
  missing-load semantics, delete, repeated migration, unreachable-database
  failure, save-after-database-loss surfacing an error and incrementing
  `gameserver_player_saves_total{status="error"}`, DSN parsing/masking, and an
  end-to-end join -> move -> disconnect -> reload-after-restart flow
- `Shared.GameLogic.Systems.MovementSystem` — server-authoritative, deterministic,
  allocation-free movement model shared with the Unity client for prediction:
  `ResolveDirection` (normalize/clamp/reject a raw input vector), `Integrate`
  (`position += direction * speed * dt`, bounds-clamped), `TryMove`,
  `DeltaTimeForTickRate`, `MaxDisplacementPerTick`, `IsDisplacementLegal`
- `MoveResult` enum (`None` / `Accepted` / `Clamped` / `Rejected` / `Blocked`) —
  validation results are returned by value, never thrown
- `Shared.GameLogic.Components.MapBounds` — axis-aligned play area with per-axis
  clamping (`FromSize`, `Default`, `Contains`, `Clamp`). Default 1000x1000 world
  units centered on the origin; configurable via `--map-width` / `--map-height`
  (`GAMESERVER_MAP_WIDTH` / `GAMESERVER_MAP_HEIGHT`) and `ServerOptions.MapBounds`.
  Positions restored from the player store are clamped on join
- `GameConstants`: `MaxInputMagnitude`, `InputDeadzoneSq`, `MaxDeltaTime`,
  `DisplacementTolerance`, `DefaultMapWidth`, `DefaultMapHeight`
- Movement tests: direction matrix, diagonal-vs-cardinal parity, dt scaling across
  tick rates, bounds clamping at all four edges + corners + edge sliding, validator
  accept/clamp/reject/block matrix, determinism, tick-loop integration with scripted
  input sequences and an input-spam anti-cheat regression test
- OpenTelemetry metrics (Meter `rpg.gameserver`) with a Prometheus scrape
  endpoint + `/healthz` on `--metrics-addr` / `METRICS_ADDR` (default `:9101`,
  empty disables). Instruments: tick duration histogram (66 ms budget buckets),
  processed inputs, players online, entities, snapshots sent, player saves by
  status, events published by type. Windows dev falls back to a localhost
  prefix when the wildcard bind needs an URL ACL. See docs/METRICS.md.

### Changed
- **Wire semantics (protocol format unchanged)**: `move_x`/`move_y` in `MsgInput`
  now carry a movement **direction**, not a per-message displacement. The server
  integrates `direction * speed * dt` (`dt = 1 / tickRate`) once per tick. Vectors
  with magnitude > 1 are normalized, so diagonal movement is no longer faster than
  cardinal; magnitude > 1.5, NaN and infinity are dropped and logged at Debug
- `EntityState.Speed` is now **world units per second** instead of a per-tick
  displacement multiplier; `ServerDefaults.DefaultPlayerSpeed` 1.0 → 5.0 u/s
- `TickLoop` coalesces buffered inputs: only the newest input per player per tick
  performs the movement integration (superseded inputs still resolve their attack).
  Closes the speed hack where movement scaled with client packet rate
- `InputHandler` takes the tick rate and map bounds; `ProcessInput` /
  `ProcessInputLocked` gained an `applyMovement` flag (defaults to `true`)
- `ValidationLogic.ValidateInput` audits the input direction via `MovementSystem`
  (timestep-independent) instead of a per-tick distance cap
- Travel distance is now tick-rate independent — tick rate is a smoothness knob,
  not a balance knob
- docs/DESIGN.md: dated "Movement Model" section (rationale, buffering choice,
  validation table, Unity prediction reuse plan); docs/README.md input semantics,
  flags, and Unity DOTS example updated

### Removed
- `Shared.GameLogic.Systems.MovementLogic` (`ApplyMove` / `ValidateMove`) and
  `GameConstants.MaxMovePerTick` — superseded by `MovementSystem`

### Fixed
- JWT claim field names (`user_id`/`server_id` → `sub`/`sid`) to match Go
  gateway wire format — cross-language token validation now works correctly

## [0.1.0] - 2026-08-04

### Added
- Initial C# port of Go game server
- `Shared.GameLogic` library with pure C# game logic (movement, combat, validation, AOI)
  - Designed for sharing between .NET server and Unity DOTS client
  - Zero Unity dependencies — standard .NET 10 class library
- `GameServer` .NET 10 console application
  - Wire protocol compatible with existing Go gateway (4-byte length prefix + JSON)
  - Server-authoritative tick loop at configurable rate (default 15Hz)
  - Thread-safe GameWorld with reader-writer locking
  - Input validation and anti-cheat (speed hack, range, cooldown)
  - Combat system (damage calculation, death handling)
  - AOI-filtered snapshot broadcasting
  - Async batch persistence (in-memory store, PostgreSQL interface ready)
  - Agones SDK interface (NoopSdk for local dev)
  - Event publisher interface for cross-server events
  - HS256 JWT validation (shared secret with gateway)
  - Entity hold on disconnect (30s map / 60s dungeon reconnect window)
  - Graceful shutdown on SIGINT/SIGTERM
- `GameServer.Tests` — comprehensive xUnit test suite
- NativeAOT publish support for minimal container images
- Docker multi-stage build (`deploy/docker/Dockerfile.gameserver-dotnet`)
- GitHub Actions CI pipeline (`ci-dotnet.yml`)
