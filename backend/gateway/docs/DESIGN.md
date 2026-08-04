# Gateway — Design Decisions

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
- Transport is TCP; KCP swap untouched.
