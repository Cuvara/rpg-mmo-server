# GameServer — Design Decisions

Dated entries, newest last. Every entry states the decision, the rationale, and the
trade-off accepted.

## 2026-08-04 — Join token `sid` enforcement

**Decision.** `Server.handleConnection` now compares the JWT `sid` claim against the
server's own `--server-id`. A mismatch is answered with
`JoinTokenResponse{OK:false, Error:"join token not valid for this server"}` and the
socket is closed.

**Rationale.** The gateway mints a join token per *allocated* server
(`transfer.GenerateJoinToken`). Without the check, one token was replayable against every
game server sharing the JWT secret — a player could bypass capacity limits and map
assignment entirely.

**Trade-off.** Tokens with an *empty* `sid` are still accepted (logged at WARN). Legacy
and test callers use `jwt.Sign` (no `sid`); rejecting them would break the in-process E2E
harness and any dev tooling. Once every issuer uses `jwt.SignWithServer`, the empty case
should become a hard reject.

## 2026-08-04 — Registry self-registration and heartbeat

**Decision.** On `Run()` the server writes `storage.ServerInfo{ServerID, MapID, Addr,
Capacity, PlayerCount}` and starts a heartbeat goroutine at
`constants.ServerHeartbeatTTL / 3` (5s for a 15s TTL). `UpdatePlayerCount` fires on every
join and leave. `Shutdown()` stops the heartbeat and deregisters.

**Rationale.** The Redis registry is TTL-based: an entry disappears if nobody refreshes
it, so a crashed pod stops being handed out to players without any external reaper. A
TTL/3 period tolerates two consecutive lost round-trips before the entry expires.

**Trade-off.** If a heartbeat reports "unknown server" (entry already expired, e.g. after
a Redis failover) the loop *re-registers* rather than giving up. That makes the server
self-healing, at the cost of possibly resurrecting an entry an operator deliberately
deleted. Deliberate removal should go through `Shutdown()`.

**Backend selection.** `cmd/gameserver` builds `storage.NewMemoryServerRegistry()` by
default and `redisstore.NewServerRegistry(cfg.RedisAddr, cfg.RedisPassword)` under
`--redis`. Nothing above the constructor changes — both satisfy `storage.ServerRegistry`.
`REDIS_ADDR` has a non-empty default in `shared/config`, so an explicit `--redis` flag
(not "addr is set") is what selects the backend.

## 2026-08-04 — Reconnect hold window

**Decision.** A TCP close no longer removes the player entity. The server does a save,
then arms a `time.AfterFunc(holdTTL)`; `holdTTL` is `constants.EntityHoldTTL` (30s) in map
mode and `constants.DungeonHoldTTL` (60s) in dungeon mode, overridable via
`ServerOpts.HoldTTL` for tests. A join for the same user inside the window cancels the
timer and *reattaches to the live entity* (`acquireEntity`), preserving position, HP,
cooldowns, and `LastInputTick`. On expiry the server does the final save and removes the
entity.

**Rationale.** Mobile networks drop sockets constantly. Reloading from `PlayerStore` on
every reconnect would rewind the player to the last 30s batch save — losing combat state
and teleporting them. Holding the entity makes a reconnect continuous and is what the
30s/60s constants were declared for.

**Trade-offs.**
- A held entity is still simulated and still visible in other players' AOI snapshots — it
  is an "AFK body" that can be attacked. That is intentional (no disconnect-to-escape) but
  means capacity accounting must use connection count, not entity count. `PlayerCount` in
  the registry therefore tracks `ConnectionManager.Count()`.
- Expiry runs on a timer goroutine, not in the tick loop, so removal is not tick-aligned.
  Acceptable: nothing in the simulation depends on the exact tick of a despawn.
- `expireHold` re-checks `conns.Get(userID)` after taking the lock, closing the race where
  a reconnect lands while the timer is already firing.
- `Shutdown()` cancels all pending holds; the saver's final flush persists their state.

## 2026-08-04 — Death events

