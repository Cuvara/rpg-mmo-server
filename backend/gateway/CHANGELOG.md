# Changelog — Gateway Module

All notable changes to the Gateway module will be documented in this file.
Format based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

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

### Added
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
