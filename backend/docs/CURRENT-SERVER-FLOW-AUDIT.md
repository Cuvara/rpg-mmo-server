# Current Server Architecture & Runtime Flow — Audit

**Date:** 2026-08-17 · **Scope:** read-only audit of what is implemented today.
No code was changed. No target architecture is proposed here.

Every claim below is anchored to a file:line in `backend/`. Where a document in
the repo disagrees with the code, the code is reported and the stale doc is
named in §9.

---

## 1. Processes that exist at runtime

| Process | Language | Entry point | Listens | Role |
|---|---|---|---|---|
| Nakama | Go plugin on `heroiclabs/nakama:3.40.0` | `nakama/main.go` | 7349/7350/7351, metrics 9100 | Meta: device/email auth, `gateway_token` RPC, economy/leaderboard |
| Gateway | Go | `gateway/cmd/gateway/main.go` | `:8000` (TCP default, KCP opt-in), metrics `:9102` | Auth + map assignment **only**. Redirector, never a data-path proxy |
| Game server | C# .NET 10 (NativeAOT) | `gameserver-dotnet/GameServer/Program.cs` | `:9000` (TCP; KCP implemented), metrics `:9101` | Simulation, snapshots, persistence, self-registration |
| Redis | 7.4-alpine | — | 6379 | Sessions, server registry, event stream, kick pub/sub |
| Postgres (meta) | 16.4 | — | 5432 | Nakama-owned |
| Postgres (game) | 16.4 | — | 5433 | `player_states`, written only by the game server |
| LGTM (Grafana/Prom/Loki/Tempo) | — | — | 3000/4317/4318/9090 | `monitoring` compose profile |

Composition: `deploy/docker-compose.yml`. Realtime services are behind the
`realtime` profile; `docker-compose.override.yml` remaps them onto canonical
ports (`8000`, `9000`) and adds a **second game server for `map_02`** on `9002`.

There is **no Kubernetes in the deploy path.** Dev, staging and production all
run `DEPLOY_MODE=containers` under docker compose. Agones/k3s manifests exist
(`deploy/agones/`, `deploy/k3s/`) and code paths exist on both sides, but they
are a parallel, opt-in path — see §7.

---

## 2. End-to-end flow

### 2.1 Client start → authentication (meta hop)

1. Client authenticates to **Nakama** over HTTP (device/email/social) and gets a
   Nakama session token.
2. Client calls the Nakama RPC `gateway_token`
   (`nakama/auth/token.go:14`, handler at `:56`).
   - Caller must be authenticated (`RUNTIME_CTX_USER_ID`), else `ErrUnauthenticated`.
   - Per-user token-bucket rate limit before any work (`allowGatewayToken`, keyed
     on user id, not IP — `nakama/auth/ratelimit.go`).
   - Signs HS256 with the **current** key of the `JWT_SECRET` keyring
     (`jwt.ParseKeyring` → `keys.SignWithServer`, `token.go:39-44`). Optional
     `server_id` claim pins the token to one game server.
   - Returns `{token, user_id, expires_in}`.

Note: Nakama also signs its own session tokens with `session.encryption_key ==
JWT_SECRET` in compose (`docker-compose.yml:103`), so a Nakama session token
verifies against the same keyring. The dedicated `gateway_token` RPC is the
intended path.

### 2.2 Client → Gateway: `MsgAuth`

- Transport: `transport.Listen(kind, addr)` — TCP default, KCP with
  `--transport=kcp` and PSK from `TRANSPORT_KEY` (`gateway/server/server.go:303`).
- Admission control happens **before** any allocation: per-source-IP token bucket
  right after `Accept` (`server.go:333`); rejected connections cost one map lookup
  and a `Close`.
- Unauthenticated sockets carry a hard 30s read deadline
  (`authTimeout`, `server.go:434`, applied at `:462`). Cleared on successful auth.
- Per-connection inbound-frame limiter; on trip the gateway replies
  `MsgAuthResp{OK:false,"rate limited"}` and closes (`server.go:490-508`).
