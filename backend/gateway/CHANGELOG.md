# Changelog — Gateway Module

All notable changes to the Gateway module will be documented in this file.
Format based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Changed
- **Allocation can no longer split a world: a full map is refused, not given a
  second server.** `registry.FindServer` used to call the allocator whenever *no
  server had spare capacity* — which includes a map whose single live server is
  simply full — and then registered the result under the same `map_id`. That
  breaks the MVP invariant of one live game server per `map_id` (ADR-2): two
  instances are two disconnected copies of the world, players on them cannot see
  or interact with each other, and there is no handoff between them. The branch
  was unreachable while allocation could not produce a live server; enabling
  `ALLOCATOR=agones` makes it reachable.

  Allocation now fires **only when the map has zero live servers**. Live but all
  full returns `ErrNoServerAvailable` with no allocator call, i.e. the existing
  client-visible `no server available for map`. Refusing a join is a loud,
  bounded failure; a silently split world is not — this is deliberately not a
  fallback. The multi-server warning in `FindServer` is unchanged and is now the
  detector for the invariant being broken by any other route.

- **The join token is minted only once the target server is actually dialable.**
  Join tokens are single-use, pinned to one server id and live 30s
  (`constants.JoinTokenTTL`). `transfer.AssignMapKeyring` minted one the moment
  `FindServer` returned — including for a pod Agones had only just allocated,
  which still has to start its NativeAOT container, bind, report `Ready` to the
  sidecar, learn its own address and self-register. If that took longer than 30s
  the client burned its only token on an address that was not answering.

  On the allocation path `FindServer` now polls the registry for the allocated
  `ServerID` and returns the entry **the game server wrote about itself**; the
  token, `ServerAddr` and `Transport` all come from that entry, so the client is
  given the self-reported, dialable address rather than the allocation response's
  guess. The gateway no longer writes that entry on the server's behalf — two
  writers on one datum is forbidden (ADR-1) and a gateway-written entry has
  nothing re-arming its 15s TTL. The already-registered path is unchanged: no
  allocator call, no polling, no added latency (asserted by test).

  `gateway_allocations_total{result="ok"}` now means "allocated **and** the pod
  registered"; a pod that never registers counts as `result="fail"`.

- `--allocator-transport` / `ALLOCATOR_TRANSPORT` is now **inert**. It stamps a
  transport onto the allocation response, and that response is used only for its
  `ServerID`; the transport announced to a client always comes from the pod's own
  registry entry. The flag is retained but is a candidate for removal.

### Added
- `registry.ErrServerStarting` and the client-facing
  `server is starting, retry shortly`: a distinct, **retryable** EnterWorld
  failure for "a server was allocated for this map but has not finished booting".
  A client can now tell it apart from `no server available for map` (full or
  unavailable — do not retry). No token and no address are handed out with it,
  and it leaks no internal detail. Matchable with `errors.Is`.
- `--allocation-wait-timeout` / `ALLOCATION_WAIT_TIMEOUT` (default **20s**) and
  `--allocation-poll-interval` / `ALLOCATION_POLL_INTERVAL` (default **250ms**)
  bound that wait (flag wins, then env, then default; an unparseable or
  non-positive env value is logged and ignored rather than failing start-up).
  20s is a deliberate compromise while pod cold start is unmeasured: longer than
  `retryTotalTimeout`'s 10s because a pod start is far heavier than a Redis blip,
  but below `JoinTokenTTL` (30s) so the wait can never outlast the token minted
  after it. Also exposed programmatically as `registry.WithAllocationWait`.
