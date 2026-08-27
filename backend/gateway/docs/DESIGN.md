# Gateway — Design Decisions

## 2026-08-22 — The registry watcher is wired in, so a dead server leaves the gateway's view before its TTL

### Context

`registry.RegistryWatcher` polls the servers the gateway knows about, notices one
that has disappeared from the registry and publishes a `server_down` event. It
had four passing unit tests and **no caller outside them** (#204):
`NewRegistryWatcher` was never invoked by `cmd/gateway`, so `Start` never ran in a
real gateway. Server death was therefore observable only when the registry TTL
expired, and until that moment the gateway kept handing clients the address of a
server that would not answer — the window that produces the split-map fault fixed
in #203.

### Decision — wire it, and route every construction through one function

Agones health checks cover **pod liveness**. The gateway's **registry view** is a
separate thing, and an entry lingering there until TTL is exactly the state that
misroutes clients. Prompt removal is what keeps that window small, so the watcher
is kept and wired rather than deleted.

`cmd/gateway.wireRegistry` is now the only place the binary constructs a
`RegistryService`. It builds the watcher, attaches it with `registry.WithWatcher`
and starts its poll loop on the process-wide context; shutdown cancels that
context and then blocks on `watcher.Stop()` before the stores are closed. Making
it the sole construction site is deliberate: the previous failure mode was wiring
that could vanish without anything failing, and now removing the call breaks the
build while removing the watcher inside it fails `TestWireRegistry_*`.

### Decision — the tracked set comes from lookups, not only from registration

`RegistryService.RegisterServer` / `DeregisterServer` track and untrack, but in
production **the gateway never registers a server**: game servers self-register
straight into Redis (ADR-2), and those two methods have no non-test callers. A
watcher fed only by them would poll an empty set forever. So `FindServer` also
tracks the server it returns — the moment the gateway learns a server exists is
the moment it hands its address to a client, which is also the only server whose
death the gateway has any reason to care about.

### Poll interval against the heartbeat TTL

`watchPollInterval` = **5s** against `constants.ServerHeartbeatTTL` = **15s**, a
**3x** margin. The relationship, not the constants, is what matters: a poll at or
above the TTL would always lose the race to expiry and the watcher would be a
no-op that still costs a registry read per server per tick.
`TestWatchPollInterval_ShorterThanHeartbeatTTL` pins it so it cannot silently
invert when either value is retuned.

### Consequences

- `server_down` is published through the gateway's existing event stream
  (`eventStreamPublisher`), not a second pub/sub client: Redis Streams on the
  Redis backend, in-memory otherwise. No new dependency.
- The event is currently consumed by the relay and logged/counted — the same MVP
  limitation as every other event (no `MsgEvent` on the wire). The detection is
  what this change buys; acting on it client-side stays blocked on `shared`.

## 2026-08-17 — Allocation only replaces an absent server, and the token is minted last

### Context

Two latent problems in the allocation path became reachable the moment
`ALLOCATOR=agones` was switched on for the first time. Neither could fire before,
because allocation could not previously produce a live server.

### Decision 1 — a full map is refused, never given a second server

`FindServer` used to allocate whenever *no server had spare capacity*, which
includes the case where the map already has a live server that is simply full. It
then registered the result under the same `map_id`. That directly breaks the MVP
invariant of **one live game server per `map_id`** (ADR-2): two instances are two
disconnected copies of the world, players on them cannot see or interact with
each other, and there is no handoff between them.

Allocation now fires **only when the registry returns zero entries for the map**.
Live-but-full resolves to `ErrNoServerAvailable` with no allocator call at all.
Refusing a join is a loud, bounded failure that the client already handles; a
silently split world is neither. Deliberately *not* softened into a fallback.

The multi-server warning in `FindServer` stays: it is now the detector for the
invariant being violated by some other route (a stray manual registration, a pod
that outlived its deregistration).

### Decision 2 — the join token is minted only once the target is dialable

Join tokens live `constants.JoinTokenTTL` = **30s**, are single-use (the game
server consumes the `jti`) and are pinned to one server id. The old code minted
one the instant `FindServer` returned — including for a pod that had only just
been allocated. An Agones pod at that moment has not started its NativeAOT
container, bound its port, reported `Ready` to the sidecar, learned its own
address or registered. If that took longer than 30s, the client burned its only
token on an address that was not answering.

`FindServer` now polls the registry for the allocated **`ServerID`** and returns
the entry *the game server wrote about itself*, from which the token, address and
transport are then derived. The gateway does **not** write that entry on the
server's behalf: that would put two writers on one datum (ADR-1), and a
gateway-written entry has nothing re-arming its 15s heartbeat TTL, so it would
expire under a pod that is alive.

Bounds: `--allocation-wait-timeout` (default **20s**) and
`--allocation-poll-interval` (default **250ms**), flag → env → default. 20s is a
deliberate compromise while pod cold start is unmeasured: longer than
`retryTotalTimeout`'s 10s, because a pod start is much heavier than a Redis blip,
but still under `JoinTokenTTL` so the wait can never outlast the token minted
after it. Timing out yields `ErrServerStarting` → the client-facing
`server is starting, retry shortly`, which is **retryable** and deliberately
distinct from `no server available for map` (full/unavailable — do not retry).
No token and no address are handed out in that case.

The already-registered path is untouched: no allocator call, no polling, no added
latency. It is the common case and a test asserts it performs zero `GetServer`
calls.

### Decision 3 — allocation is single-flight per `map_id`

Telling clients to retry makes concurrency a correctness problem, not an
efficiency one. Each retry of `server is starting, retry shortly` found the map
still unserved and allocated *another* pod; only one of them can ever win the
`map_id` registration (decision 1), nothing deallocates the rest — Agones does
not reclaim an `Allocated` GameServer and no `Deallocate` path exists anywhere in
this codebase — so on a `replicas: 1` fleet a single reconnecting player could
exhaust the fleet and leave a trail of stuck pods.

The first caller for a map allocates and waits; everyone who arrives while that
runs blocks on the same call and shares its result. Kept as ~40 lines of
map-of-channels under a dedicated mutex rather than adding
`golang.org/x/sync/singleflight`: the module has no `golang.org/x/sync`
dependency today (only transitive `go.sum` hashes), the package's extra surface
(`DoChan`, `Forget`, shared-result counting) is unused here, and the one subtlety
we *do* need — never caching a result — is a `delete` we want to be able to see.

Two properties that are not obvious and are tested:

- **A failed allocation is not cached.** The in-flight entry is removed as soon
  as the leader finishes, success or failure, so one transient allocator error
  cannot poison a map until restart. Success needs no cache either: by then the
  server is in the registry and `FindServer` resolves it before reaching the
  allocator.
- **The leader's work is detached from the leader's own context**
  (`context.WithoutCancel`). Followers must not lose an allocation because the
  client that happened to arrive first hung up. A follower that gives up leaves
  the leader running for everyone else.

The existing-server path never touches the mutex or the map.

### Decision 3b — the server you get must serve the map you asked for

Decision 2 closed "the pod has not registered yet". It did not close "the pod
registered, but for a different map", and that gap shipped a silent join to the
wrong world.

The mechanism, in order:

1. Allocation is by **Fleet**, never by map. `AgonesAllocator.Allocate` posts a
   `GameServerAllocation` whose only selector is `agones.dev/fleet: <fleet>`; the
   requested `map_id` is used solely to fill in the returned `ServerInfo`. There
   is exactly one map fleet (`ALLOCATOR_FLEET_MAP`), and its pods serve whatever
   its fleet spec's `GAMESERVER_MAP_ID` says.
2. The wait polls by **`ServerID`**, not by map, because the point of the wait is
   "has *this pod* published its entry".
3. A pod self-registers under its own map at boot. So when a client asks for
   `map_77` and the only fleet serves `map_01`, the allocation succeeds, the poll
   hits the pod's pre-existing `map_01` entry on the first read, and the wait
   returns a healthy entry — for the wrong map.

Reproduced on a k3d Agones fleet on 2026-08-17: `map_77` requested, the client
handed `map-servers-dotnet-dev-…` at `127.0.0.1:7002` (map `map_01`), join
accepted, snapshots flowing, smoke test `PASS`. No error was logged anywhere; the
only tell was the latency (448ms vs ~3ms on the registry path).

`FindServer` now compares the resolved entry's `MapID` with the requested one on
**both** paths and returns `ErrFleetMapMismatch`, which is:

- **not** `ErrNoServerAvailable`. That means "the map is full / we are out of
  capacity" and points an operator at fleet size. This means "the fleet you
  configured hosts a different map" and points at `GAMESERVER_MAP_ID`. Collapsing
  them would send the operator to the wrong knob.
- **not** `ErrServerStarting`, which is the one *retryable* assignment failure.
  This one is terminal: no retry changes which map a fleet serves.

On the registry path the same comparison is a store-integrity check rather than a
fleet one: `FindByMapID` is keyed by `map_id`, so an entry for another map means
the index is lying. It is refused, not filtered: filtering would degrade into "no
server for this map" and, with an allocator configured, into an allocation the
map does not need.

### Decision 3c — the one failure that *is* cached

The mismatch also leaks. Because the pod registers under `map_01`, `map_77` stays
unregistered, so the *next* sequential request allocates again. Decision 3's
single-flight does not help: it merges callers that overlap in time, and a client
retry loop does not. Three GameServers were watched going `Allocated` for one
`map_77` retry loop, and none of them ever comes back — Agones has no un-allocate
and this codebase has no `Deallocate`. A guard that only rejects *after* the
allocation still burns one pod per attempt.

So a proven mismatch is remembered per `map_id` for `--allocation-mismatch-ttl`
(default **60s**), and `FindServer` consults it *before* calling the allocation
API. The bound is **one GameServer per `map_id` per TTL**, asserted by a test.

This is a deliberate exception to decision 3's "never cache a failure", and the
difference is the failure's nature, not its severity:

| | transient allocation failure | fleet/map mismatch |
|---|---|---|
| Cause | Redis blip, fleet momentarily exhausted, lost boot race | fleet spec's `GAMESERVER_MAP_ID` |
| Changes without operator action | yes, seconds | no, not while those pods run |
| Cost of retrying | one API call | one permanently-`Allocated` GameServer |
| Cost of caching | poisons a map that was about to work | none within the TTL |

The TTL exists so the cache cannot outlive its truth: redeploying the fleet with
the right map makes the gateway usable again within a minute, no restart. A
negative value disables the memory outright — logged loudly at start-up, since it
re-arms the drain.

**What this does not solve.** The gateway still cannot serve a map no fleet was
deployed for. The real fix is to stop allocating by fleet-only and patch the
allocated pod's `GAMESERVER_MAP_ID` through `GameServerAllocation`'s `metadata`
(Agones applies annotations/labels to the allocated GameServer), which requires
the C# game server to read its map from the pod's own annotations rather than the
env var baked into the fleet spec. That is `backend/gameserver-dotnet/` plus
`backend/deploy/`, neither of which this change owns. Nor does the guard
distinguish "a map that does not exist in the game" from "a map whose fleet is
not deployed yet" — the gateway has no map catalogue, so both surface as
`map is not available`.

### Decision 4 — refuse a wait that would starve the heartbeat

A connection is served by one goroutine: the read loop dispatches a frame and
does not read the next one — including `MsgPong` — until that handler returns.
`handleEnterWorld` is now the one handler that blocks for a *configurable* time,
so a large `--allocation-wait-timeout` silently stops the client's pongs from
being recorded and `HeartbeatLoop` closes the connection after `pongTimeout`
(30s). The gateway would drop the very client it was holding the socket open for,
with a symptom — "clients vanish during slow allocations" — that points nowhere
near the cause.

`server.MaxHandlerBlockingWait` = `pongTimeout - pingInterval` = **20s** makes
the coupling explicit and exported, and `cmd/gateway` **exits 1** above it (same
fail-fast precedent as a missing `JOIN_TOKEN_SECRET`). The margin is one full
ping period: at the ceiling, a single lost or delayed ping still leaves a whole
interval for a pong to arrive and be processed.

The default came down **20s → 15s** as a direct consequence: a default sitting
exactly on a ceiling has no room for scheduling and poll-interval slop, and it is
the value nobody is choosing deliberately. A test in `gateway/server` asserts
`DefaultAllocationWaitTimeout < MaxHandlerBlockingWait`, which no start-up check
would catch until someone ran it.

### Decision 5 — the allocator's default fleet names the fleet that exists

`DefaultFleetMap` was `map-servers-dev`, the retired **Go** fleet whose game
server was deleted in `670a803` and whose manifests are gone. Nothing caught it:
`NewAgonesAllocator` only builds a REST client, so the gateway started clean and
failed at the first allocation — the one code path that matters, at the one
moment it matters. It is now `map-servers-dotnet-dev`, the only deployed fleet,
and a test asserts the constant against that literal so the next rename fails in
CI rather than in the cluster.

`DefaultFleetDungeon` was deleted rather than repointed. No dungeon fleet exists
(ADR-14 stage 6 is unstarted), so any default would be the same trap one
generation later. An unset fleet is now simply not registered for that kind, and
`Allocate(KindDungeon)` fails with `ErrKindNotConfigured` naming the flag to set
— a configuration error stated as one, instead of a Kubernetes 404 for a fleet
that was never going to be there.

**Not implemented: validating the fleet at construction.** It is tempting —
`NewAgonesAllocator` has a REST client in hand — but it is the wrong trade twice
over. It makes the gateway's start-up depend on cluster reachability, and the
gateway is the redirector that must come up during a cluster outage (metrics,
probes and already-registered maps all keep working without Agones). And reading
a Fleet needs `get fleets` RBAC, which the gateway does not have and should not
be granted: it holds exactly `create gameserverallocations`. A validating gateway
would emit a 403 that looks identical to a missing fleet. The compensating
control is the constant-vs-deployed-name test above, which catches the actual bug
class with no runtime coupling at all.

### Consequences

- `--allocator-transport` / `ALLOCATOR_TRANSPORT` **no longer reaches a client.**
  It stamps a transport onto the allocation response, and that response is now
  used only for its `ServerID`; the announced transport always comes from the
  pod's own registry entry. The flag is retained (it costs nothing and the
  allocator still fills the field) but it is now inert — a candidate for removal.