- `handleAuth` (`server.go:600`):
  1. `VerifyClientJWTKeyring` against the `JWT_SECRET` keyring (every key
     verifies, first key signs). Local verification — **no Nakama roundtrip**.
  2. **Duplicate-login detection** (`server.go:621-641`): if a session already
     exists for the user, and it belongs to this gateway → kick the local socket
     (`MsgKick` then `MsgDisconnect`, both `reason=duplicate_login`,
     `sendKickAndClose:736`). If it belongs to another gateway → `PublishKick`
     over the Redis pub/sub channel `gateway:kick`
     (`shared/constants/keys.go`, `GatewayKickChannel`).
     ⚠️ The kick publisher/subscriber are `Option`s (`WithKickPublisher` /
     `WithKickSubscriber`) and **`cmd/gateway/main.go` never sets them** — the
     cross-gateway path is dead code in the shipped binary; only the same-gateway
     kick works today.
  3. `CreateSession` writes `session:{user_id}` as JSON
     (`gateway/session/manager.go:51`) with `SessionTTL = 1h`
     (`shared/constants/ttl.go`).
  4. Replies `MsgAuthResp{OK, UserID}`.

Only three message types are accepted after auth: `MsgAuth`, `MsgEnterWorld`,
`MsgDisconnect` (plus `MsgPing`/`MsgPong`, handled before the session check so a
Redis blip cannot break heartbeats — `server.go:513-520`). Anything else is
logged once per connection and dropped (`server.go:534`).

**Session check per frame** (`checkSession:550`): reads the session store, and
on a **store error fails OPEN** (`server.go:566-571`) — the connection continues
and the error is logged. Only a genuine `ErrNotFound` clears the identity and
rejects. Sliding TTL: every accepted frame refreshes the 1h TTL.

### 2.3 Client → Gateway: `MsgEnterWorld{MapID}` → find game server

`handleEnterWorld` (`server.go:788`) → `transfer.AssignMapKeyring`
(`gateway/transfer/map_assign.go:36`):

1. `RegistryService.FindServer(ctx, mapID)` (`gateway/registry/registry.go:202`):
   - `FindByMapID` with exponential-backoff retry (3 retries, 1s→2s→4s, total cap
     10s; only transient errors retried — `registry.go:104`).
   - If more than one live server serves the map → **warn only**, no enforcement
     ("the world is split", `registry.go:214`). This is ADR-2's MVP invariant:
     one live server per `map_id`, unenforced.
   - Selection: least `PlayerCount` among servers with `PlayerCount < Capacity`,
     ties broken by lowest `ServerID` (deterministic, `registry.go:222-232`).
   - If none has room **and** an allocator is configured → `AllocateServer`, then
     `Register` the result. Otherwise → `ErrNoServerAvailable`.
2. `GenerateJoinTokenKeyring(userID, serverID, joinKeys)`
   (`gateway/transfer/join_token.go:28`): HS256 over the **`JOIN_TOKEN_SECRET`**
   keyring — deliberately not `JWT_SECRET` — TTL **30s**
   (`constants.JoinTokenTTL`). `serverID` is mandatory; an unpinned token could be
   replayed against any pod.
3. Gateway updates the session with `server_id` + `map_id`
   (`UpdateSession`, `server.go:834`) and replies
   `MsgEnterWorldResp{ServerAddr, JoinToken, Transport}`.

`ServerAddr` is whatever the game server advertised in the registry
(`GAMESERVER_PUBLIC_ADDR`), returned **verbatim**. Program.cs warns at startup
when that value is hostless (`Program.cs:154-171`).

Client-facing errors are sanitised: `no server available for map` /
`not implemented` / `internal error` (`server.go:867-882`).

### 2.4 Client → Game server: `MsgJoinToken` (direct hop, gateway not involved)

`GameServerHost.HandleConnectionAsync` (`gameserver-dotnet/GameServer/Server/GameServer.cs:549`):