- **Per-request logging for the client handshake — the gateway was invisible.**
  A live run of the netcode sample completed a full handshake (Nakama device
  auth → `gateway_token` → map assignment → direct join → snapshots streaming)
  and the gateway's entire log for it was **zero lines**; its whole log was 9
  lines since startup. The hop could only be evidenced *indirectly*, from the
  client reporting the address it had been handed. A gateway that misbehaved
  could not be diagnosed from the gateway.

  A session now logs three info lines — `auth ok`, `enter world assigned`,
  `client disconnected` — plus a `conn` correlation number on every line
  (including the debug ones), so `grep '"conn":N'` reconstructs one session.
  `enter world assigned` carries the map, server id, server address and
  transport: the only record anywhere of where a client was sent. Failures that
  were previously silent — a malformed auth frame, a rejected token, a session
  store that would not write, an unassignable map — now say which one they were
  via a `reason` field. Verified end to end against the live stack, not by
  reading the code: a passing smoketest produced exactly the three lines, and a
  gateway started with a mismatched `JWT_SECRET` produced
  `auth failed reason=invalid_token`, which is the diagnosis the log previously
  could not give.

  **No credential is logged**: not the client JWT, not the issued join token,
  not a signing secret. Both tokens are bearer credentials, so a log holding one
  is a log that can be replayed into a session; failures report the verifier's
  error (expired vs bad signature) rather than the token. `TestLogNeverContains
  Credentials` scans a full handshake's log for all three and fails on a
  substring match. User ids *are* logged, matching the game server, which prints
  them on join.

  **The level assignment is a volume decision.** At 200 concurrent clients a
  line on a per-message path is a denial of service against the gateway's own
  disk, so: auth and enter-world are once per session and are info; heartbeats
  are the only frame a connected client repeats (20 frames/s at 200 clients) and
  are not logged at all; a TCP accept is the one event an unauthenticated peer
  can mint at will and stays at debug. Full table in `docs/README.md`.

- **`MsgKick` is now emitted on eviction, alongside `MsgDisconnect`.** Type 15
  had been defined in `wire.proto` with working codecs on both the Go and C#
  sides since it was introduced, but nothing ever sent it. `kickLocalUser` now
  goes through `sendKickAndClose`, which sends `MsgKick{reason}` followed by
  `MsgDisconnect{reason}` — same reason string in both — and half-closes once
  they flush.

  Both frames are sent rather than one because they address different clients.
  `MsgKick` is the typed signal; `MsgDisconnect` is what any client written
  before this change already acts on, and such a client ignores an unknown type
  15. Emitting only `MsgKick` would have stranded them on a socket that stops
  answering. The order is contractual: a client that understands `MsgKick` reads
  the reason there and must treat the following `MsgDisconnect` as the same
  eviction, not a second one.

  `KickReasonDuplicateLogin` is exported so the reason string has one definition
  rather than a literal per call site. `duplicate_login` is the only reason the
  gateway emits today; `wire.proto` names others (`server_shutdown`,
  `session_expired`, `rate_limited`) that remain unwired — shutdown still closes
  without a frame, and the session/rate-limit paths still answer on the next
  frame via `MsgAuthResp`.

  Both frames are JSON regardless of the connection's latched encoding, for the
  reason the previous single frame was: eviction runs on the *evicting*
  connection's goroutine and `ClientConn.enc` may only be read from the evicted
  connection's `ReadLoop`.

### Fixed
- **`unexpected message type` was a per-message warning.** It predates the
  logging work above and fires once per inbound frame, so a client speaking the
  wrong protocol — sending gameplay frames to the gateway, which is exactly the
  ADR-3 mistake — would warn once per frame indefinitely. It, and the new
  `auth failed` line (which a socket can drive by looping bad tokens), are now
  latched: the first occurrence on a connection logs at its natural level and
  the rest drop to debug. The per-connection message limiter bounds these but
  does not make them safe — its default of 60 frames/s still permits 60 log
  lines a second from one socket.

- **`docs/API.md` documented a `JOIN_TOKEN_SECRET` fallback that no longer
  exists, and recommended it.** The join-token section said an unset
  `JOIN_TOKEN_SECRET` falls back to `JWT_SECRET` "because `gameserver-dotnet`
  cannot read the new variable yet". Both halves are obsolete: the C# game server
  reads it (`GameServerHost` parses it into `_joinKeys`) and *requires* it
  (`Program.cs` exits 2 without it), and this gateway refuses to start without it
  too (`cmd/gateway/main.go`). An operator following the old text got a gateway
  that will not boot — or, giving only the gateway a distinct secret, a fleet
  where every join fails signature verification. No code changed; the doc was
  describing a state the code left behind.