- A map whose only server is full is now a hard refusal for as long as it is
  full. Raising capacity, or sharding into separate `map_id`s, is the answer —
  not a second instance.
- `gateway_allocations_total{result="ok"}` now means "allocated **and** the pod
  registered", not "the allocation API answered 200".

## 2026-08-04 — Real Agones allocator (GameServerAllocation API)

### Context

`MsgEnterWorld` for a map no live server hosts used to be a dead end: the registry
returned "no available server" because the only `Allocator` implementation was
`StubAllocator`. The drawio "lookup / allocate" edge (page 3) needed a real
implementation against the Agones control plane running on k3s.

### Decisions

**1. Talk to the aggregated allocation API over REST, not agones-allocator gRPC.**

Agones exposes two ways to allocate:

| Option | Why not / why |
|--------|---------------|
| `agones-allocator` gRPC service | Separate deployment, requires client TLS certs + a `Secret` per caller and a LoadBalancer/NodePort. Its reason to exist is *multi-cluster* allocation. Our gateway is one hop from the same cluster's API server. |
| Aggregated API `POST /apis/allocation.agones.dev/v1/namespaces/{ns}/gameserverallocations` | **Chosen.** Same auth as everything else (ServiceAccount token in-cluster, kubeconfig out of cluster), no extra component, no cert plumbing. Identical semantics — it is the API `kubectl create -f allocation.yaml` hits. |