**Decision.** `input.Handler` gained a `SetDeathHandler(DeathFunc)` hook, called when
`combat.HandleDeath` reports a fresh kill. `Server.onEntityDeath` maps entity type to an
event id — `player` → `player_death`, `boss` → `boss_killed`, everything else is dropped —
and publishes a JSON `DeathPayload{VictimID, VictimType, KillerID, MapID, ServerID}` to the
`events:game` stream via `events.Publisher`.

**Rationale.** `events.Publisher` and `ServerOpts.EventStream` existed but had zero callers;
death is the first event other services (Nakama rewards, world-chat announcements,
leaderboards) actually need.

**Trade-offs.** The publish runs on its own goroutine because the hook fires *inside* the
tick loop and the Redis stream backend does network I/O — the tick must never block on it.
The cost is that event ordering across two deaths in the same tick is not guaranteed.
Mob/NPC deaths are deliberately not published: they are high-frequency and no consumer
needs them yet.

## 2026-08-04 — Input tick acknowledgement (partial)

**Decision.** `InputMessage.Tick` is now read. `Handler.ProcessInput` stores it on
`game.Entity.LastInputTick` monotonically (a stale or replayed frame never lowers it) and
`World.LastInputTick(userID)` exposes it.

**Rationale.** Client-side prediction needs to know which input the server has consumed
before it can reconcile (rewind/replay). Server-side tracking is the half that belongs to
this module.

**Trade-off — not yet on the wire.** `messages.SnapshotMessage` has only `{Tick, Entities}`
and `EntitySnapshot` has no per-player ack field; `shared` is owned by another agent, so
nothing was added there. Until `SnapshotMessage` carries e.g. `AckInputTick`, the value is
only observable server-side and client reconciliation remains blocked.

## 2026-08-04 — Player state persisted to PostgreSQL (opt-in)

**Decision.** `cmd/gameserver` selects the `storage.PlayerStore` implementation
at boot: `pgstore.PostgresPlayerStore` when `GAME_DB_URL` (or `--game-db-url`)
is set, otherwise the in-memory store. The chosen backend is logged, with the
DSN password redacted.

**Opt-in, not default.** Unit tests, the integration suite and a bare `go run`
must keep working with no database around, so an unset `GAME_DB_URL` keeps the
old in-memory behaviour. But a *set-yet-broken* DSN is fatal: the process runs
`Migrate` and exits non-zero rather than silently accepting players whose
progress will be dropped on shutdown.

**Migrate at boot, from the game server.** The DDL is idempotent, so every
replica applying it concurrently is safe, and it means a pod scheduled against a
freshly provisioned database is self-sufficient — no init-container ordering.

**Nothing in the tick loop changed.** `persistence.Saver` already talks to the
interface and runs off-tick every 30s; swapping the implementation adds network
I/O only on that goroutine. `Server.loadOrCreatePlayer` treats any load error as
"new player", which now includes a transient database error — a player could be
respawned at defaults during an outage. Distinguishing `storage.ErrNotFound`
from real failures (and refusing the join on the latter) is the follow-up.



## 2026-08-04 — Opt-in KCP listener, transport published in the registry

`Server.Run` listens through `shared/transport.Listen(kind, addr)` instead of
`net.Listen("tcp", …)`; the kind comes from `ServerOpts.Transport`
(`--transport` / `GAMESERVER_TRANSPORT`, default `tcp`). `handleConnection`,
the join-token handshake, the tick loop and the snapshot writer take a
`net.Conn` and are byte-identical on both transports — the length-prefixed
codec in `shared/messages` does not care what carries the bytes.

`register()` publishes the normalized kind as `storage.ServerInfo.Transport`, so
the gateway can tell the client what to dial without any out-of-band config.
This is the only reason the field exists on the registry entry: the game server
is the only component that knows the truth.

**Disconnect detection changes on KCP.** UDP has no FIN: a client that drops its
socket is not observable, so `ReadLoop` does not return and the final `SaveAll`
does not run until the reconnect hold expires. That is exactly what the hold
window (`EntityHoldTTL` / `DungeonHoldTTL`) already exists for, so behaviour
degrades to "state saved when the hold expires" rather than breaking — but
clients should send `MsgDisconnect` before closing, which the smoke test and the
integration suite both do. A KCP-level idle timeout is the follow-up if the hold
window proves too coarse for dungeon pod reclaim.