### Added
- `docs/API.md`: the wire encoding is now documented — Protobuf or legacy JSON,
  identified from the first body byte (`0x08` vs `{`), latched per connection so
  every reply answers in the encoding the client used. The one deliberate
  exception (duplicate-login kick builds JSON off the victim's read-loop
  goroutine) is called out.
- `docs/API.md`: join tokens are single-use — `SignWithServer` attaches a `jti`
  whenever `serverID` is set, and the game server consumes it once through
  `JtiTracker`. Documented together with the tracker's real scope (in-memory,
  per-process, 60 s), since that is what makes `sid` pinning load-bearing rather
  than redundant.
- `docs/API.md`: `MsgPing` (11) and `MsgPong` (12) added to the handled-message
  table, with the reason they are dispatched *before* `checkSession` — a pong
  must not be rejected because a Redis blip failed the session lookup. The table
  previously implied they fell into "logged and ignored".
- `docs/API.md`: `EnterWorldResponse.Transport` documented in the handshake
  sequence — the client must dial the game server with that transport, and empty
  means `"tcp"` (pre-field registry entries).
- `docs/API.md`: noted that `MsgKick` (15) is defined and has codecs on both
  sides but is emitted by nothing; duplicate-login eviction sends
  `MsgDisconnect{reason:"duplicate_login"}`, which is what a client should watch.

### Added
- **Exponential backoff retry for registry lookups.** `FindServer` and `GetServer`
  now retry transient Redis errors (connection refused, timeout) with backoff
  (1s, 2s, 4s, max 3 retries, 10s total timeout). Business-logic errors
  (`ErrNoServerAvailable`, `ErrNotFound`) are not retried. Context cancellation
  aborts between retries so a disconnected client does not waste attempts
- **Server-down watcher with Pub/Sub notification.** `RegistryWatcher` periodically
  polls known servers (every 5s) and publishes a `ServerDownEvent` to the
  `gateway:server_down` channel when a server's heartbeat expires. Other gateway
  instances can subscribe via `SubscribeServerDown` to clear cached state. Includes
  in-memory `MemoryPubSub` for testing
- **Enriched session model.** Session store value changed from a plain
  `user_id` string to a JSON object:
  `{"gateway_id":"gw-0","server_id":"","map_id":"","created_at":N,"last_activity":N}`.
  `SessionData` struct with `GetSession` and `UpdateSession` methods on
  `SessionManager`. `NewSessionManager` accepts an optional `gatewayID`
  (variadic, backward-compatible); `ValidateSession` handles both the new JSON
  format and the legacy plain-string format for rolling upgrades.
  `GatewayKickChannel` constant added to `shared/constants` for cross-gateway
  coordination
- **Duplicate login detection and kick.** On `MsgAuth`, the gateway checks
  whether a session already exists for the user. If it belongs to this gateway,
  the old connection receives `MsgDisconnect(reason="duplicate_login")` and is
  closed before the new session is created. If it belongs to a different
  gateway, a kick request is published via `KickPublisher` (noop for in-memory
  backend, Redis Pub/Sub for multi-instance). `KickPublisher` /
  `KickSubscriber` interfaces with `WithKickPublisher` / `WithKickSubscriber`
  options; `handleKickEvent` processes incoming kick requests
- **Session-server association tracking.** After a successful `MsgEnterWorld`
  (join token minted), the session in the store is updated with `server_id` and
  `map_id` so the gateway knows where each player is currently playing. Uses
  the new `UpdateSession` method. `AssignResult` gained a `ServerID` field
- **User-to-connection lookup.** `userConns` map on `Gateway` enables O(1)
  connection lookup by `user_id` for local duplicate-login kicks.
  `trackUser`, `untrackUser`, `findUserConn` methods manage the mapping

### Fixed
- **Data race in `kickLocalUser`.** The method called `old.Reply` from the new
  connection's ReadLoop goroutine, which reads the `enc` field that the old
  connection's ReadLoop may still be writing. Switched to
  `messages.NewEnvelope` (JSON, always safe from any goroutine)
- `cmd/gateway` now passes `--instance-id` / hostname as `gatewayID` to
  `NewSessionManager`, so every session record is tagged with the owning
  gateway instance