Multi-cluster allocation, when we get there, is an additive change: the same
request body is what `agones-allocator` proxies.

**2. Thin hand-rolled types instead of the Agones clientset.**

`agones.dev/agones/pkg/apis/allocation/v1` transitively drags `viper`, `hcl`,
`fsnotify` and friends into the gateway binary (via `agones/pkg/util/runtime`),
for a payload that is ~6 fields. The gateway therefore models the request and the
`status` it reads (`state`, `gameServerName`, `address`, `ports[]`) locally in
`registry/agones_allocator.go` and JSON-decodes leniently — unknown fields are
ignored, so Agones can add fields without breaking us. `k8s.io/client-go` is still
a real dependency: it owns credential resolution (in-cluster SA token, kubeconfig
including exec plugins, CA bundle, proxy) — precisely the part worth *not*
hand-rolling. `rest.HTTPClientFor(cfg)` gives an `*http.Client` already carrying
that auth, so the allocator is one `http.Post` away.

**3. Fleet selection by label, kind → fleet mapping in config.**

The request selects `agones.dev/fleet=<fleet>`, with `KindMap`/`KindDungeon`
mapped to `--allocator-fleet-map` / `--allocator-fleet-dungeon`
(*corrected 2026-08-17*: the map default is `map-servers-dotnet-dev`, the only
deployed fleet; the dungeon kind has **no** default and fails with
`ErrKindNotConfigured` until a dungeon fleet exists — see the 2026-08-17 entry at
the top of this file). `AgonesAllocator` satisfies both the existing
`Allocator` (map path, used by `RegistryService`) and the richer `KindAllocator`
(the seam the dungeon transfer will use).

