# Gateway API — Wire Protocol

All frames are `shared/messages.Envelope` (`{type, payload}`) with a 4-byte big-endian
length prefix, 1 MB max frame. Message ids are defined in `shared/messages/messages.go`.

### Encoding: Protobuf or JSON, detected per frame

The envelope body is **either** Protobuf (default) **or** legacy JSON, and the
receiver tells them apart from the **first body byte** — no negotiation, no
version field, no extra round trip:

| First byte | Encoding | Why it cannot collide |
|---|---|---|
| `0x08` | Protobuf | proto3 always emits field 1 (`type`), which is ≥ 1 for every real message |
| `0x7B` (`{`) | JSON | a JSON object always opens with `{` |

The gateway **never chooses** an encoding. `ClientConn.enc` is latched from the
first frame decoded on the connection and every reply goes back in that same
encoding (`cc.Reply`, never `messages.NewEnvelope`). That is what lets a Protobuf
gateway keep serving a JSON client, and lets gateway, game server and Unity
client be upgraded in any order. See `shared/docs/DESIGN.md`.

Decoding fails closed: `type == 0` is rejected in both branches. Without that
guard a body starting `0x12` parses as a well-formed Envelope carrying only
field 2 — a silent half-parse rather than an error.

> One deliberate exception: the duplicate-login kick built in `kickLocalUser`
> uses `messages.NewEnvelope` (JSON) because it is constructed off the victim
> connection's read-loop goroutine, and `enc` may only be read from that
> goroutine.

## Handshake

```
1. Client → Gateway  MsgAuth          AuthRequest{Token}          (JWT from Nakama)
2. Gateway → Client  MsgAuthResp      AuthResponse{OK, UserID}
3. Client → Gateway  MsgEnterWorld    EnterWorldRequest{MapID}
4. Gateway → Client  MsgEnterWorldResp EnterWorldResponse{ServerAddr, JoinToken, Transport}
5. Client → Gateway  MsgDisconnect    (no payload)                (optional, graceful)
```

`Transport` names the realtime transport the **target game server** speaks —
`"tcp"` or `"kcp"` — copied from that server's registry entry. **Empty means
`"tcp"`**, which is what entries written before the field existed carry. The
client must dial the game server with this transport, not with whatever it used
to reach the gateway: the two are configured independently.

## Messages handled by the gateway

| Id | Message | Precondition | Behavior |
|----|---------|--------------|----------|
| 1 | `MsgAuth` | none — the only frame accepted without a session | Verify JWT locally (`session.VerifyClientJWT`, shared secret, no Nakama call) → `SessionStore.Set("session:{user_id}", TTL=1h)` → reply `MsgAuthResp{OK}`. Invalid token/payload → `MsgAuthResp{OK:false, Error}` |
| 3 | `MsgEnterWorld` | live session | Least-loaded live server for `MapID` with `PlayerCount < Capacity`; a map with **no** live server may be allocated (waited for), a map whose servers are all full is refused → 30s join token (`sid` claim = server id), minted last → `MsgEnterWorldResp` |
| 9 | `MsgDisconnect` | live session | Destroy the session record, half-close the socket |
| 11 | `MsgPing` | **none — handled before the session check** | Reply `MsgPong{Timestamp echoed, ServerTime}` |
| 12 | `MsgPong` | **none — handled before the session check** | Refresh the connection's liveness timer; on an authenticated connection also re-arm the session TTL (at most once per minute, store errors fail open) |
| other | — | live session | Logged and ignored |

**Heartbeat frames bypass session enforcement on purpose.** They are dispatched
in `handleMessage` *before* `checkSession`: a `MsgPong` that keeps the connection
alive must not be rejected because a Redis blip made the session lookup fail, and
a ping carries no session semantics at all. They are still subject to the inbound
frame limiter, which runs first.