- **30-second unauthenticated connection timeout.** Connections that do not send
  `MsgAuth` within 30 seconds are closed automatically, preventing connection-slot
  exhaustion from idle or malicious clients.

### Changed
- **`JOIN_TOKEN_SECRET` is now mandatory (fatal if unset).** The gateway exits
  with a fatal error when the env var is empty or unset. The fallback to
  `JWT_SECRET` has been removed.
- **`GenerateJoinTokenKeyring` rejects empty `serverID`.** An empty server ID in
  the join token would bypass the game server's `sid` check.
- **Map transfer documentation in `transfer/dungeon.go`.** Documents the
  client-driven map transfer flow (MsgTransferMap types 13/14) and confirms
  no gateway code change is required — the existing MsgEnterWorld flow handles
  re-entry to a new map server.

### Added
- **Heartbeat loop (MsgPing/MsgPong).** Each client connection sends MsgPing
  every 10 s after TCP accept. If no MsgPong is received within 30 s the
  connection is closed. Incoming MsgPing from a client is answered with a
  MsgPong echoing the sender's timestamp plus the server's wall clock.
  Heartbeat messages bypass session validation — a pong must refresh the
  timer even during a transient Redis outage.

- **The gateway answers in the encoding the client spoke.** `ClientConn` latches
  the encoding of the first frame it decodes (`shared/messages` sniffs JSON vs
  Protobuf from the first body byte) and every response is built through the new
  `ClientConn.Reply` instead of `messages.NewEnvelope`, so a new response type
  cannot accidentally be pinned to JSON. The gateway never picks an encoding of
  its own — which is what lets the gateway, the game servers and the client be
  upgraded in any order, and lets a Protobuf-capable gateway keep serving JSON
  clients through a rollout. The zero value is `EncodingJSON`, so anything that
  somehow replies before reading stays on the legacy encoding.

### Added
- `/readyz` readiness endpoint and `metrics.Readiness`, a concurrency-safe set
  of named dependency checks. `/healthz` stays liveness-only and returns 200
  even when Redis is down; `/readyz` returns 503 with the failing check names
  (names only — never the error text, which carries internal addresses).
  **The split is deliberate:** Kubernetes restarts a container that fails
  liveness but only removes it from service on a readiness failure. Wiring Redis
  into `/healthz` would restart every gateway pod at once during a Redis outage,
  killing player connections that do not depend on Redis (ADR-3) and hitting a
  recovering Redis with a reconnect storm. A restart cannot heal a sick
  dependency (DR audit **G9**)
- Metrics: `gateway_redis_up`, `gateway_relay_up` (gauges),
  `gateway_session_checks_total{result="ok"|"expired"|"store_error"}` and
  `gateway_stream_group_loss_total`. All zero-primed. The session-check split is
  what makes a Redis blip visible — previously it was indistinguishable from
  normal expiry on a dashboard
- `registry.ErrNoServerAvailable` — a matchable sentinel for the "map is full or
  absent" capacity condition, wrapped by `FindServer`