**4. Server-id contract: GameServer name == pod name == registered server id.**

An allocation answers with a `gameServerName`; the gateway signs it into the join
token as `sid`. For the game server to accept that token it must have registered
under the same id — but pods derived their id as `gs-<mode>-<map_id>`, which is
identical for every replica of a fleet. Fix (surgical, in `gameserver/cmd`):
`resolveServerID` prefers `GAMESERVER_ID`, then `POD_NAME`, then the old default,
and the four fleet manifests inject `POD_NAME` via the downward API
(`fieldRef: metadata.name`). Standalone `go run` behaviour is unchanged.

**5. Opt-in, fail-fast wiring.**

`--allocator=none|agones` (env `ALLOCATOR`), default `none` — no Kubernetes
config, no behaviour change, CI and integration tests untouched. With `agones`,
a bad kubeconfig is fatal at boot rather than a surprise on the first player.

### Consequences

- An unserved map now costs one API round trip (10s timeout) instead of an error.
- `ErrNoCapacity` (`state: UnAllocated`) is distinguishable from transport/API
  failures via `errors.Is`, so autoscaler-backoff logic can be added later.
- The gateway needs RBAC to `create gameserverallocations` in its namespace when
  it runs in-cluster (out-of-cluster it inherits the kubeconfig user).
- Allocated servers are registered in the gateway's registry with the returned
  name/address, so a second player entering the same map reuses it instead of
  allocating again.

### Still open

- Dungeon allocation is wired at the allocator (`KindDungeon`) but
  `transfer.StubDungeonTransfer` still does not call it.
- No `Deallocate` / shutdown path: reclaiming is left to Agones idle handling.
- The gateway does not read `status.state` changes afterwards — a GameServer that
  dies after allocation is noticed via the registry heartbeat TTL, or sooner by
  the registry watcher (5s poll, wired in 2026-08-22 — see the entry at the top of
  this file). `status.state` itself is still not read.

## 2026-08-04 — Selectable store backends, session lifecycle, event relay wiring

### Context

The gateway wrote session records but never read them back, ignored `MsgDisconnect`, and
constructed its stores directly in `cmd/gateway` as in-memory maps. `events.StubEventRelay`
had zero callers. That made the gateway effectively stateful-by-omission: sessions leaked
until TTL, and nothing could ever run more than one instance.

