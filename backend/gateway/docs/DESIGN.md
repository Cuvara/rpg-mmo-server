# Gateway — Design Decisions

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
(`map-servers-dev` / `dungeon-servers-dev` by default, matching
`deploy/agones/*-dev.yaml`). `AgonesAllocator` satisfies both the existing
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
  dies after allocation is only noticed via the registry heartbeat TTL.

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
the registry allocates and registers the new instance. `StubAllocator` still returns
`ErrNotImplemented`, so `cmd/gateway` wires the registry *without* an allocator: the honest
"no available server" error beats a misleading "allocator not implemented".

### Consequences

- The gateway is genuinely stateless with the Redis backend: N instances share sessions,
  registry and the event consumer group (one group `gateway`, one consumer per instance).
- Dead game servers disappear from lookups on their own (`redisstore.ServerRegistry`
  heartbeat TTL); the gateway needs no liveness logic of its own.
- Tests run both backends: memory directly, Redis via `miniredis` (no external service, and
  `FastForward` makes TTL behavior assertable).

### Still open

- No `MsgEvent` on the wire → relay is log-only (blocked on `shared`).
- Agones allocation, dungeon transfer, and `player:location:{user_id}` tracking remain stubs.

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