### Changed
- `ClientConn.UserID` / `ClientConn.State` are no longer exported plain fields.
  Identity now lives behind a `sync.RWMutex` with accessors — `UserID()`,
  `State()`, `Identity()`, `SetAuthenticated()`, `SetInWorld()` and
  `ClearIdentity()`. `Identity()` returns both halves under one lock so a caller
  branching on user *and* state cannot observe a half-applied transition;
  `ClearIdentity()` returns the user it took, so the check ("is there a
  session?") and the act ("destroy it") happen atomically and two teardown paths
  cannot both claim the same session

### Fixed
- **Data race on `ClientConn` identity.** `cleanupSession` wrote `UserID`/`State`
  from the read-loop goroutine (`handleConn`'s defer) while `CloseGracefully`
  and `writeLoop` read `UserID` from the write-loop goroutine for their log
  lines — an unsynchronised read/write on the same words, reported by the
  `-race` build in CI. The struct comment explaining that `msgBucket` needs no
  lock *because* it is ReadLoop-only was correct for `msgBucket` and was being
  quietly assumed for the identity fields, which genuinely cross goroutines.
  Race detection is probabilistic, so this had already reached `develop` green.
  Audit of every other `ClientConn` field found no second instance: `msgBucket`
  and `limited` really are ReadLoop-only, `closeAfterFlush`/`halfClosed` are
  already `atomic.Bool`, and `conn`/`sendCh`/`done`/`once`/`logger` are
  immutable after construction or internally synchronised
- **A Redis blip de-authenticated every online player.** `checkSession` treated
  any `ValidateSession` error as an expired session, so a store outage dropped
  live connections to `StateConnected` and told correctly-authenticated players
  "session expired" — a forced re-login for the whole population, caused by a
  dependency gameplay does not use. Infrastructure errors are now distinguished
  from `storage.ErrNotFound` and **fail open**: the connection stays
  authenticated (it already proved possession of a valid JWT at `MsgAuth`) and
  the error is logged and counted. A session the store affirmatively reports as
  gone is still rejected (DR audit **G6**)
- **The gateway crash-looped when Redis was down at boot.** `relay.Start`
  failing propagated out of `Gateway.Run`, `main` exited 1 and the pod
  crash-looped, so a Redis outage took auth and map assignment down with it. The
  relay is now started in the background with exponential backoff (1s → 30s):
  the gateway serves traffic immediately and the relay attaches when Redis
  returns, no restart required. `Gateway.RelayUp()` and `gateway_relay_up`
  expose the degraded state (DR audit **G3**)
- **Internal errors were sent to clients verbatim.** `handleEnterWorld` passed
  `err.Error()` straight into the `EnterWorldResponse`, leaking internal
  hostnames, private IPs and ports (e.g.
  `dial tcp 10.0.1.7:6379: connect: connection refused`) to any unauthenticated
  peer that could reach the port. Errors are now mapped to a fixed set of
  client-safe messages and the detail is logged server-side. Anything not
  explicitly classified collapses to `internal error`, so a new error type
  cannot start leaking by default (DR audit **G7**)
- `events.Relay.Start` latched `started = true` before `Subscribe` succeeded, so
  a failed start was permanent — the retry above would have hit the
  already-started guard forever. It now latches only on success

### Added
- Rate limiting. `server.WithConnRateLimit` bounds accepts per source IP
  (default 10/min, burst 10) and `server.WithMsgRateLimit` bounds inbound frames
  per connection (default 60/s, burst 120). The per-IP check runs immediately
  after `Accept`, before any goroutine or session exists; the per-frame check is
  a struct field on `ClientConn`, not a map lookup, so the hot path stays
  allocation-free. A connection that trips the message limit gets one
  `rate limited` error frame and is then closed
- `gateway_rate_limited_total{reason="connection"|"message"}` counter,
  zero-primed at start-up
- `server.WithJoinTokenSecret` — join tokens are signed with `JOIN_TOKEN_SECRET`
  instead of `JWT_SECRET`, so a compromised game-server pod cannot forge client
  auth tokens. Unset falls back to `JWT_SECRET` (unchanged behaviour) with a
  start-up warning
- `server.WithTransportKey` — passes `TRANSPORT_KEY` to the KCP listener for
  AES-256 wire encryption
- Secret rotation: `JWT_SECRET` and `JOIN_TOKEN_SECRET` accept
  `"current,previous"`. `session.VerifyClientJWTKeyring`,
  `transfer.GenerateJoinTokenKeyring`, `transfer.ValidateJoinTokenKeyring`,
  `transfer.AssignMapKeyring`
- `ClientConn.SendAndClose` / `RemoteIP`
- Flags: `--transport-key`, `--join-token-secret`, `--conn-rate-per-min`,
  `--msg-rate-per-sec`. Start-up warns when `JOIN_TOKEN_SECRET` is unset or
  `JWT_SECRET` is still the built-in dev default

### Fixed
- Disconnecting a client with an explanatory frame no longer loses that frame to
  a TCP reset. A hard `Close()` on a socket with unread inbound data (exactly
  what a flooding client leaves behind) makes the kernel emit RST instead of
  FIN, and RST discards the unsent send buffer — so a rate-limited player could
  get a bare disconnect with no reason. New `ClientConn.CloseGracefully`
  half-closes with `CloseWrite`, bounds the drain with a 2s read deadline, and
  lets `ReadLoop` (the socket's only reader) consume the backlog so the final
  `Close` sees an empty receive queue. Used by the rate-limit path and
  `handleDisconnect`; KCP falls back to a plain `Close` (no half-close, and no
  RST semantics to worry about). Surfaced as an intermittent `TestMsgRateLimit`
  failure under a loaded `go test ./...`; the test now also asserts the stream
  ends with EOF rather than `ECONNRESET`, which fails deterministically against
  the old behaviour

### Changed
- `NewClientConn` takes a `ratelimit.Bucket` (pass the zero value for no limit)
- `WriteLoop` half-closes instead of hard-closing when a deferred close was
  requested and fully flushed; abrupt exits (encode error, dead socket,
  Shutdown) still hard-close
- Keyrings are parsed once in `New`, not per request

### Known gaps
- **KCP is not reachable end to end.** `gameserver-dotnet` has no KCP
  implementation, so `--transport=kcp` and `TRANSPORT_KEY` protect the
  client→gateway hop only (ADR-8)
- **`JOIN_TOKEN_SECRET` needs a C# counterpart before it can be enabled.** The
  game server reads only `JWT_SECRET` (`GameServer/Program.cs:24`) and verifies
  join tokens at `GameServer/Server/GameServer.cs:217`
  (`JwtValidator.Verify(joinReq.Token, _options.JwtSecret)`). Until that reads
  `JOIN_TOKEN_SECRET` (falling back to `JWT_SECRET`), setting the split on the
  gateway alone breaks every join
- Both rate limiters are per process — N replicas admit N x the limit

### Added
- `registry.WithLogger` option. `FindServer` now logs a warning when a `map_id`
  resolves to more than one live game server — the MVP invariant is one server per
  map, and two instances are two disconnected copies of the world
  (`backend/docs/ARCHITECTURE-DECISIONS.md`, ADR-2)

### Changed
- `RegistryService.FindServer` breaks `PlayerCount` ties on `ServerID` so server
  selection is deterministic. Previously ties resolved in Go map / Redis `SMEMBERS`
  order, so two equally-loaded servers could split a party across instances
- Docs corrected to describe the gateway as a **redirector, not a router/proxy**:
  it never forwards gameplay traffic, and no `router.go` exists (ADR-3). Affects
  `CLAUDE.md` and `docs/README.md`. Performance targets marked as unbenchmarked
  estimates, and the meaningless "packet forwarding latency" target replaced with
  auth latency (ADR-7)

### Added
- Prometheus instrumentation (`metrics/`): `gateway_connections_active` (gauge),
  `gateway_auth_total`, `gateway_enter_world_total`, `gateway_allocations_total`
  (counters labelled `result=ok|fail`) and `gateway_relay_events_total`, plus the
  standard `go_*`/`process_*` collectors.
- Separate metrics listener serving `/metrics` (promhttp) and `/healthz` (200
  liveness): `--metrics-addr` / `METRICS_ADDR`, default `:9102`;
  `off`/`none`/empty disables it. Never shares the realtime port.
- `server.WithMetrics` and `registry.WithMetrics` options; both are optional and
  every recorder is nil-safe, so an uninstrumented gateway behaves as before.

### Changed
- Game servers migrated from Go to C# .NET 10 (`backend/gameserver-dotnet/`).
  Gateway wire protocol unchanged (4-byte BE length prefix + JSON, `snake_case`
  fields) — no gateway code changes required for compatibility.

### Added
- Opt-in KCP/UDP listener for the realtime path: `--transport=tcp|kcp`
  (`GATEWAY_TRANSPORT`, default `tcp`) and the `server.WithTransport` option.
  `Gateway.Run` listens through `shared/transport`; handlers are unchanged.
- `EnterWorldResponse.Transport` is now filled from the target game server's
  registry entry, so the client knows which transport to dial for hop 2.
  `transfer.AssignResult` gained the matching `Transport` field. Empty means
  `tcp` — servers registered before the field existed keep working.
- `--allocator-transport` / `ALLOCATOR_TRANSPORT` (defaults to `--transport`):
  the transport stamped onto a ServerInfo synthesized by the Agones allocator,
  since the allocation API reports no transport and the pod has not registered
  itself yet. Must match the fleet manifest's `--transport` argument.
- Real Agones allocator (`registry.AgonesAllocator`): `POST` to the aggregated
  `allocation.agones.dev/v1` `GameServerAllocation` endpoint using `client-go`
  credential resolution (in-cluster ServiceAccount, else `--allocator-kubeconfig`
  / `$KUBECONFIG` / `~/.kube/config`). Fleet selection by
  `agones.dev/fleet` label, `KindMap`/`KindDungeon` mapped from config.
  Wire types are modelled locally instead of importing the Agones clientset —
  see `docs/DESIGN.md` for the rationale.
- `registry.ErrNoCapacity` sentinel for `state: UnAllocated`, plus
  `registry.KindAllocator` / `AllocationRequest` for kind-aware allocation.
- `cmd/gateway` flags `--allocator` (`none`|`agones`), `--allocator-namespace`,
  `--allocator-fleet-map`, `--allocator-fleet-dungeon`, `--allocator-kubeconfig`
  with matching `ALLOCATOR*` env vars. Default stays `none`, so nothing changes
  unless allocation is explicitly enabled.
- Tests: table-driven allocator client tests against an `httptest` fake API server
  (success, `UnAllocated`, API error with `Status.message`, malformed body, missing
  port, timeout, request shape/URL per kind) and a gateway-level enter-world test
  asserting allocation only fires for unserved maps and that the join token's
  `sid` is the allocated GameServer name.

### Changed
- `cmd/gateway` wires the registry with the Agones allocator when
  `--allocator=agones`; a bad Kubernetes config is fatal at boot.

### Fixed
- Cross-server event stream name mismatch: gameserver published to
  "events:game" (double-prefixed to "events:events:game" by the store) while
  the gateway relay subscribed to "global" — events never arrived. Both sides
  now share constants.GameEventStream ("game", store adds the prefix once).

### Added
- Selectable store backends in `cmd/gateway`: `memory` (default) or `redis`
  (`redisstore.SessionStore` / `ServerRegistry` / `EventStream` sharing one client).
  Resolved from `--backend`, `GATEWAY_BACKEND`, or an exported `REDIS_ADDR`
- `--addr` and `--instance-id` flags for `cmd/gateway` (`--instance-id` names the
  event-stream consumer within the `gateway` consumer group)
- Session validation + sliding TTL refresh on every non-auth frame (`checkSession`)
- `MsgDisconnect` handling and session cleanup on socket close
- `session.SessionKey` and `SessionManager.RefreshSession`
- Real `events.Relay` over any `storage.EventStream`, plus `events.Sink`/`SinkFunc`;
  wired into the gateway via `server.WithEventRelay` (started in `Run`, stopped in
  `Shutdown`). Sink logs/counts events — no client fan-out until `shared/messages`
  gains a client-facing event type
- `Gateway.OnEvent`, `Gateway.EventCount`, `Gateway.ConnCount`
- `registry.NewRegistryServiceWithAllocator` and `RegistryService.GetServer`
- Tests: session lifecycle, relay, and registry lookup covered against both the memory
  and Redis (miniredis) backends
- `docs/API.md` and `docs/DESIGN.md`

### Changed
- `RegistryService.FindServer` picks the least-loaded server with spare capacity instead
  of the first match; falls back to the allocator when one is configured
- `server.New` takes variadic `Option`s (existing 4-arg calls unchanged)
- `StubEventRelay.Start` is now a no-op instead of returning `ErrNotImplemented`, so a
  gateway configured without a stream still starts
- `docs/README.md`: run modes, flags, backend selection and env table
- Bump Go version to 1.26 (align with CI and gameserver)

### Fixed
- Sessions were write-only: never read back, never refreshed, never destroyed — they
  leaked until TTL and left ghost-online players in a shared store

### Added (initial)
- Initial module setup with go.mod (`github.com/duycuong/rpg-mmo/gateway`)
- CLAUDE.md agent instructions for Gateway Engineer role
