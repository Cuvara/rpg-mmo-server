# Changelog — Gateway Module

All notable changes to the Gateway module will be documented in this file.
Format based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Fixed
- **Heartbeats now keep the gateway session alive**
  ([#231](https://github.com/Cuvara/rpg-mmo-server/issues/231)). A `MsgPong` on
  an authenticated connection re-arms the session TTL
  (`refreshSessionOnPong`), so a client that holds the gateway socket open
  sending only heartbeats — the recommended shape per
  `gameserver-dotnet/docs/API.md` — no longer has its session expire under the
  live connection after `SessionTTL` (1 h) on one map, which made the next map
  transfer fail with `session expired` and forced exactly the re-auth the kept
  connection exists to avoid. Store writes are bounded to one per
  `sessionRefreshInterval` (1 min) per connection, so a 10 s heartbeat does not
  EXPIRE-spam Redis; an unauthenticated pong refreshes nothing; and store
  errors fail open (refresh skipped, connection untouched), matching
  `checkSession`. Table-driven regression:
  `TestGateway_PongRefreshesSessionTTL`, `TestShouldRefreshSession_RateLimited`.

### Removed
- **Cross-gateway duplicate-login kick, which was declared at every layer and
  constructed at none, is gone** ([#211](https://github.com/Cuvara/rpg-mmo-server/issues/211)).
  `KickPublisher` and `KickSubscriber`, `noopKickPublisher`, the `kickPub` and
  `kickSub` fields, `WithKickPublisher` and `WithKickSubscriber`,
  `handleKickEvent`, its `SubscribeKick` call in `Run` and its `Close` call in
  `Shutdown`, and the `PublishKick` branch in `handleAuth`. The matching
  `GatewayKickChannel` constant goes with it; that half is in
  `backend/shared/CHANGELOG.md`.

  **Nothing was broken and nothing is fixed — what is fixed is a false claim.**
  `cmd/gateway/main.go` has never called either option, so `kickPub` was the noop
  from the first line of `New` to the last line of the process and `kickSub` was
  nil, which is why `Run` never subscribed and `Shutdown` never closed anything.
  The feature has therefore been a no-op for its entire life. It read as
  finished, though, at every layer a reader would check: two interfaces, a noop
  implementation, two functional options, a handler, a channel constant with a
  paragraph of rationale, and a line in `CURRENT-SERVER-FLOW-AUDIT.md`
  describing the behaviour as real. That is the defect — a reader planning a
  multi-replica gateway would have concluded the problem was already solved.

  **Deleted rather than implemented, and the reasoning is not "it was easier".**
  Two reasons, and the second is the stronger one. First, ADR-17 pins the
  deployment to one gateway replica, and `deploy/k8s/app/40-gateway.yaml` pins it
  again for an independent reason — single-flight per `map_id` is per gateway
  process (ADR-16), so two replicas racing on a cold map each allocate a
  GameServer that Agones cannot un-allocate. With one replica there is no second
  instance to publish to, so an implementation could be neither observed nor
  tested end to end; it would be speculative work validated by nothing, which is
  how the machinery being deleted came to exist. Second, and decisively: the
  deleted code describes the **wrong transport**. The constant's own comment
  argued for Redis Pub/Sub on the grounds that message loss is acceptable, and
  ADR-5 — "Streams, not pub/sub" — decides against that model for cross-process
  coordination. So this was not a head start on the eventual feature. It was a
  shape that would have had to be thrown away, sitting in the tree looking like
  progress.

  **What replaces it is a written-down gap, not silence.** `handleAuth` keeps its
  `duplicate login detected` log with `old_gateway` and `new_gateway` — the field
  pair that makes the gap observable the moment a second replica exists — and
  carries a comment stating that a session owned by another gateway is
  deliberately left alone, why that is safe today, and that the fix is Streams
  with consumer-group ACK per ADR-5. `docs/CURRENT-SERVER-FLOW-AUDIT.md` §2.2
  gains a "What a second gateway replica would need" note describing the failure
  precisely (a user authenticating against replica B keeps a live session on
  replica A, indefinitely, with no error on either side), and ADR-17 now lists
  this alongside the hostPort and single-flight questions that must be answered
  before any second replica — because a consequence of the single-replica
  decision belongs next to the decision.

- **Local duplicate-login kick is untouched, deliberately and completely.**
  `userConns`, `findUserConn`, `trackUser`, `kickLocalUser` and
  `sendKickAndClose` are unchanged, as is the `MsgKick`-then-`MsgDisconnect`
  ordering contract. It is the half that has always worked and is reached on
  every login. Its four tests in `duplicate_login_test.go` are unchanged and
  still pass; **no test was deleted by this change**, because no test ever
  exercised the removed options — which is itself the point #211 makes about
  this class of defect. A unit test constructs the thing it tests, so it proves
  the component works and says nothing about whether production constructs it.

### Fixed
- **A map served by two game servers is no longer load-balanced across the
  split** (#203). ADR-2 allows exactly one live server per `map_id`, and
  `FindServer` already logged loudly when the registry returned more than one —
  then fell straight through into least-loaded selection across all of them.
  That is the worst available response to the fault. Least-loaded steers each new
  joiner into whichever half is emptier, so the two copies of the world converge
  on **equal** population: both stay occupied, neither ever drains, and the
  accidental split becomes a permanent one in which players standing in the same
  coordinates cannot see or fight each other. The gateway was, in effect,
  treating a violated invariant as a capacity pool to exploit.

  Selection with more than one server for a map is now **deterministic and
  load-blind**: the lowest `ServerID` among those with spare capacity, with
  `PlayerCount` not consulted at all. Every caller — every gateway instance,
  every client retry — therefore lands on the same half, so the other half
  drains as its players log out and the split converges out rather than widening.
  This does not repair a split (nothing migrates the players already on the
  losing half) and it is not meant to; it stops the gateway from feeding one
  while an operator reacts to the warning that is still emitted. Selection when
  exactly one server serves the map is unchanged, as are the wrong-map refusal
  (`ErrFleetMapMismatch`), the all-servers-full refusal (`ErrNoServerAvailable`,
  never an allocation) and the allocation path.

  Two harder responses were considered and **rejected for now**: refusing the
  lookup outright, and rejecting the second registration. Refusing at
  registration is the only one that actually enforces the invariant, but it
  breaks rolling replacement — a new pod self-registers before the old one's TTL
  expires — while the health watcher is still unwired (#204), so it would trade a
  split world for an unservable map. Deterministic selection is the containment
  step that costs nothing and blocks nothing.

  Consequence to know: which half a joiner reaches now depends on server id
  ordering rather than load, so on a fleet scaled past `replicas: 1` (ADR-18) the
  clients pile onto one pod and the spare looks idle. That is the intended
  reading — the idle pod is the copy of the world that should not exist, not
  spare capacity. Covered by new table-driven tests over both the memory and
  Redis registries: the emptier-but-higher-id server losing, a three-way split,
  the lowest id being full so the next-lowest wins, an all-full split still
  refused, and a single server unchanged, plus an order-independence test that
  rotates the store's return order across calls (a Redis set has no ordering
  guarantee, and two gateways disagreeing on the pick would reintroduce the
  split). `TestRegistryService_FindServer_PrefersLeastLoaded` was removed: it
  asserted precisely the behaviour this change inverts.

### Changed
- **A momentarily exhausted fleet no longer gets the terminal "do not retry"
  answer** (#152). `EnterWorld` collapsed two unlike conditions into one message:
  *this map has live servers and all are full* (`registry.go:408`, stable, ADR-2
  forbids growing out of it) and *this map has no live server and the allocation
  API answered `UnAllocated`* (`registry.go:559`), which routinely clears in
  seconds as the Fleet controller brings a replacement pod to `Ready` — 5.38s
  measured on k3d (ADR-18). The second now answers with a new retryable message,
  `all servers busy, retry shortly`, distinct from both
  `no server available for map` (this map is full — terminal) and
  `server is starting, retry shortly` (a server *was* allocated and is booting).
  No registry change was needed: `allocateAndWait` already wraps with a two-verb
  `%w`, so `registry.ErrNoCapacity` was matchable at the gateway all along — only
  a new `case`, ordered **before** `ErrNoServerAvailable` so the broader sentinel
  cannot swallow the narrower one.
- **The retryable branch is narrowed to `ErrNoCapacity` alone, and that is what
  keeps it off the pod leak.** The terminal answer existed because every retry
  allocates and Agones has no un-allocate; that reason does not hold here, because
  `UnAllocated` is a decoded 2xx body stating no GameServer was handed out — a
  retry costs one allocation POST and **no pod**, and the retry that succeeds
  usually costs not even that, since pods self-register at startup before any
  allocation (ADR-18) and the next `EnterWorld` then resolves on the registry
  path. Every *other* allocator failure (transport error, non-2xx status,
  undecodable body) may have allocated a pod whose response was lost, so those
  keep the terminal message. A gateway-side wait for fleet capacity was
  considered and **rejected**: it would turn a millisecond refusal into a
  multi-second stall on the join path for a condition that may never clear, and
  the bound belongs to the client's backoff. Named, not fixed: nothing caps how
  often a client may retry, so N clients retrying sequentially drive up to N
  allocation POSTs per round against the Agones API — API load, not a leak.
  Tests cover the new classification, the narrowing, the two-verb `%w`
  matchability, and that all six client-facing `EnterWorld` messages are
  pairwise distinct.

### Documentation
- **Audited the gateway's clock discipline for #153; it derives no rates, and one true
  wall-clock interval was found and left unfixed.** The gateway contributes no figure to
  `backend/docs/BENCHMARK.md` (nothing there is measured through it), but it was checked rather
  than assumed. Every `rate` in the module is a rate *limiter* — a configured policy, not a
  measurement — and `shared/ratelimit` refills from `now.Sub(b.last)` with both endpoints from
  `time.Now()`, so it is monotonic and correct. Session `CreatedAt`/`LastActivity` are
  wall-clock stamps rather than intervals, and expiry is enforced by Redis' own TTL, not by
  arithmetic in Go. **The exception is the heartbeat**: `server/connection.go` tests
  `time.Since(time.UnixMilli(last)) > pongTimeout`, and `time.UnixMilli` returns a `time.Time`
  carrying **no** monotonic reading (verified — a monotonic-bearing `time.Time` renders a
  trailing `m=+…` and the rebuilt one does not), so `time.Since` degrades to wall-clock
  subtraction. Consequence: `MaxHandlerBlockingWait = pongTimeout - pingInterval` asserts a 20s
  margin and the gateway refuses to start with `--allocation-wait-timeout` above it, but the
  allocation wait is a monotonic context deadline while the pong timeout is wall-clock — the two
  sides of that margin run on different clocks. On this host a nominal 30s pong budget elapses
  in ~25.7s real, making the enforced margin ~17.1s rather than 20s. The 15s default still fits,
  so nothing is broken today. Not specific to this box either: a wall clock can be stepped by
  NTP on any host, which is the standard reason timeouts come from a monotonic source. **Left
  unfixed deliberately** — runtime behaviour, not a document figure, and a heartbeat timeout
  change wants its own commit and tests. Refs #153.

### Fixed
- **The registry watcher had no caller, so a dead game server stayed in the
  gateway's view for up to a full heartbeat TTL** (#204). `registry.RegistryWatcher`
  polls the servers the gateway knows about, notices one that has vanished from
  the registry and publishes `server_down` — and it had four passing unit tests
  and **no construction site outside them**. `NewRegistryWatcher` was never called
  by `cmd/gateway`, so `Start` never ran in a real gateway and the tests could not
  fail: they build their own watcher. The consequence was that server death was
  observable only when `constants.ServerHeartbeatTTL` (15s) expired, and for that
  whole window `FindServer` kept handing clients the address of a server that
  would not answer — the state that produces the split-map fault fixed in #203.
  Agones health checks do not cover this: they watch **pod liveness**, while the
  thing that misroutes a client is the gateway's **registry view**, and nothing
  was shortening the gap between the two. The watcher is now constructed,
  attached to the `RegistryService` (`registry.WithWatcher`) and started on the
  process context in `cmd/gateway.wireRegistry`, with `watcher.Stop()` on the
  existing SIGINT/SIGTERM path before the stores are closed. Poll interval **5s**
  against the **15s** TTL — a **3x** margin, and the ratio is the point: a poll at
  or above the TTL would always lose the race to expiry and the watcher would be a
  no-op that still costs a registry read per server per tick.
  `TestWatchPollInterval_ShorterThanHeartbeatTTL` pins that relationship so a
  future retune of either constant cannot silently invert it.
- **The watcher's tracked set now comes from lookups, not only from registration,
  or it would have been empty in every real deployment.** The obvious hooks —
  `RegistryService.RegisterServer` / `DeregisterServer` — are wired (track on
  register, untrack on a graceful deregister, so a clean shutdown is never
  reported as a fault), but in production the gateway **never registers a
  server**: game servers self-register straight into Redis (ADR-2) and those two
  methods have no non-test callers. A watcher fed only by them would have polled
  an empty set forever — wiring that looks correct and detects nothing. So
  `FindServer` tracks the server it returns: the moment the gateway learns a
  server exists is the moment it hands its address to a client, which is also the
  only server whose death the gateway has a reason to care about. `server_down` is
  published through the gateway's existing event stream rather than a second
  pub/sub client (Redis Streams on the Redis backend, in-memory otherwise), so no
  new dependency enters the binary; it is consumed by the relay and logged like
  every other event, since `shared` still has no client-facing `MsgEvent`.
- **`cmd/gateway.wireRegistry` is now the single construction site for the
  `RegistryService`, so this cannot rot back.** The failure this issue is made of
  is wiring that can disappear without any test noticing, so the fix comes with a
  test at the wiring level rather than more coverage of the watcher's logic:
  `TestWireRegistry_StartsWatcher` (fails, on a 2s deadline, if `Start` is not
  called — `Stop` blocks until the poll loop exits, and there is no poll loop to
  exit), `TestWireRegistry_TracksRegisteredServer` and
  `TestWireRegistry_TracksServerHandedToClient` (both fail if the watcher is not
  constructed or not attached). All three were verified against a mutated
  `wireRegistry` with the construction removed, and again with only `Start`
  removed. Routing every construction through one function means deleting the
  call from `main` breaks the build instead of silently disarming the watcher.
- **A client was handed a game server for a map it did not ask for, and every
  layer reported success.** Reproduced on a live k3d Agones fleet: with
  `ALLOCATOR=agones` and a fleet serving `map_01`, `MsgEnterWorld{map_id:
  "map_77"}` allocated a pod, minted a valid join token for it, and answered with
  its address; the client joined, played and the smoke test passed. The cause is a
  chain of individually reasonable steps — allocation targets a **Fleet**, never a
  `map_id`; the wait that follows polls the registry by **`ServerID`**; and a pod
  self-registers under its fleet's own `GAMESERVER_MAP_ID` at boot — so the poll
  found the pod's *pre-existing* `map_01` entry on its first read and returned a
  healthy server for the wrong world. Nothing compared the two maps. `FindServer`
  now does, on the allocation path *and* on the registry path, and refuses with a
  new `ErrFleetMapMismatch`. Deliberately **not** a flavour of
  `ErrNoServerAvailable`: that one says "the map is full, grow the fleet", this one
  says "the fleet you configured hosts a different map, fix `GAMESERVER_MAP_ID`" —
  opposite operator responses, so collapsing them sends the operator to the wrong
  knob. The registry-path check is a store-integrity check rather than a fleet one
  (`FindByMapID` is keyed by `map_id`, so an entry for another map means the index
  is lying) and it refuses rather than filtering: a silent filter degrades into
  "no server for this map" and, with an allocator configured, into an allocation
  the map does not need.
- **The same bug leaked GameServers without bound.** Because the pod registers
  under `map_01`, `map_77` is still unregistered afterwards, so the *next* request
  allocated another pod — three watched going `Allocated` for one retry loop, none
  ever reclaimed (Agones has no un-allocate, this codebase has no `Deallocate`).
  The existing single-flight does not help: it merges callers that overlap in
  time, and a client retrying is sequential. A guard that only rejects *after*
  allocating still burns a pod per attempt, so a proven mismatch is now remembered
  per `map_id` for `--allocation-mismatch-ttl` / `ALLOCATION_MISMATCH_TTL`
  (default **60s**, negative disables and is logged loudly), consulted **before**
  the allocation API is called. Bound: **one GameServer per `map_id` per TTL**,
  asserted by a test. This is a deliberate exception to allocation's "never cache
  a failure" rule, and the difference is the failure's nature: a transient failure
  (Redis blip, momentarily exhausted fleet) fixes itself in seconds, so caching it
  poisons a map that was about to work; a fleet's map is fixed for the life of its
  pods, so no retry can turn the answer into "yes" while each retry costs a pod
  permanently. The TTL keeps the cache from outliving its truth — a fleet
  redeployed with the right map is usable again within a minute, no restart.
- **`MsgEnterWorld` no longer invites the retry that drives the leak.** A map the
  deployment cannot serve now answers `map is not available` — terminal, and
  distinct from both `server is starting, retry shortly` (retryable) and
  `no server available for map` (the map exists but is full). It names no fleet,
  namespace or server id.

  **Not fixed, and out of this module's reach:** the gateway still cannot make a
  fleet serve an arbitrary map. The real answer is patching the allocated pod's
  `GAMESERVER_MAP_ID` through `GameServerAllocation` metadata, which requires the
  C# game server to read its map from the pod's annotations instead of the env var
  baked into the fleet spec (`backend/gameserver-dotnet/` + `backend/deploy/`).
  The gateway also has no map catalogue, so "a map that does not exist in the
  game" and "a map whose fleet is not deployed yet" are indistinguishable here.
- **The Agones allocator's default map fleet named a fleet that no longer
  exists.** `DefaultFleetMap` was `map-servers-dev` — the retired **Go** fleet,
  whose game server was deleted in `670a803` and whose manifests are gone from
  the repo. The replacement is `map-servers-dotnet-dev`, and it is now the
  default. The old value failed in the worst possible way: `NewAgonesAllocator`
  only builds a REST client and never validates the fleet name, so a gateway with
  `ALLOCATOR=agones` started perfectly and broke at the **first allocation** —
  the one code path that matters, at the one moment it matters. A test now pins
  the constant to the deployed fleet name so the next rename fails in CI instead
  of in the cluster.
- **`DefaultFleetDungeon` removed rather than repointed.** No dungeon fleet
  exists (ADR-14 stage 6 unstarted), so any default would be the same trap one
  generation later. A kind with no configured fleet is no longer registered at
  all, and `Allocate(KindDungeon)` fails immediately with the new
  `ErrKindNotConfigured`, naming `--allocator-fleet-dungeon` /
  `ALLOCATOR_FLEET_DUNGEON` — a configuration error stated as one, instead of a
  Kubernetes 404 for a fleet that was never going to be there.

  **Not done, deliberately: validating the fleet at construction.** It would make
  gateway start-up depend on cluster reachability — and the gateway is the
  redirector that must come up *during* a cluster outage — and reading a Fleet
  needs `get fleets` RBAC the gateway does not have and should not be granted (it
  holds exactly `create gameserverallocations`), so it would emit a 403
  indistinguishable from a missing fleet. The constant-vs-deployed-name test is
  the compensating control.

### Changed
- **Allocation is now single-flight per `map_id`.** Concurrent `MsgEnterWorld`
  calls for one unserved map produced one allocation **each**. Because only one
  of those pods can ever win the `map_id` registration (ADR-2) and nothing
  deallocates the rest — Agones does not reclaim an `Allocated` GameServer and
  this codebase has no `Deallocate` path — the losers leak. The retry this
  gateway explicitly asks clients to perform (`server is starting, retry
  shortly`) turned that into a feedback loop: retry → map still unserved →
  another pod → another retry. On a `replicas: 1` fleet one reconnecting player
  could exhaust the fleet.

  The first caller allocates and waits; callers arriving while that runs share
  its outcome. A failed allocation is **not** cached, so one transient failure
  cannot poison a map until restart. The leader's work is detached from its own
  caller's context, so a leader that hangs up does not abort the allocation its
  followers are waiting on. Different `map_id`s never block each other, and the
  existing-server path never touches the lock. Implemented as a ~40-line
  map-of-channels rather than adding `golang.org/x/sync/singleflight`: the module
  has no `golang.org/x/sync` dependency today, and the package's extra surface is
  unused here.

- **The gateway refuses to start with an allocation wait that would starve the
  client heartbeat**, and the default wait came down **20s → 15s**. A connection
  is served by one goroutine: the read loop does not read the next frame —
  including `MsgPong` — until the current handler returns. `handleEnterWorld` is
  now the one handler that blocks for a configurable time, so a large
  `--allocation-wait-timeout` stops pongs from being recorded and `HeartbeatLoop`
  closes the connection after `pongTimeout` (30s): the gateway would drop the very
  client it was waiting for, with a symptom that points nowhere near the cause.

  The coupling is now explicit as `server.MaxHandlerBlockingWait` =
  `pongTimeout - pingInterval` = **20s**, `cmd/gateway` exits 1 above it (same
  fail-fast precedent as a missing `JOIN_TOKEN_SECRET`), and a test asserts the
  default stays strictly under it — the old 20s default sat exactly on the
  ceiling with no room for scheduling or poll-interval slop.

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
- `--allocation-wait-timeout` / `ALLOCATION_WAIT_TIMEOUT` (default **15s**) and
  `--allocation-poll-interval` / `ALLOCATION_POLL_INTERVAL` (default **250ms**)
  bound that wait (flag wins, then env, then default; an unparseable or
  non-positive env value is logged and ignored rather than failing start-up).
  15s is a deliberate compromise while pod cold start is unmeasured: longer than
  `retryTotalTimeout`'s 10s because a pod start is far heavier than a Redis blip,
  below `JoinTokenTTL` (30s) so the wait can never outlast the token minted after
  it, and strictly below `server.MaxHandlerBlockingWait` (20s) so it cannot starve
  the connection's heartbeat. Also exposed programmatically as
  `registry.WithAllocationWait`.
- `registry.ErrKindNotConfigured`: the allocator has no Fleet for the requested
  kind. `KindDungeon` returns it by default now that `DefaultFleetDungeon` is
  gone.
- `server.MaxHandlerBlockingWait` (`pongTimeout - pingInterval` = 20s): the
  longest a message handler may block the connection's read loop before starving
  its heartbeat. Exported so `cmd/gateway` can refuse a wait above it.
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