### Decisions

**1. Backends are chosen at process start, never at call sites.**
`cmd/gateway/main.go` builds either the memory trio (`MemorySessionStore`,
`MemoryServerRegistry`, `MemoryEventStream`) or the Redis trio (`redisstore.*`) and injects
them through the existing `storage.*` interfaces. No package below `main` knows which one it
got, so no business logic changes between dev and production.

Resolution order: `--backend` → `GATEWAY_BACKEND` → `redis` if `REDIS_ADDR` is exported →
`memory`. The env sniff exists because `shared/config` hard-defaults `RedisAddr` to
`localhost:6379`; using that field as the switch would silently make Redis the default and
break the tests. Trade-off: an operator who only sets `GATEWAY_BACKEND=redis` still gets the
`localhost:6379` default endpoint — acceptable, and logged at startup.

The three Redis stores share a single `redis.Client` (one connection pool per process)
rather than three, matching the "< 100 MB per instance" budget.

**2. Sessions are validated on every frame, not just written.**
`checkSession` runs before every non-`MsgAuth` frame: read the record, compare the user id,
then `Refresh` the TTL. Two round trips per frame instead of one — acceptable because the
gateway only handles handshake-rate traffic (auth / enter-world / disconnect), not the
per-tick input stream, which goes client↔gameserver directly.