1. Read exactly one frame; it must be `MsgJoinToken`, else error + close (`:568`).
2. Verify against the `JOIN_TOKEN_SECRET` keyring (`_joinKeys.Verify`, `:579`).
3. **`sid` claim must be non-empty and equal this server's id** (`:588`) — no
   empty-claim bypass.
4. **JTI replay protection**: `JtiTracker.TryConsume` — a join token is
   single-use (`:597`, `Server/JtiTracker.cs`).
5. Capacity check against `_connections.Count >= Capacity` (`:604`).
6. Cancel any pending reconnect hold for the user (`:614`).
7. Entity acquisition (`:622`): reuse the live entity if present; otherwise load
   from `IPlayerStore` and resolve spawn via `PlayerSpawn.Resolve` — saved
   coordinates are only reused when the saved row belongs to **this** map; HP and
   MaxHP always carry across (`Persistence/PlayerSpawn.cs`).
8. Register the connection, `PlayerJoined()` metric, fire-and-forget
   `RegistrationService.NotifyPlayerCountChanged()`, and
   `NotifyAgonesAllocatedOnce()` (§7).
9. Reply `MsgJoinTokenResp{Ok, UserId, TickRate}` where `TickRate` is the
   **critical/movement** Hz, so the client predicts at the rate the server
   integrates at (`:698`).
10. Start `WriteLoopAsync` / `ReadLoopAsync` / `HeartbeatLoopAsync`.

Heartbeat: `MsgPing` every 10s, close after 30s with no pong
(`Net/Connection.cs:256,259,443`).

### 2.5 Realtime gameplay

**Threading model.** `EcsWorld` (Arch ECS) is guarded by a single
`ReaderWriterLockSlim` (`World/EcsWorld.cs:95`); inputs land in a queue under a
separate `_inputLock` (`:635`). Network threads push inputs; the tick thread
drains them.

**Tick loop** (`Server/TickLoop.cs`), three configurable rates on one integer
timeline (ADR-13, `SIM_CRITICAL_HZ` / `SIM_WORLD_HZ` / `SIM_BACKGROUND_HZ`,
default **60 / 15 / 5** in compose and the fleet manifest):

1. `ApplyStructuralChanges()` — deferred spawns/despawns applied at one explicit
   point (Arch `CommandBuffer` is banned under NativeAOT, ADR-11).
2. **Critical group, every base tick**: drain inputs, rebind stale handles,
   **coalesce to the newest input per entity per tick** (`_newestInputIndex`,
   `:265`) so N packets/tick ≠ N movement steps, then one write scope for
   `ProcessInput` + `ApplyHeldMovement`.
3. `ISimulationPhase.Tick` — runs whichever declared systems are due
   (enemy AI lives here, `Scaffolding/EnemySpawner.cs`).
4. **Snapshot broadcast, gated on the WORLD rate** (`:343`) — not the base rate.
   Phase A gathers each viewer's AOI + anchor under **one** read lock
   (`_world.ReadAll(_gatherViews)`); encoding/serialising happens on each
   connection's own write task, off the tick thread.
5. Overload policy: a tick over budget increments `RecordTickOverrun`; more than
   `MaxLagTicks = 8` behind → **drop the backlog and resynchronise** rather than
   chase it (`:149`, `:204`). Simulation time then lags wall time — bounded and
   measurable rather than a death spiral.

**Snapshots** are delta-encoded per connection with a full keyframe on join, on
`MsgResync`, and every `KeyframeInterval` snapshots (default 30). Each carries
`ack_tick` = newest accepted input tick for that player (client reconciliation
anchor). Wire encoding is Protobuf with an entity-type enum + entity-id
interning; legacy JSON is still accepted and distinguished by the first body byte
(ADR-9). AOI is still **brute-force** — a uniform grid was built and measured
2.8× slower (BENCHMARK.md Part V).

**Persistence** (`Persistence/AsyncSaver.cs`): async batch save every 30s, never
on the tick thread; plus a forced save on map transfer and before hold expiry.
Accepted crash-loss window ≤30s of position/HP (ADR-6). Store is
`PostgresPlayerStore` when `GAME_DB_URL` is set — a configured-but-unreachable DB
is **fatal at startup** (`Program.cs:293-298`), it never degrades to memory.
`--migrate-only` applies migrations and exits (`Program.cs:179`).

