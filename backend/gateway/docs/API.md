# Gateway API — Wire Protocol

All frames are `shared/messages.Envelope` (`{type, payload}`) with a 4-byte big-endian
length prefix, 1 MB max frame. Message ids are defined in `shared/messages/messages.go`.

## Handshake

```
1. Client → Gateway  MsgAuth          AuthRequest{Token}          (JWT from Nakama)
2. Gateway → Client  MsgAuthResp      AuthResponse{OK, UserID}
3. Client → Gateway  MsgEnterWorld    EnterWorldRequest{MapID}
4. Gateway → Client  MsgEnterWorldResp EnterWorldResponse{ServerAddr, JoinToken}
5. Client → Gateway  MsgDisconnect    (no payload)                (optional, graceful)
```

## Messages handled by the gateway

| Id | Message | Precondition | Behavior |
|----|---------|--------------|----------|
| 1 | `MsgAuth` | none — the only frame accepted without a session | Verify JWT locally (`session.VerifyClientJWT`, shared secret, no Nakama call) → `SessionStore.Set("session:{user_id}", TTL=1h)` → reply `MsgAuthResp{OK}`. Invalid token/payload → `MsgAuthResp{OK:false, Error}` |
| 3 | `MsgEnterWorld` | live session | Least-loaded live server for `MapID` with `PlayerCount < Capacity` → 30s join token (`sid` claim = server id) → `MsgEnterWorldResp` |
| 9 | `MsgDisconnect` | live session | Destroy the session record, close the socket |
| other | — | live session | Logged and ignored |

## Session enforcement (added 2026-08-04)

Every frame except `MsgAuth` goes through `checkSession`:

1. Connection must be past `StateConnected` and carry a `UserID`; otherwise
   `not authenticated`.
2. `SessionStore.Get("session:{user_id}")` must return the same user id; otherwise the
   connection is demoted to `StateConnected` and the client gets `session expired`.
3. On success `SessionStore.Refresh(key, SessionTTL)` re-arms the TTL — a sliding window
   driven by client activity.

Error replies reuse the response type of the request: `MsgEnterWorldResp{Error}` for
`MsgEnterWorld`, `MsgAuthResp{OK:false, Error}` otherwise.

| Error string | Meaning |
|--------------|---------|
| `invalid auth request` | payload did not decode |
| `invalid token` | JWT signature/expiry rejected |
| `session creation failed` | session store write failed |
| `not authenticated` | frame sent before a successful `MsgAuth` |
| `session expired` | session record gone (TTL, explicit disconnect, evicted elsewhere) |
| `assign map: ...` | no live server with capacity for the map |

## Session teardown

A session record is destroyed when:

- the client sends `MsgDisconnect`, or
- the socket closes for any reason (`handleConn` defer), or
- its TTL lapses in the store.

This keeps a Redis-backed store from reporting ghost-online players after a drop.

## Join token

`transfer.GenerateJoinToken(userID, serverID, secret)` — HS256, TTL
`constants.JoinTokenTTL` (30s), claims `{sub: userID, sid: serverID}`. The game server
verifies it as the first frame on its socket.

Signed with **`JOIN_TOKEN_SECRET`**, not `JWT_SECRET` (added 2026-08-06). The
join secret is distributed to every game-server pod; the auth secret is not, so
sharing them made one compromised pod able to forge client auth tokens. Unset
`JOIN_TOKEN_SECRET` falls back to `JWT_SECRET` (unchanged behaviour, start-up
warning) — required today because `gameserver-dotnet` cannot read the new
variable yet.

Both secrets accept a comma-separated rotation list; the keyring variants
(`GenerateJoinTokenKeyring`, `ValidateJoinTokenKeyring`, `AssignMapKeyring`)
take a pre-parsed `jwt.Keyring` so `EnterWorld` does no string splitting.

## Rate limiting (added 2026-08-06)

| Surface | Default | Key | On reject |
|---|---|---|---|
| Connection accept | 10/min, burst 10 | source IP | socket closed immediately, no frame |
| Inbound frame | 60/s, burst 120 | connection | one `MsgAuthResp{ok:false, error:"rate limited"}`, then close |

Both increment `gateway_rate_limited_total{reason="connection"|"message"}`.
`0` on either env var disables that limiter. Limits are per gateway process.

## Server allocation (added 2026-08-04)

`MsgEnterWorld` for a map that no registered server hosts (or where every server is
full) triggers an Agones allocation when the gateway runs with `--allocator=agones`:

```
MsgEnterWorld{map_id}
  -> registry.FindServer            # storage.ServerRegistry lookup, capacity filtered
  -> (miss) AgonesAllocator.Allocate
       POST {apiserver}/apis/allocation.agones.dev/v1/namespaces/{ns}/gameserverallocations
       {"apiVersion":"allocation.agones.dev/v1","kind":"GameServerAllocation",
        "metadata":{"namespace":"rpg-realtime"},
        "spec":{"selectors":[{"matchLabels":{"agones.dev/fleet":"map-servers-dev"}}],
                "scheduling":"Packed"}}
  <- status{state:"Allocated", gameServerName, address, ports:[{name:"game",port}]}
  -> register {server_id: gameServerName, addr: "address:port"}
  -> MsgEnterWorldResp{server_addr, join_token(sid = gameServerName)}
```

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
| `--allocator-fleet-map` | `ALLOCATOR_FLEET_MAP` | `map-servers-dev` | Fleet for map allocations |
| `--allocator-fleet-dungeon` | `ALLOCATOR_FLEET_DUNGEON` | `dungeon-servers-dev` | Fleet for dungeon allocations |
| `--allocator-kubeconfig` | `ALLOCATOR_KUBECONFIG` | in-cluster, then `$KUBECONFIG`, then `~/.kube/config` | Credential source |

## Go API additions

| Symbol | Package | Description |
|--------|---------|-------------|
| `session.SessionKey(userID) string` | `gateway/session` | Canonical `session:{user_id}` key |
| `(*SessionManager).RefreshSession(ctx, sessionID) error` | `gateway/session` | Re-arms the session TTL; errors when the session is gone |
| `registry.NewRegistryServiceWithAllocator(reg, alloc)` | `gateway/registry` | Registry that asks an `Allocator` for a new instance when nothing has capacity |
| `registry.NewAgonesAllocator(AgonesConfig) (*AgonesAllocator, error)` | `gateway/registry` | Allocator backed by the Agones `GameServerAllocation` API |
| `registry.AllocationRequest` / `KindAllocator` | `gateway/registry` | Kind-aware allocation (`KindMap`, `KindDungeon`) |
| `registry.ErrNoCapacity` | `gateway/registry` | Sentinel for `state: UnAllocated` (fleet exhausted) |
| `(*RegistryService).GetServer(ctx, serverID)` | `gateway/registry` | Single live server lookup |
| `events.NewRelay(stream, name, sink, logger) *Relay` | `gateway/events` | Real `EventRelay` over any `storage.EventStream` |
| `events.Sink` / `events.SinkFunc` | `gateway/events` | Per-event callback contract |
| `server.WithEventRelay(relay) Option` | `gateway/server` | Attaches a relay; started in `Run`, stopped in `Shutdown` |
| `(*Gateway).OnEvent(ev)` / `EventCount()` / `ConnCount()` | `gateway/server` | Relay sink + introspection |

`server.New` keeps its original 4-argument form (options are variadic), so existing
callers — including `integration_test` — compile unchanged.