Refresh-on-activity (sliding TTL) over a fixed 1h window: a player in a 3-hour raid must not
be logged out mid-session, and an abandoned socket must not hold a record for an hour.
Heartbeats count as activity (#231): `MsgPong` on an authenticated connection also re-arms
the TTL — bounded to once per minute per connection, failing open on store errors — because
the recommended client shape is to park the gateway socket sending only heartbeats between
map transfers, and without this the session expired under the live connection after an hour
on one map.

A vanished record demotes the connection to `StateConnected` and returns `session expired`
rather than closing the socket, so the client can re-`MsgAuth` on the same connection.

**3. Session teardown on both paths.**
`MsgDisconnect` and the `handleConn` defer both call `cleanupSession`. Relying on TTL alone
would leave a Redis-backed deployment reporting ghost-online players for up to an hour.
`cleanupSession` takes the identity via `cc.ClearIdentity()`, which returns the user it
cleared under the connection's lock, so a double call is a no-op and the two paths cannot
both destroy the same session.

**3a. Connection identity is mutex-guarded, and not by accident.**
`ClientConn` is touched by two goroutines per connection: the read loop (`ReadLoop` →
`handleMessage` → auth / session checks / teardown) and the write loop (`WriteLoop` →
`writeLoop` / `CloseGracefully`). The identity fields cross that boundary — the read side
writes them, the write side reads them for every log line it emits — so they are unexported
and reached only through `UserID()` / `State()` / `Identity()` / `SetAuthenticated()` /
`SetInWorld()` / `ClearIdentity()`, all guarded by an `RWMutex`. They were plain exported
fields until a `-race` CI build caught `cleanupSession` writing them while
`CloseGracefully` read them.

The rest of the struct is deliberately *not* locked, and each for a stated reason:
`msgBucket` and `limited` are only ever touched from the read loop (`allowMessage` is
called from `handleMessage` and nowhere else), `closeAfterFlush` and `halfClosed` are
`atomic.Bool`, and `conn` / `sendCh` / `done` / `once` / `logger` are immutable after
construction or internally synchronised. Adding a field that crosses the read/write
boundary means putting it behind `mu`.

**4. The relay is real; only its sink is stubbed.**
`events.Relay` subscribes to any `storage.EventStream` and dispatches to a `Sink`. The
gateway is that sink (`Gateway.OnEvent`). It currently **logs and counts** events instead of
pushing them to clients, because `shared/messages` has no client-facing event type — ids
stop at `MsgDisconnect`, and `shared` is owned by `agent-shared`. When a `MsgEvent` lands,
`Gateway.OnEvent` becomes the fan-out point (iterate `g.conns`, `cc.Send`) and nothing else
moves. `StubEventRelay` was changed from "always `ErrNotImplemented`" to a no-op, since
`Run` now starts whatever relay it is given and a hard error there would kill startup.

Sink wiring in `main` is a closure over the not-yet-built `*Gateway` (the relay is a
constructor argument of the gateway that also needs the gateway as its sink). Safe because
dispatch cannot happen before `Run` starts the relay.

**5. Allocation is a registry concern, least-loaded is the placement policy.**
`FindServer` now scans all live servers with spare capacity and returns the lowest
`PlayerCount` instead of the first match — first-match piles players onto whichever server
the store happened to list first. When nothing has capacity and an `Allocator` is configured,
the registry allocates and registers the new instance. *(Superseded 2026-08-17: allocation
now fires only when the map has **no** live server, and the registry no longer writes the
allocated entry — see the entry at the top of this file. Superseded again 2026-08-22 (#203):
least-loaded is the placement policy only while a map has exactly ONE live server. With more
than one — a violated ADR-2 invariant — placement is the lowest `ServerID` with spare capacity
and `PlayerCount` is ignored, because balancing across a split world is what keeps both halves
populated.)* `StubAllocator` still returns
`ErrNotImplemented`, so `cmd/gateway` wires the registry *without* an allocator: the honest
"no available server" error beats a misleading "allocator not implemented".

### Consequences

- The gateway is genuinely stateless with the Redis backend: N instances share sessions,
  registry and the event consumer group (one group `gateway`, one consumer per instance).
- Dead game servers disappear from lookups on their own (`redisstore.ServerRegistry`
  heartbeat TTL). *(Superseded 2026-08-22: expiry alone leaves up to a full
  `ServerHeartbeatTTL` in which the gateway still announces a dead server, so the
  gateway now runs the registry watcher on top of it — see the entry at the top of
  this file. TTL expiry remains the backstop; the watcher is what shortens the
  window.)*
- Tests run both backends: memory directly, Redis via `miniredis` (no external service, and
  `FastForward` makes TTL behavior assertable).

### Still open

- No `MsgEvent` on the wire → relay is log-only (blocked on `shared`).
- Agones allocation and dungeon transfer remain stubs. *(Updated 2026-08-22:
  `player:location:{user_id}` tracking was listed here as a third stub, but it was
  never a stub — no code ever wrote or read that key. The constant behind it is
  deleted, #210.)*

## 2026-08-04 — Opt-in KCP listener + per-hop transport announcement

`Gateway.Run` now listens through `shared/transport.Listen(kind, addr)` instead
of `net.Listen("tcp", …)`, with the kind supplied by the new
`server.WithTransport` option (`--transport` / `GATEWAY_TRANSPORT`, default
`tcp`). Accepted connections are `net.Conn` either way, so `ClientConn`,
`ReadLoop`/`WriteLoop` and every handler are unchanged.

The client's two hops are negotiated independently: the gateway may speak TCP
while the assigned game server speaks KCP, or vice versa. `AssignResult` gained
`Transport`, copied from the target server's `storage.ServerInfo.Transport`, and
`handleEnterWorld` forwards it in `EnterWorldResponse.Transport`. Empty means
`tcp`, so registry entries written by game servers that predate the field still
produce a response every existing client understands.

The gateway does **not** verify that it can itself reach the game server over
the announced transport — the registry is the single source of truth, exactly as
it already is for `Addr`. A misconfigured game server is a deploy bug, not a
runtime negotiation the gateway can fix.

Why the gateway does not simply mirror its own transport onto the response: map
servers and dungeon servers can be rolled to KCP independently of the gateway
fleet (they are separate Agones fleets), so a per-server value is the only one
that stays correct during a partial rollout.

**Agones allocation is the one case the registry cannot answer.** When
`FindServer` falls through to the allocator, the gateway synthesizes the
`ServerInfo` itself from the allocation response and registers it — the pod has
not registered yet, and the allocation API says nothing about transports. So
`AgonesConfig.Transport` (`--allocator-transport` / `ALLOCATOR_TRANSPORT`,
defaulting to the gateway's own `--transport`) is stamped onto the allocated
entry. It **must match the fleet manifest's** `--transport` argument; a mismatch
sends the first client of a freshly allocated pod to the wrong transport, until
the pod's own registration overwrites the entry. The default (inherit the
gateway's transport) is correct for a uniform rollout, which is the normal case.

> **Superseded 2026-08-17.** The gateway no longer registers the allocated
> `ServerInfo` and no longer announces its transport: it waits for the pod's own
> registry entry and announces that (see the 2026-08-17 entry at the top of this
> file). `--allocator-transport` therefore no longer reaches any client — the
> mismatch failure mode described above cannot occur any more, and the flag is
> inert.


---

## 2026-08-06 — Rate limiting and secret separation

See `backend/docs/ARCHITECTURE-DECISIONS.md` ADR-8 for the threat model; this
entry covers the gateway-specific implementation choices.

### Where the limiters sit

**Per-IP, immediately after `Accept`.** The check happens before
`NewClientConn`, before `trackConn`, before the handler goroutine — a rejected
connection costs one map lookup and a `Close()`. Putting it after any of those
would mean a connection flood still allocates a goroutine stack and a channel
per attempt, which is most of the damage.

**Per-connection, as a struct field.** `ClientConn.msgBucket` is a value, not a
`*Limiter` keyed by IP. Two reasons. First cost: this runs on every inbound
frame, and a mutex + map lookup per frame is the kind of thing that only shows
up under the load you least want it under. As a plain field it is 10.8 ns and no
allocation, and it needs no lock because only `ReadLoop`'s goroutine touches it.
Second correctness: keying messages by IP would let one player behind a shared
NAT throttle everyone else on it.

**Why per-IP for connections but per-user for the Nakama RPC.** At accept time
there is no identity yet — the IP is all there is. By the time `gateway_token`
is called the caller is authenticated, so the user id is both available and the
better key, for the same NAT reason.

### Reply-once-then-close

A connection that trips the message limiter gets exactly one `rate limited`
frame and is then closed. The alternatives were both worse:

- *Reply to every over-limit frame* — the limiter becomes an amplifier: the
  attacker sends cheap frames and the gateway answers every one of them.
- *Close immediately with no reply* — indistinguishable from a crash or a
  network fault, and a legitimate client (a buggy build, say) has no way to
  learn why.

Making that work took two mechanisms, because there are two distinct ways the
explanatory frame can be lost.

**The queue race** — `Close()` tears down the socket immediately, which races
the write loop and drops a frame that is still sitting in `sendCh`.
`SendAndClose` sets a flag that `WriteLoop` checks *after* a successful write,
so the connection only ends once the queue is drained. `cc.limited` suppresses
everything after the first rejection.

**The RST race** — flushing the frame is not enough. A hard `Close()` on a TCP
socket whose *receive* queue still holds unread bytes makes the kernel emit RST
instead of FIN, and an RST discards the socket's unsent send buffer. That is
precisely the state a flooding client leaves behind: the gateway stops reading
after the limit trips, so the rest of the flood sits unread, and the frame that
was just written gets thrown away. The client sees a bare connection reset with
no reason — and because it depends on whether `ReadLoop` happened to drain the
backlog before `WriteLoop` closed, it only shows up under load. It surfaced as
an intermittent `TestMsgRateLimit` failure in a loaded `go test ./...`.

`CloseGracefully` fixes it structurally:

1. `CloseWrite()` on the `*net.TCPConn` — the queued bytes leave with a FIN
   rather than an RST. The client reads the frame, then a clean EOF.
2. A `closeDrainTimeout` (2s) read deadline bounds the wait, so a client that
   ignores the FIN cannot pin a socket.
3. `ReadLoop` — the socket's **only** reader — keeps consuming the backlog until
   EOF or the deadline, then its deferred `Close()` runs against an empty
   receive queue, which is a clean FIN.

Draining deliberately stays in `ReadLoop` rather than happening inside
`CloseGracefully`: a second reader racing `Decode` mid-frame would corrupt the
very teardown this exists to make orderly. Transports without half-close (KCP —
`kcp.UDPSession` has no `CloseWrite`) fall back to a plain `Close`, which is
safe there for the same reason it is unsafe on TCP: no kernel receive queue, no
RST, and kcp-go flushes pending output on close.

`handleDisconnect` uses the same path — a client that pipelined anything after
`MsgDisconnect` would otherwise turn an orderly goodbye into a reset.

The regression test asserts both halves: the explicit `rate limited` frame
*and* that the stream ends with EOF rather than `ECONNRESET`. The second
assertion is the deterministic one — it fails on every run with the old hard
close, instead of only on unlucky timing.

### Defaults

10 conn/min/IP and 60 msg/s/conn. The real protocol is three frames on one
connection, so both are ~2 orders of magnitude above legitimate use. They exist
to bound abuse, not to shape traffic — a limit tight enough to be interesting is
a limit that will page someone at 3am over a flapping mobile link.
`TestMsgRateLimitAllowsNormalHandshake` is the regression guard.

### Two secrets, not one

`JWT_SECRET` verifies what Nakama issued; `JOIN_TOKEN_SECRET` signs what the
gateway issues. The asymmetry that matters: the join secret must be distributed
to every game-server pod, while the auth secret only ever lives on Nakama and
the gateway. Sharing them meant a compromised pod could mint auth tokens for any
user. Split, it can at most mint join tokens — which only get you into a game
server you already are.

The fallback (unset → reuse `JWT_SECRET`) exists because the C# game server
cannot yet read `JOIN_TOKEN_SECRET`; turning the split on unilaterally would
break every join. The start-up warning is the reminder.

Keyrings are parsed once in `New` and stored, not parsed per request —
`AssignMapKeyring` exists precisely so `EnterWorld` does no string splitting.

## Surviving Redis (2026-08-06)

Four gaps from the disaster-recovery audit turned a Redis blip into a
player-visible outage. The common thread: the gateway treated a degradable
dependency as a required one.

### The gateway does not need Redis to serve

Redis backs sessions, the server registry and the event stream. None of that is
in the gameplay data path — the gateway hands out `{ServerAddr, JoinToken}` and
the client talks to the game server directly (ADR-3). A player already in a map
is unaffected by Redis being down. So the design rule is: **a Redis failure
degrades the gateway, it never kills it.**

Concretely:

- **Boot (G3).** `relay.Start` failing used to propagate out of `Run`, so `main`
  exited 1 and the pod crash-looped. The relay now starts in the background and
  retries with backoff (1s → 30s). The listener binds regardless.
- **Runtime (G6).** `checkSession` conflated "store returned an error" with
  "session is gone", so an outage de-authenticated the entire online
  population — the single worst outcome available, since the sessions were
  actually fine. Store failures now fail *open*.
- **After a wipe (G4).** `NOGROUP` was retried as if transient. It is not: the
  group is gone and every subsequent read fails identically. The relay
  re-creates it.
- **Visibility (G9).** None of the above was observable. `gateway_redis_up`,
  `gateway_relay_up`, `gateway_stream_group_loss_total` and the
  `store_error` split on `gateway_session_checks_total` make each degraded mode
  a series you can alert on.

### Why failing open on session checks is the right trade

Failing closed sounds safer and is not. The client presented a valid,
signature-checked JWT at `MsgAuth`; the session record is a *revocation and
presence* mechanism layered on top, not the authentication itself. When the
store is unreachable the gateway cannot answer "was this revoked?", and the two
options are:

- **Fail closed** — disconnect every online player, force a full re-login storm
  against an already-sick dependency, for a threat (a session revoked in the
  last few seconds) that is rare and low-impact.
- **Fail open** — honour the JWT for the duration of the outage. A session
  explicitly destroyed just before the outage keeps working until Redis returns.

The blast radius of the first is the entire player base; of the second, a
handful of already-authenticated users. `TestExpiredSessionStillRejected` pins
that this does not weaken the normal path: an affirmative "not found" is still
a rejection.

### Liveness must not depend on Redis

`/healthz` stays 200 during a Redis outage; `/readyz` does not. k8s restarts on
liveness failure and only deschedules on readiness failure, so tying Redis to
liveness converts a dependency outage into a fleet-wide simultaneous restart —
maximum damage, zero benefit, since restarting cannot fix Redis. See
`docs/README.md` for the probe wiring.

### Client-facing errors are a closed set (G7)

`handleEnterWorld` forwarded the raw wrapped error, which meant an
unauthenticated peer could read `dial tcp 10.0.1.7:6379: connect: connection
refused` — free internal topology. Classification is now explicit and the
default is generic, so a newly introduced error type cannot start leaking; it
just reports `internal error` until someone deliberately classifies it. The
`registry.ErrNoServerAvailable` sentinel exists so the one genuinely useful
condition survives that collapse without string matching.

#### Decision — an exhausted fleet is retryable, a full map is not (#152)

Until 2026-08-18 `clientSafeAssignError` gave one terminal answer,
`no server available for map`, to two conditions that are not alike:

- **the map has live servers and all are full** (`registry.go:408`, no allocator
  call at all). Stable. ADR-2 forbids growing out of it. Terminal is correct.
- **the map has no live server and the allocation API answered `UnAllocated`**
  (`registry.go:559`, wrapping `registry.ErrNoCapacity`). Self-correcting: the
  Fleet controller is already bringing a replacement pod to `Ready`, measured at
  5.38s on k3d (ADR-18).

The two were already distinguishable at the point of classification and nobody
had noticed: `allocateAndWait` wraps with a **two-verb** `%w`
(`"%w %s: allocate: %w"`), so `errors.Is(err, registry.ErrNoCapacity)` matches
through it while `errors.Is(err, registry.ErrNoServerAvailable)` still does too.
No registry change was needed — only a new `case`, placed **before** the
`ErrNoServerAvailable` case so the broader sentinel cannot swallow the narrower
one, and a new message `all servers busy, retry shortly`.

**Why this does not reopen the pod leak.** The reason the terminal answer existed
is that every retry allocates and Agones has no un-allocate. That reason does not
apply here: `UnAllocated` is a decoded 2xx body stating that no GameServer was
handed out, so retrying it costs one allocation POST and **no pod**. Better, the
successful retry usually costs no POST either — pods self-register at startup
before any allocation (ADR-18), so once the replacement is `Ready` the next
`EnterWorld` resolves it on the registry path. The branch is therefore narrowed to
`ErrNoCapacity` alone: a transport error, a non-2xx status or an undecodable body
may each have allocated a pod whose response was lost, so those keep the terminal
message. Compare `ErrServerStarting`, which *is* retryable today and where each
retry genuinely does allocate a fresh pod — the new branch is strictly the safer
of the two.

**Why the retry is not bounded server-side.** `ErrServerStarting`'s bound is a
*wait* (`allocWaitTimeout`), not a retry cap; nothing stops a client looping on it
either. Adding a gateway-side wait for fleet capacity was rejected: it turns a
millisecond refusal into a multi-second stall on the join path — the issue's own
point is that today's cost is a wrong-looking refusal, *not* latency — and it
would wait on a condition that may never clear (a genuinely full fleet with no
autoscaler, which ADR-18 pins this deployment to). The bound belongs to the
client's backoff. What the gateway does contribute is `allocateOnce`, which
collapses concurrent retries for one `map_id` into a single allocation attempt.
The residual, named rather than fixed: N clients retrying sequentially drive up
to N allocation POSTs per round against the Agones API. That is API load, not a
leak, and it is the price of the honest answer.