**Heartbeats are session activity** (#231). A `MsgPong` on an authenticated
connection re-arms the session TTL (`refreshSessionOnPong`), so a client that
keeps the gateway socket open sending only heartbeats — the recommended shape —
never has its session expire under the live connection. The refresh is bounded
to once per `sessionRefreshInterval` (**1 min**) per connection so a 10 s
heartbeat cadence does not translate into a store write per pong, and a store
error fails open exactly like `checkSession`: the refresh is skipped, the
connection is untouched, and nothing is sent — a pong never draws a reply. An
unauthenticated pong refreshes nothing.

The gateway pings on its own initiative every **10 s** (`pingInterval`) and closes
any connection that has not produced a `MsgPong` within **30 s** (`pongTimeout`).
The game server uses the same two values, so a client can run one heartbeat
implementation against both hops.

### Eviction: `MsgKick` + `MsgDisconnect`, in that order

When the gateway evicts a connection it sends **two** frames carrying the **same**
reason string, then half-closes:

```
Gateway → Client  MsgKick(15)        KickMessage{reason}        typed eviction signal
Gateway → Client  MsgDisconnect(9)   DisconnectMessage{reason}  legacy frame, same reason
                  <FIN>
```

Both are sent because they address different clients, not because they carry
different information. `MsgKick` is the typed signal; `MsgDisconnect` is what
every client written before `MsgKick` was wired up already acts on, and such a
client silently ignores an unknown type 15. Sending only `MsgKick` would strand
them on a socket that simply stops answering.

**Client contract**: a client that understands `MsgKick` takes the reason from it
and MUST treat the `MsgDisconnect` that follows as *the same eviction*, not a
second event — otherwise it reports the disconnect twice. A client that does not
understand `MsgKick` ignores it and behaves exactly as before. The order is
therefore part of the contract, not an implementation detail.

| Reason | Emitted when |
|---|---|
| `duplicate_login` | the same user authenticated on another connection (same gateway, or another gateway via the kick Pub/Sub channel) |

`wire.proto` names further reasons (`server_shutdown`, `session_expired`,
`rate_limited`) as an intended vocabulary. **The gateway does not emit them
today**: shutdown closes sockets without a frame, an expired session replies
`MsgAuthResp{error:"session expired"}` on the *next* frame rather than pushing
anything, and the frame limiter replies `MsgAuthResp{error:"rate limited"}`.
Treat the table above as the complete current set; a client should handle an
unrecognised reason by disconnecting and surfacing a generic message.

Both frames are JSON regardless of the connection's latched encoding — eviction
runs on the *evicting* connection's goroutine, and `ClientConn.enc` may only be
read from the evicted connection's own `ReadLoop`. A Protobuf client must
therefore accept a JSON eviction frame, which the first-byte sniff already
handles transparently.

## Session enforcement (added 2026-08-04)

Every frame except `MsgAuth` goes through `checkSession`:

1. Connection must be past `StateConnected` and carry a `UserID`; otherwise
   `not authenticated`.
2. `SessionStore.Get("session:{user_id}")` must return the same user id; otherwise the
   connection is demoted to `StateConnected` and the client gets `session expired`.
   **Only `storage.ErrNotFound` counts as "gone".** A store *failure* (Redis
   unreachable, timeout) is a different condition and fails open: the connection
   stays authenticated and the frame is processed. See "Store failures" below.
3. On success `SessionStore.Refresh(key, SessionTTL)` re-arms the TTL — a sliding window
   driven by client activity.

### Store failures vs expiry (changed 2026-08-06)

These were previously conflated, so a Redis blip told every online player
`session expired` and forced a full re-login. They are now distinct:

| Condition | Client sees | Connection state |
|-----------|-------------|------------------|
| Session record absent (`storage.ErrNotFound`) | `session expired` | demoted to `StateConnected` |
| Store unreachable / timeout | *nothing* — the frame is processed normally | unchanged, stays authenticated |

Failing open is deliberate: the client already proved possession of a valid JWT
at `MsgAuth`, so the residual risk is that an explicitly destroyed session keeps
working for the duration of the outage — strictly better than disconnecting the
entire player base because a dependency restarted. Store failures increment
`gateway_session_checks_total{result="store_error"}`.

Error replies reuse the response type of the request: `MsgEnterWorldResp{Error}` for
`MsgEnterWorld`, `MsgAuthResp{OK:false, Error}` otherwise.

**Error strings are a closed set.** Internal error text is never forwarded to a
client — it embeds internal hostnames, private IPs and ports. Anything not
classified below is reported as `internal error` and logged server-side.

| Error string | Meaning |
|--------------|---------|
| `invalid auth request` | payload did not decode |
| `invalid token` | JWT signature/expiry rejected |
| `session creation failed` | session store write failed |
| `not authenticated` | frame sent before a successful `MsgAuth` |
| `session expired` | session record gone (TTL, explicit disconnect, evicted elsewhere) |
| `invalid enter world request` | `MsgEnterWorld` payload did not decode |
| `no server available for map` | the map's live server(s) are full, or the map has no server and allocation failed for a reason other than an exhausted fleet. **Do not retry** — retrying cannot create capacity, and ADR-2 forbids adding a second server to a full map |
| `all servers busy, retry shortly` | the map has no live server and the allocation API answered `UnAllocated`: every GameServer in the fleet is taken and none is `Ready` at this instant (`registry.ErrNoCapacity`). **Retryable** — retry after a few seconds. Distinct from `no server available for map`: nothing here is full, the fleet is momentarily empty and the Fleet controller is already bringing a replacement to `Ready` (5.38s measured on k3d, ADR-18) |
| `server is starting, retry shortly` | an allocation is (or was) under way for this map but no address exists yet: the allocated pod had not registered itself when the wait window expired, or the handler's own `server.EnterWorldBudget` (18s) ran out while the allocation was still in flight — the allocation keeps running detached either way. **Retryable** — retry after a few seconds. Distinct from `all servers busy, retry shortly`, where no server was allocated at all |
| `map is not available` | no fleet or server in this deployment hosts the requested `map_id`: the pod that answered the allocation serves a different map (its fleet's `GAMESERVER_MAP_ID`), or the registry index returned a server for another map. **Terminal — do not retry**: retrying cannot change which map a fleet serves, and every retry costs a GameServer Agones never un-allocates. Distinct from `no server available for map`, which means the map exists but is full |
| `not implemented` | the requested transfer mode is unimplemented (e.g. dungeon) |
| `internal error` | anything else — store failure, allocator failure, token signing failure |
| `rate limited` | connection tripped the inbound frame limiter |

> Changed 2026-08-06: `MsgEnterWorld` previously replied with the raw wrapped
> error (`assign map: find servers: dial tcp 10.0.1.7:6379: ...`). Clients that
> string-matched on `assign map: ...` must switch to the values above.

## Session teardown

A session record is destroyed when:

- the client sends `MsgDisconnect`, or
- the socket closes for any reason (`handleConn` defer), or
- its TTL lapses in the store.

This keeps a Redis-backed store from reporting ghost-online players after a drop.

## Join token

`transfer.GenerateJoinToken(userID, serverID, secret)` — HS256, TTL
`constants.JoinTokenTTL` (30s), claims `{sub: userID, sid: serverID, jti}`. The
game server verifies it as the first frame on its socket.

Signed with **`JOIN_TOKEN_SECRET`**, not `JWT_SECRET` (added 2026-08-06). The
join secret is distributed to every game-server pod; the auth secret is not, so
sharing them made one compromised pod able to forge client auth tokens.

> **`JOIN_TOKEN_SECRET` is mandatory on both sides — there is no fallback.**
> The gateway refuses to start without it (`cmd/gateway/main.go`), and so does
> the C# game server (`Program.cs`, exit code 2). Both log a warning when it is
> set to the *same value* as `JWT_SECRET`, which defeats the split.
>
> Corrected 2026-08-11: this section previously said an unset `JOIN_TOKEN_SECRET`
> falls back to `JWT_SECRET` "because `gameserver-dotnet` cannot read the new
> variable yet". Both claims are obsolete — the game server reads it
> (`GameServerHost` parses it into `_joinKeys`) and requires it. Following the old
> text now yields a gateway that will not boot, or, if only the gateway is given
> a distinct secret, a fleet where **every join fails signature verification**.

### Replay protection (`jti`)

`SignWithServer` attaches a unique `jti` claim **whenever `serverID` is non-empty**
— i.e. to every join token, and to no client auth token. The game server consumes
it exactly once through `JtiTracker.TryConsume` and rejects a token whose `jti` is
missing or already seen (`"Token already used"`).

So a join token is **single-use, single-server, 30-second**: `sid` must equal the
receiving server's own id (empty `sid` is rejected outright, no bypass), the `jti`
must be fresh, and the expiry bounds the window.

The tracker is **in-memory and per game-server process**, holding consumed ids for
60 s. Two consequences worth designing around: a restarted pod forgets the set
(harmless — the 30 s TTL is shorter than the gap), and the guard does not span
pods, which is why `sid` pinning carries the cross-pod half of the protection.

Both secrets accept a comma-separated rotation list; the keyring variants
(`GenerateJoinTokenKeyring`, `ValidateJoinTokenKeyring`, `AssignMapKeyring`)
take a pre-parsed `jwt.Keyring` so `EnterWorld` does no string splitting.

## Rate limiting (added 2026-08-06)

| Surface | Default | Key | On reject |
|---|---|---|---|
| Connection accept | 10/min, burst 10 | source IP | socket closed immediately, no frame |
| Inbound frame | 60/s, burst 120 | connection | one `MsgAuthResp{ok:false, error:"rate limited"}`, then a half-close (FIN) |

The message-limit disconnect is an orderly TCP half-close (`CloseWrite`), not a
hard close, so the error frame is guaranteed to reach the client — a hard close
with the flood still unread would make the kernel send RST and discard it.
Clients should therefore expect: error frame → EOF. `MsgDisconnect` is handled
the same way.

Both increment `gateway_rate_limited_total{reason="connection"|"message"}`.
`0` on either env var disables that limiter. Limits are per gateway process.

## Server allocation (added 2026-08-04)

`MsgEnterWorld` for a map that **no registered server hosts at all** triggers an
Agones allocation when the gateway runs with `--allocator=agones`:

```
MsgEnterWorld{map_id}
  -> registry.FindServer            # storage.ServerRegistry lookup, capacity filtered
  -> (map has ZERO live servers) AgonesAllocator.Allocate
       POST {apiserver}/apis/allocation.agones.dev/v1/namespaces/{ns}/gameserverallocations
       {"apiVersion":"allocation.agones.dev/v1","kind":"GameServerAllocation",
        "metadata":{"namespace":"rpg-realtime"},
        "spec":{"selectors":[{"matchLabels":{"agones.dev/fleet":"map-servers-dotnet-dev"}}],
                "scheduling":"Packed"}}
  <- status{state:"Allocated", gameServerName, address, ports:[{name:"game",port}]}
  -> poll registry for gameServerName until the POD registers itself
       (--allocation-wait-timeout, --allocation-poll-interval)
  -> MsgEnterWorldResp{server_addr, transport, join_token(sid = gameServerName)}
       # every field taken from the pod's OWN registry entry
```

Two rules govern this path.

**Allocation replaces an absent server; it never adds capacity to a full one**
(ADR-2, one live game server per `map_id`). If the map already has live servers
and every one is at capacity, `MsgEnterWorld` replies
`no server available for map` and **no allocation is requested**. Registering a
second server under one `map_id` would produce two disconnected copies of the
world with no handoff between them; refusing the join is a loud, bounded
failure, a silently split world is not. `FindServer` still logs a loud warning if
a map ever does resolve to more than one server — that is the detector for the
invariant being broken by some other means.

**The join token is minted last, from the pod's own registry entry.** An
allocation response only names the pod; the pod still has to start its NativeAOT
container, bind, report `Ready` to the SDK sidecar, learn its own address and
self-register. The gateway does **not** write that entry on the pod's behalf
(ADR-1: one writer per datum — and a gateway-written entry has nothing re-arming
its 15s TTL). It polls for the entry instead, and only then signs the token, so
the address and `transport` announced to the client are the ones the server
self-reported. Since join tokens are single-use, pinned to one `sid` and live
only `constants.JoinTokenTTL` (30s), minting earlier would burn the client's only
token on an address that is not answering. If the entry never appears the client
gets the retryable `server is starting, retry shortly` and **no** token.

**Allocation is single-flight per `map_id`.** Concurrent `MsgEnterWorld` calls for
one unserved map produce exactly one allocation; the rest wait for it and share
its outcome. Without that, the retry this API asks for is an amplifier — each
retry of `server is starting, retry shortly` would allocate another pod, only one
can ever win the `map_id` registration, and nothing deallocates the losers. A
failed allocation is never cached, so the next request is free to try again.

**An exhausted fleet is retryable; every other allocator failure is not.** When
the allocation API answers `UnAllocated`, `AllocateServer` returns
`registry.ErrNoCapacity` and `MsgEnterWorld` replies `all servers busy, retry
shortly` rather than the terminal `no server available for map`. The split is
narrow on purpose. `UnAllocated` is a decoded 2xx body stating that **no**
GameServer was handed out, so the retry it invites costs one allocation POST and
leaks no pod — and the retry that finally succeeds usually costs not even that,
because a pod self-registers at startup *before* any allocation (ADR-18), so once
the replacement is `Ready` the next `MsgEnterWorld` resolves it straight from the
registry with no allocator call. Any other allocator failure — transport error,
non-2xx status, undecodable body — may have allocated a pod whose response was
lost, and since Agones has no un-allocate and this gateway has no `Deallocate`,
inviting a retry there is precisely how un-reclaimable pods accumulate. Those
keep the terminal message. Note what is **not** bounded: nothing caps how often a
client may retry `all servers busy`. That bound belongs to the client's backoff,
not the gateway — a gateway-side wait would convert a millisecond refusal into a
multi-second stall on the join path for a condition that may never clear, and the
`allocateOnce` single-flight already collapses concurrent retries for one map
into one allocation attempt. Issue #152.

**An allocated server must serve the map that was asked for.** Allocation targets
a **Fleet**, not a `map_id`: the request Agones receives is "give me a GameServer
of fleet X", and the pod it returns serves whatever its own fleet spec's
`GAMESERVER_MAP_ID` says. Nothing in the allocation request carries the requested
map. The wait that follows polls the registry by **`ServerID`**, so for a
single-map fleet asked to serve some other map it succeeds *instantly* — the pod
self-registered under its fleet map at boot — and returns an entry for the wrong
world. Until 2026-08-17 the gateway announced that entry: the client joined a
map it never asked for, with a valid join token, and every layer logged success.
The map on the entry is now compared with the requested one, and a mismatch is
refused with `map is not available` (`registry.ErrFleetMapMismatch`) — a
configuration fault, deliberately **not** a flavour of `ErrNoServerAvailable`,
because "grow the fleet" and "fix `GAMESERVER_MAP_ID`" are opposite operator
responses. The same comparison runs on the registry path: `FindByMapID` is keyed
by `map_id`, so an entry it returns for another map means the store's index is
lying, and that is refused too rather than quietly filtered.

**A proven mismatch is remembered, and it is the one cached failure.** Agones has
no un-allocate and this project has no `Deallocate`, so an `Allocated` GameServer
never returns to the pool. The single-flight above merges *concurrent* callers
only; a client retrying is sequential, and each sequential attempt found the map
still unregistered (the pod registered under the fleet's map, not the requested
one) and allocated another pod — an unbounded drain from one client politely
retrying a `map_id` that does not exist. The verdict is therefore cached for
`--allocation-mismatch-ttl` (default **60s**), bounding the cost to **one
GameServer per `map_id` per TTL**. This does not contradict "a failed allocation
is never cached": those failures are transient (Redis blip, momentarily exhausted
fleet) and resolve by themselves, so caching one would poison a map that is about
to work. A fleet's map is fixed for the life of its pods — no number of retries
turns the answer into "yes" — while the TTL still lets a corrected fleet be
picked up without restarting the gateway.

What this does **not** fix: the gateway still cannot make a fleet serve an
arbitrary map. The real answer is patching `GAMESERVER_MAP_ID` per allocation
through `GameServerAllocation` metadata, which needs the game server to read its
map from a source it does not read today (`backend/gameserver-dotnet/`). Until
then a `map_id` is servable only if some fleet was deployed for it.

**The wait is bounded by the heartbeat, not just by taste.** `MsgEnterWorld` is
handled on the connection's own read-loop goroutine, which is also what records
the client's `MsgPong`, so the wait cannot approach `pongTimeout` (30s) without
the gateway disconnecting the client it is waiting for. The ceiling is
`pongTimeout - pingInterval` = **20s** (`server.MaxHandlerBlockingWait`); the
gateway **refuses to start** with a larger `--allocation-wait-timeout`, and the
15s default sits below it with a margin.

**One deadline covers the whole path, because the legs stack.** Bounding each
leg is not enough: a cold-map join can chain the registry lookup's retry window
(`registry.RetryTotalTimeout`, 10s) + the allocation HTTP call
(`registry.DefaultTimeout`, 10s) + the registration wait (15s) ≈ **35s** — well
past the 20s heartbeat window even though every leg is individually legal
(issue #235). `handleEnterWorld` therefore runs the entire assignment under one
deadline, `server.EnterWorldBudget` = `MaxHandlerBlockingWait − 2s` = **18s**
(the 2s slice is reserved for the session write-back and response flush). When
the budget expires the client gets the retryable
`server is starting, retry shortly` and the connection stays up — while the
single-flight allocation leader keeps running **detached** to completion, so
the client's retry resolves the freshly registered server from the registry
without allocating again. A guard test pins the stacked worst case against the
same constants.

The already-registered path is unaffected: no allocation, no polling, no added
latency.

`state: "UnAllocated"` (fleet exhausted) surfaces as `registry.ErrNoCapacity`, wrapped
into the `MsgEnterWorldResp.error` string. Non-2xx responses report the Kubernetes
`Status.message`.

**Server-id contract**: `sid` in the join token is the allocated `gameServerName`.
Pods must register under that same id — `gameserver/cmd/gameserver` resolves
`--server-id` from `GAMESERVER_ID`, then `POD_NAME` (injected by the fleet manifests
through the downward API), then the legacy `gs-<mode>-<map_id>` default.

### Allocator flags / env (cmd/gateway)

| Flag | Env | Default | Description |
|------|-----|---------|-------------|
| `--allocator` | `ALLOCATOR` | `none` | `none` or `agones` |
| `--allocator-namespace` | `ALLOCATOR_NAMESPACE` | `rpg-realtime` | Namespace holding the fleets |
| `--allocator-fleet-map` | `ALLOCATOR_FLEET_MAP` | `map-servers-dotnet-dev` | Fleet for map allocations — **one fleet for every `map_id`**. The allocator never validates the name, so a fleet that does not exist fails at the first allocation, not at start-up; and because allocation is by fleet, a request for a `map_id` this fleet does not serve is only caught *after* the allocation, by the map comparison above |
| `--allocator-fleet-dungeon` | `ALLOCATOR_FLEET_DUNGEON` | *(none)* | Fleet for dungeon allocations. Unset by design — no dungeon fleet is deployed, so `KindDungeon` fails with `no fleet configured for allocation kind` naming the setting to fix |
| `--allocator-kubeconfig` | `ALLOCATOR_KUBECONFIG` | in-cluster, then `$KUBECONFIG`, then `~/.kube/config` | Credential source |
| `--allocation-wait-timeout` | `ALLOCATION_WAIT_TIMEOUT` | `15s` | How long to wait for an allocated pod to register itself before replying `server is starting, retry shortly`. Generous because pod cold start is unmeasured; below `JoinTokenTTL` (30s) so the wait cannot outlast the token minted after it, and strictly below `server.MaxHandlerBlockingWait` (20s) so it cannot starve the connection's heartbeat. **The gateway refuses to start above 20s.** |
| `--allocation-poll-interval` | `ALLOCATION_POLL_INTERVAL` | `250ms` | Registry re-check interval during that wait (≤80 single-key reads over the full window) |
| `--allocation-mismatch-ttl` | `ALLOCATION_MISMATCH_TTL` | `60s` | How long a proven "the configured fleet does not serve this `map_id`" verdict is remembered, refusing further allocations for that map with `map is not available`. Bounds the leak to one GameServer per map per window. A **negative** value disables the memory and restores allocate-per-retry; it is logged loudly at start-up and is an escape hatch, not a tuning knob |

All three accept Go duration strings (`15s`, `500ms`). Flag wins, then env, then
the default; an unparseable value is logged and ignored rather than failing
start-up. For the two wait knobs a non-positive env value is also ignored; for
`--allocation-mismatch-ttl` a negative value is honoured, because negative means
"disabled" there. They only apply when `--allocator=agones`.

## Go API additions

| Symbol | Package | Description |
|--------|---------|-------------|
| `session.SessionKey(userID) string` | `gateway/session` | Canonical `session:{user_id}` key |
| `(*SessionManager).RefreshSession(ctx, sessionID) error` | `gateway/session` | Re-arms the session TTL; errors when the session is gone |
| `registry.NewRegistryServiceWithAllocator(reg, alloc)` | `gateway/registry` | Registry that asks an `Allocator` for a new instance when a map has **no** live server |
| `registry.WithAllocationWait(timeout, interval)` | `gateway/registry` | Bounds the wait for an allocated server's own registry entry |
| `registry.ErrKindNotConfigured` | `gateway/registry` | Sentinel: no Fleet configured for the requested allocation kind (the default state of `KindDungeon`) |
| `server.MaxHandlerBlockingWait` | `gateway/server` | `pongTimeout - pingInterval`: the longest a handler may block the read loop before starving the heartbeat |
| `server.EnterWorldBudget` | `gateway/server` | `MaxHandlerBlockingWait - 2s`: the single deadline over the whole `MsgEnterWorld` assignment path, so its stacked legs (lookup retries + allocation call + registration wait ≈ 35s worst case) cannot outlive the heartbeat window (issue #235) |
| `registry.RetryTotalTimeout` | `gateway/registry` | Total cap on transient-error retry loops (lookup/get); exported so the guard test derives the stacked enter_world worst case from the constants the code runs on |
| `registry.ErrServerStarting` | `gateway/registry` | Retryable sentinel: allocated server never registered inside the wait window, or the caller's context ended while the single-flight allocation was still running detached |
| `registry.NewAgonesAllocator(AgonesConfig) (*AgonesAllocator, error)` | `gateway/registry` | Allocator backed by the Agones `GameServerAllocation` API |
| `registry.AllocationRequest` / `KindAllocator` | `gateway/registry` | Kind-aware allocation (`KindMap`, `KindDungeon`) |
| `registry.ErrNoCapacity` | `gateway/registry` | Sentinel for `state: UnAllocated` (fleet exhausted). Matchable through `allocateAndWait`'s two-verb `%w` wrap, which is what lets the gateway answer `all servers busy, retry shortly` instead of the terminal message |
| `(*RegistryService).GetServer(ctx, serverID)` | `gateway/registry` | Single live server lookup |
| `events.NewRelay(stream, name, sink, logger) *Relay` | `gateway/events` | Real `EventRelay` over any `storage.EventStream` |
| `events.Sink` / `events.SinkFunc` | `gateway/events` | Per-event callback contract |
| `server.WithEventRelay(relay) Option` | `gateway/server` | Attaches a relay; started in `Run`, stopped in `Shutdown` |
| `(*Gateway).OnEvent(ev)` / `EventCount()` / `ConnCount()` | `gateway/server` | Relay sink + introspection |

`server.New` keeps its original 4-argument form (options are variadic), so existing
callers — including `integration_test` — compile unchanged.