**Cross-server events**: `OnEntityDeath` builds a `DeathPayload` and publishes to
`IEventStream` — which is **always `NoopEventStream`** (`Program.cs:379`). The
gateway's relay consumes `events:game` via Redis Streams consumer group + ACK
(`gateway/events/relay.go`) but nothing feeds it, and even if it did the relay can
only log/count: `shared/messages` has no client-facing `MsgEvent`
(`server.go:267`). ADR-5.

**Nakama rewards**: on a player killing a mob, fire-and-forget
`RewardKillAsync` + `SubmitKillAsync` over Nakama's HTTP API with
`runtime.http_key` (`GameServer.cs:974`, `Nakama/NakamaClient.cs`).

### 2.6 Server registration & discovery

The game server registers **itself** — there is no external registration step.

- `RedisServerRegistry.ConnectAsync` with `AbortOnConnectFail=false`; a Redis
  outage at boot is **non-fatal**, the server runs unregistered and logs loudly
  (`Program.cs:330-338`).
- `RegistrationService` (`Registry/RegistrationService.cs`): register, then
  heartbeat every `TTL/3` = **5s against a 15s TTL**
  (`RegistryDefaults.HeartbeatTtl`, matching Go's `constants.ServerHeartbeatTTL`).
- **Every heartbeat is also a repair**: if `HeartbeatAsync` reports the entry is
  gone (Redis wipe, failover, TTL lapse) the service re-registers instead of
  erroring (`:130-140`). Self-heals within one interval.
- Player count is pushed on change, fire-and-forget off the join/leave path
  (`NotifyPlayerCountChanged:207`) so Redis never blocks a join.
- Registration happens **after** `ReadyAsync()` and after the listener is bound
  (`GameServer.cs:362-377`); shutdown reverses the order (§2.8). ADR-14 decisions 2-3.

### 2.7 Leaving the world

Three distinct exits:

| Exit | Path | Entity |
|---|---|---|
| **Socket drop / crash** | `HandleConnectionAsync` `finally` → `OnPlayerDisconnected` (`GameServer.cs:854`) | **Held** for `HoldTtl` (30s map / 60s dungeon), then saved and removed |
| **Explicit `MsgTransferMap`** | `HandleTransferMapAsync` (`:790`) | Saved immediately, **removed at once** (intentional leave, nothing to reconnect to), connection closed |
| **Server shutdown** | `ShutdownAsync` → `DrainClientsAsync` | All holds cancelled, `MsgDisconnect{reason:"server_shutdown"}` broadcast, 2s grace |

Hold mechanics (`:854-930`): one `CancellationTokenSource` per user in a
`ConcurrentDictionary`; a superseding hold cancels+disposes the previous one; the
expiry task **saves before removing** (otherwise up to 30s of play is discarded),
and claims the removal atomically via `TryRemove(KeyValuePair)` so a reconnect
during the save cannot have its freshly reattached entity deleted.

Map transfer is **client-driven and gateway-free** (`transfer/dungeon.go:9-18`):
client → `MsgTransferMap` to current server → server saves/responds/removes/closes
→ client sends a fresh `MsgEnterWorld` to the gateway → normal assignment.

**Gateway side of leaving**: `handleConn`'s defer calls `cleanupSession`, which
`ClearIdentity()` (atomic, exactly one winner) and `DestroySession` — a dropped
socket must not leave a session record behind (`server.go:439-486`).
Note the gateway session is 1h TTL and independent of the gameplay socket; the
gateway is not told when a player leaves the game server.

### 2.8 Game server shutdown / cleanup

`Program.cs` registers `PosixSignalRegistration` for **SIGINT and SIGTERM** with
`ctx.Cancel = true` (`:414-422`) — deliberately not `AppDomain.ProcessExit`, which
killed the process mid-drain and lost the final save.

`ShutdownAsync` (`GameServer.cs:416`) is idempotent and concurrency-safe
(`Interlocked.Exchange` on `_shutdownStarted`; later callers await the same
`TaskCompletionSource`). Order:

1. Cancel the CTS · 2. dispose the listener (also tears down KCP sessions) ·
3. drain and dispose every reconnect hold · 4. `DrainClientsAsync` — broadcast
`MsgDisconnect{server_shutdown}`, wait 2s · 5. `CloseAll` connections ·
6. **`RegistrationService.DeregisterAsync()`** — leave the registry *before* the
final save, so the gateway stops handing out this address immediately rather than
waiting out the 15s TTL · 7. `_saver.SaveAllAsync()` final save ·
8. `_agonesSdk.ShutdownAsync()`.

Then `Program.cs`'s `finally`: `server.DisposeAsync()` → dispose Agones client →
dispose the Postgres pool, in that order (`:456-463`).

**Gateway shutdown** (`main.go:287-299`): SIGINT/SIGTERM → `gw.Shutdown()` (close
listener, close every conn, stop relay/kick subscriber) → stop metrics listener →
close Redis stream + client.

---

## 3. Security posture (as implemented)

- **Three independent secrets**, each with a stated dev default:
  `TRANSPORT_KEY` (KCP PSK, empty = plaintext), `JWT_SECRET` (Nakama→gateway auth),
  `JOIN_TOKEN_SECRET` (gateway→game server). All three are comma-separated
  **rotation keyrings**: first key signs, every key verifies.
- `JOIN_TOKEN_SECRET` is **required on both sides** — gateway exits 1
  (`main.go:99-104`), game server exits 2 (`Program.cs:249-255`). Reusing
  `JWT_SECRET` warns on both.
- Join tokens: 30s TTL, `sid`-pinned, **single-use via JTI**.
- Rate limits: per-IP accepts, per-connection frames (gateway), per-user
  `gateway_token` (Nakama).
- Tokens are never logged, by construction (`server.go:594-599`, `:783-787`).
- **KCP is not reachable end to end for gameplay** in the ADR-8 summary's words,
  but the C# side *does* now have a KCP listener
  (`Net/Transport/Kcp*.cs`, with `KcpInteropTests`) — see §9, item 4.

---

## 4. Observability

- Gateway: Prometheus on `:9102`, `/metrics` + `/healthz`, with **readiness
  checks** registered per backend (Redis ping doubles as the `gateway_redis_up`
  sampler, `main.go:182-187`). The metrics listener starts **before** backend
  wiring so a crash-looping gateway is still probeable.
- Game server: OTel→Prometheus on `:9101`, plus a `/status` JSON endpoint carrying
  tick, players online, entity count, `enemies_alive`, redis/postgres connectivity,
  uptime (`Program.cs:430-441`). The Unity DOTS sample polls it.
- Degradation is explicit: `RelayUp` gauge, `gateway_stream_group_loss_total`
  sampled every 10s, `SessionCheckStoreError` counter.
- Dashboards: `deploy/monitoring/dashboards/rpg-gameplay.json`.

---

## 5. Failure behaviour summary

| Dependency down | Behaviour |
|---|---|
| Redis, gateway side | Session checks **fail open** (connection continues); relay retries with 1s→30s backoff and the gateway serves degraded; registry lookups retry 3× then error → `no server available for map` |
| Redis, game server side | Boot: non-fatal, runs unregistered (gateway can't find it). Running: heartbeat logs and retries; gameplay untouched |
| Postgres (game), boot | **Fatal** — refuses to start rather than silently using memory |
| Nakama | Gateway has **no runtime dependency** on it (JWT verified locally). Game server reward calls are fire-and-forget |
| Game server crash | Registry entry expires in ≤15s; nothing replaces it (no orchestration in the deploy path) |
| Event stream | Nothing published (Noop); relay consumes an empty stream |

---

## 6. What is deliberately absent

- **Gameplay content.** `Shared.GameLogic` carries one movement rule and one
  combat rule; enemy AI lives in a directory named `Scaffolding/`.
- **Dungeon instancing.** `--mode=dungeon` changes exactly one thing: the hold
  window, 60s instead of 30s (`Program.cs:369`). `StubDungeonTransfer` returns
  `ErrNotImplemented` (`gateway/transfer/dungeon.go:30`). No checkpointing, no
  allocate-per-party, no instance lifecycle.
- **Client-facing events.** No `MsgEvent` type exists.
- **Cross-gateway duplicate-login kick.** Implemented but not wired (§2.2).
- **Matchmaking / social** Nakama modules: not started.

---

## 7. Agones / k3s — actual state

Both halves of the integration now **exist in code**:

- **Gateway**: `AgonesAllocator` POSTs to the aggregated allocation API
  (`/apis/allocation.agones.dev/v1/namespaces/%s/gameserverallocations`) via a
  `client-go` REST client, deliberately not importing the Agones clientset
  (`registry/agones_allocator.go`). Selected with `--allocator=agones` /
  `ALLOCATOR=agones`; **default is `none`** (`main.go:362-365`), in which case an
  unserved map is simply an error.
- **Game server**: `HttpAgonesSdk` speaks the sidecar's **HTTP** interface on
  `localhost:9358` — four POSTs (`/ready`, `/health`, `/allocate`, `/shutdown`),
  no gRPC dependency (`Agones/AgonesSdk.cs:66`). Selected with `--agones` /
  `AGONES_ENABLED=true`; **default is `NoopAgonesSdk`**. No method ever throws.
  `Ready` on listen; health loop every 2s **only when `IsEnabled`**; `Allocate`
  reported once, off the join critical path (`GameServer.cs:504`), never
  un-reported (Agones has no un-allocate); `Shutdown` on drain.

What is **not** true today:

- Agones is **not in the deploy path**. Dev/staging/prod are docker compose.
- `deploy/agones/fleet-map-dotnet-dev.yaml` still carries `health.disabled: true`
  with a comment stating the C# SDK is a no-op — that comment is now false, but
  the flag is intentionally left for a staged rollout (ADR-14 stage 4).
- ADR-14 is headed **"not yet implemented. Nothing in this ADR has shipped."**
  Stages 1-3 have since shipped (see `gameserver-dotnet/CHANGELOG.md`
  *Unreleased → Real Agones SDK over the HTTP sidecar*). The ADR header is stale.
- Stage 5 — an end-to-end allocation from `MsgEnterWorld` to a joined client —
  has **never been demonstrated**. No C# server has been observed reporting Ready
  to a real sidecar in this project.
- The only cluster Agones has run on is `docker-desktop`, and the fleets recorded
  there (`map-servers-dev`, `dungeon-servers-dev`) run the **deleted Go** game
  server image.

---

## 8. Invariants a target architecture must not break

1. **ADR-3** — the gateway is never in the gameplay data path. It accepts exactly
   three message types.
2. **ADR-2** — one live server per `map_id`; two instances = two disconnected
   copies of the world with no handoff. Warned, not enforced. The allocator's
   "allocate a second instance for a full map" branch **is** the violation, and is
   only reachable once Agones works end to end.
3. **ADR-1** — one writer per datum. Agones owns pod lifecycle; Redis owns the
   `map_id → address` lookup; neither reads the other's answer.
4. **ADR-4** — Redis is a system of record, not a cache: `maxmemory-policy
   noeviction` is explicit. Evicting a registry hash silently removes a live
   server from matchmaking.
5. **Secret split** — a compromised game-server pod must not be able to forge
   client auth tokens.
6. **Join token is single-use, 30s, server-pinned.** Any redirect/retry design
   must mint a fresh token.
7. **Snapshot cadence is the world rate, not the base rate.** Sending every base
   tick quadruples per-client bandwidth past ADR-7's <50 KB/s mobile budget and
   silently redefines the keyframe interval (which counts snapshots).
8. **`Shared.GameLogic` is compiled into the Unity client.** Changing it is a
   two-repo change; the client pins a resolved commit in `packages-lock.json`.

---

## 9. Documentation that disagrees with the code

| Doc | Claim | Reality |
|---|---|---|
| `docs/ARCHITECTURE-DECISIONS.md` ADR-14 header | "not yet implemented. Nothing in this ADR has shipped" | Stages 1-3 shipped: `HttpAgonesSdk` exists, is wired, is tested |
| `deploy/agones/fleet-map-dotnet-dev.yaml:36-52` | "The C# server's Agones SDK is a no-op … `--agones` parses, logs a warning and changes nothing" | False since the SDK landed; `disabled: true` is still correct as a staged-rollout choice, but for a different reason |
| `deploy/docker-compose.yml:226-228` | "the C# arg parser only matches SPACE-separated flags (`--addr :9000`, not `--addr=:9000`)" | `Program.cs:491` handles the `=` form |
| ADR-8 summary row | "KCP is **not** reachable end to end — the C# game server has no KCP" | The C# server has `KcpListener`/`KcpSession`/`KcpCrypto` and `KcpInteropTests` against the Go probe (`interop/kcpprobe`) |
| `docs/CORE_FLOW.md` | pre-C#-migration diagrams | Repo already flags it as partly stale |
| Root `CLAUDE.md` / ADR-7 | 150 players per game server | Explicitly retracted; the ceiling is **unknown**, blocked on a separate load-generator machine |

Two further code-level observations, reported for the reviewer, not fixed:

- `gateway/cmd/gateway/main.go` never calls `WithKickPublisher` /
  `WithKickSubscriber`, so cross-gateway duplicate-login kick is unreachable in
  the shipped binary despite being implemented and documented.
- `GameServer.cs:988 ParseAddr` is dead code (the transport factory does its own
  parsing).

---

## 10. Flow diagram (as implemented)

```
Unity client
  │ 1. HTTP  authenticate (device/email)                    ┌──────────┐
  ├────────────────────────────────────────────────────────►│  Nakama  │──► Postgres (meta)
  │ 2. HTTP  RPC gateway_token  → {token,user_id}           └──────────┘
  │◄────────────────────────────────────────────────────────
  │
  │ 3. TCP/KCP :8000   MsgAuth{JWT}                          ┌──────────┐
  ├─────────────────────────────────────────────────────────►│ Gateway  │
  │◄─── MsgAuthResp{OK,UserID}       session:{uid} SETEX 1h  │  (Go)    │──┐
  │ 4. MsgEnterWorld{MapID}                                  └──────────┘  │ Redis
  │           FindServer(map) → least-loaded, cap-checked                  │ servers:{map}
  │           mint join token (JOIN_TOKEN_SECRET, 30s, sid, jti)           │ session:{uid}
  │◄─── MsgEnterWorldResp{ServerAddr, JoinToken, Transport}                │ events:game (unfed)
  │                                                                        │
  │ 5. TCP/KCP :9000  MsgJoinToken{token}    ┌────────────────────┐        │
  ├─────────────────────────────────────────►│  Game server (C#)  │────────┘ self-register
  │◄─── MsgJoinTokenResp{Ok,UserId,TickRate} │  60Hz critical     │          + heartbeat 5s/TTL 15s
  │                                          │  15Hz world+snap   │
  │ 6. MsgInput  (per client tick) ─────────►│   5Hz background   │──► Postgres (game)
  │◄─── MsgSnapshot (delta, world rate) ─────│  Arch ECS + RWLock │     async save 30s
  │     MsgPing/MsgPong 10s / 30s timeout    └────────────────────┘
  │
  │ 7a. socket drop  → entity HELD 30s (60s dungeon) → save → remove
  │ 7b. MsgTransferMap → save → remove now → client re-enters via gateway
  │ 7c. SIGTERM → drain(MsgDisconnect,2s) → deregister → final save → Agones Shutdown
```

---

*Read-only audit. No files under `gateway/`, `gameserver-dotnet/`, `shared/`,
`nakama/` or `deploy/` were modified.*
