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
`cleanupSession` clears `UserID`/`State` so a double call is a no-op.

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
