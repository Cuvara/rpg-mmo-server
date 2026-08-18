# Architecture Decisions

Responses to seven architecture criticisms of this design. Each section follows the
same shape: **Context** (the criticism), **Current state** (what the code actually
does, with `file:line` evidence read out of the tree — not from older docs),
**Decision**, **Consequences**, and **Follow-up work** sized S/M/L.

Sizing key: **S** = under a day, single module. **M** = a few days, one module plus
its tests and docs. **L** = a week or more, or cross-module/coordination-heavy.

Verified against `develop` at commit `3ca99b3` on 2026-08-05. Where this document
and `backend/docs/CORE_FLOW.md` disagree, this document is newer — CORE_FLOW was
written against the deleted Go game server and much of its §6 "Known gaps" list is
now stale (see ADR-3 and ADR-5).

---

## ADR-1 — Data ownership: one writer per datum

### Context

The criticism: it is not clear which store owns which data, so the same fact could
be written from two places and diverge.

### Current state

Four stores are in play. Interfaces live in `backend/shared/storage/interfaces.go`:
`PlayerStore` (:56-60), `SessionStore` (:63-70), `ServerRegistry` (:77-87),
`EventStream` (:94-98).

| Datum | Store | Writer | Evidence |
|---|---|---|---|
| Accounts, wallet, social, leaderboard | Nakama's own PostgreSQL (`postgres` service) | Nakama, via its runtime storage API | `backend/nakama/auth/profile.go:36-37,47,72` (`nk.StorageRead`/`StorageWrite`) |
| `player_states` (position, HP, map) | Game-state PostgreSQL (`postgres-game` service) | **gameserver-dotnet only** | `GameServer/Persistence/AsyncSaver.cs:88-111` → `PostgresPlayerStore.cs:145-168`; schema `pgstore/schema.sql:12-23` |
| `session:{user_id}` | Redis (TTL 1h) | **gateway only** | `gateway/session/manager.go:34-40,55-60,62-68` |
| `servers:id:{id}`, `servers:map:{map}` | Redis (TTL 15s) | **gameserver-dotnet** (self-registration + heartbeat); the gateway also registers Agones-allocated servers | `GameServer/Registry/RedisServerRegistry.cs`, `GameServer/Registry/RegistrationService.cs`; `gateway/registry/registry.go:85,106,111-112` |
| `events:game` | Redis Streams | nothing live (see ADR-5) | `gateway/events/relay.go:16` subscribes; no live publisher |
| Live world (entities, mobs, pending input) | C# process memory | gameserver-dotnet | `GameServer/World/GameWorld.cs:22-27` |

Three findings the criticism is right about:

1. **The server registry no longer has a shell-script writer.** It used to: the
   gateway wrote through the typed interface (`registry.go:85`, for Agones-allocated
   servers) while `scripts/register-gameserver.sh` wrote the same keys with raw
   `redis-cli HSET`/`SADD`/`EXPIRE`, because the C# server had no Redis client. That
   script's own header said "Delete this script the day the C# server registers
   itself" — which has now happened, and it is deleted. The C# server owns its own
   entry through `GameServer/Registry/`, using the same key layout and the same
   `ServerHeartbeatTTL` the Go code assumes.
2. **Nakama never touches the game-state DB.** Every hook takes `db *sql.DB`
   (`backend/nakama/main.go:23`, `auth/token.go:49`) and never dereferences it.
   The two PostgreSQL instances are genuinely separate.
3. **`backend/shared/storage/pgstore/` is orphaned.** No Go binary imports it; the
   C# server has its own reimplementation in `PostgresPlayerStore.cs`. The two
   schemas are kept byte-identical by a test, but nothing enforces that at build time.

Also dead: `constants.PlayerLocationKey` (`shared/constants/keys.go:7`) has no
consumer anywhere — the `player:{user_id}:location` index described in
`backend/gateway/CLAUDE.md:37` was never built.

### Decision

**Exactly one writer per datum, enforced by convention and documented here.** The
ownership matrix above is normative. Specifically:

- Player gameplay state is owned by the game server that currently hosts the player.
  No other process writes `player_states`.
- Session records are owned by the gateway. Game servers never read or write them.
- The registry is owned by *the game server that the entry describes* — which today
  means the deploy script acting as its proxy. The gateway may only write entries
  for servers it allocated itself via Agones.
- Meta data (accounts, wallet, social) is owned by Nakama and reached only through
  Nakama's API, never by direct SQL from our code.

**Recovery source-of-truth per datum**: `player_states` in PostgreSQL is the only
recoverable gameplay datum. Sessions and the registry are transient and are
rebuilt by clients re-authenticating and servers re-registering; they are *not*
backed up. Live world state is not recoverable (ADR-6).

### Consequences

- The registry's two-writer situation is accepted **only** as a stopgap. It is safe
  today because the script and the gateway write disjoint entries in practice (the
  script writes the one static dev server; the gateway only writes servers it
  allocated), but nothing enforces that disjointness.
- Because Redis holds no gameplay state, a total Redis loss costs sessions and the
  registry — recoverable by reconnect and re-registration — not player progress.
- Keeping two PostgreSQL instances means no cross-database transaction is possible
  between meta and gameplay. Any "spend gold, grant item in world" flow must be
  built as an idempotent two-step, never assumed atomic.

### Follow-up work

- **S** — Delete `constants.PlayerLocationKey` or implement the location index.
- **S** — Delete the orphaned Go `shared/storage/pgstore/` package, or add a build-time
  assertion tying its schema to the C# one instead of relying on a test.
- ~~**M** — Give the C# server a Redis client so it self-registers and heartbeats,
  then delete `scripts/register-gameserver.sh`.~~ **Done.** `GameServer/Registry/`
  registers on startup, heartbeats every 5s against a 15s TTL and deregisters on
  shutdown; the script is deleted. This collapsed the registry to one writer per
  server and fixed the dead heartbeat in ADR-2.

---

## ADR-2 — Map server sharding

### Context

The criticism: one server per `map_id` is a scaling ceiling and a single point of
failure, and there is no cross-map handoff.

### Current state

Neither registry implementation enforces one server per map. `Register` is keyed by
`ServerID` and simply adds the id to a per-map set:

- Memory: `shared/storage/memory.go:149-154`, `FindByMapID` linear scan
  (`memory.go:186-196`), returns **all** matches.
- Redis: `redisstore/registry.go:69-86` (`HSET` + `SADD`), `FindByMapID` via
  `SMEMBERS` + `HGETALL` (`registry.go:135-163`), returns **all** matches.

`FindByMapID` returns `[]ServerInfo`, and the gateway selects the least-loaded
server with capacity (`gateway/registry/registry.go:55-94`) — **not** first-fit as
CORE_FLOW.md:88 claims.

The important finding: **the allocator deliberately creates a second server for a
full map.** When no server has capacity and Agones is enabled,
`registry.go:77-91` allocates a new instance and registers it under the *same*
`map_id`. So the system already produces multiple instances per map — and two
instances of one map are two disconnected copies of the world. Players on
different instances cannot see or fight each other, and there is no handoff
between them. Nothing warns about it.

This used to be compounded by a dead heartbeat: `ServerRegistry.Heartbeat`
(`redisstore/registry.go:90-99`) had **zero callers**, and the dev entry was
written once with a 1-hour TTL by a deploy script and never re-armed. That is
fixed — the C# server now re-arms its own 15s TTL every 5s
(`GameServer/Registry/RegistrationService.cs`), so a stale entry disappears in
seconds. The multi-instance hazard above is unchanged.

### Decision

**MVP policy: exactly one live game server per `map_id`.** A map's capacity cap is
the player ceiling for that map; when it is full, further joins are refused rather
than silently sharded onto a second instance. Horizontal splitting of a single map
is explicitly out of scope for the MVP.

Because the allocator can violate this invariant, the condition is **surfaced, not
enforced**: `FindServer` now logs a loud warning when a map resolves to more than
one live server (`gateway/registry/registry.go`, this PR). It is not a hard error
because failing the request would take a live map offline during a rolling
deploy, when two entries legitimately overlap for a few seconds.

Selection is now **deterministic**: ties on `PlayerCount` break on `ServerID`
(this PR). Previously ties resolved in Go map / Redis `SMEMBERS` order, which is
arbitrary — two equally-loaded servers could split a party across instances on
consecutive joins.

**The seam for later horizontal splitting** is `FindByMapID` returning a slice plus
`ServerInfo.MapID`. A future zone/shard model changes the *key* (`map_01#shard_2`)
and the selection function, without touching the wire protocol, the join-token
flow, or the game server — which never learns it is one shard among several.

### Consequences

- A map's ceiling is one process. At the documented 100-player default capacity
  (`GameServer/Program.cs`, `--capacity`) that is the per-map CCU cap.
- A map server crash takes that map down until Agones replaces the pod; players
  reconnect and reload from PostgreSQL, losing up to 30s (ADR-6).
- Running Agones allocation for *map* fleets is currently unsafe under this policy
  — it is what creates the second instance. Dungeon fleets are unaffected, since
  each dungeon instance is a distinct logical world by design.
- The dead heartbeat means a crashed server's registry entry lingers until its TTL
  expires (1 hour in dev), and the gateway keeps handing out join tokens for a
  server that is gone. Clients fail at the game-server dial step.

### Follow-up work

- **S** — Call `Heartbeat` from a ticker so dead servers leave the registry within
  `ServerHeartbeatTTL` (15s) instead of an hour. Blocked on the C# Redis client
  (ADR-1 follow-up) unless the deploy script re-arms it meanwhile.
- **M** — Decide the map-fleet allocator policy: either disable allocation for map
  fleets, or make allocation replace rather than supplement a full map's server.
- **L** — Zone/shard model with cross-instance handoff (interest-region transfer,
  shared entity ids, party co-location). Only worth doing with real CCU data (ADR-7).

---

## ADR-3 — Gateway is a redirector, not a router

### Context

The criticism: docs and diagrams describe the gateway as a UDP/KCP router that
forwards packets, but the code hands the client a game-server address and steps
out of the way. The contradiction is real and the docs are the wrong half.

### Current state

The gateway handles exactly three message types
(`gateway/server/server.go:225-241`): `MsgAuth`, `MsgEnterWorld`, `MsgDisconnect`.
`MsgInput` and `MsgSnapshot` are not cases — they hit `default` and are logged as
"unexpected message type". There is no proxy path in the module, and no
`router.go` exists despite `backend/gateway/CLAUDE.md:92` listing one.

`EnterWorldResponse` carries `ServerAddr`, `JoinToken`, `Transport`
(`shared/messages/messages.go:53-58`, populated at `server/server.go:354-358`).
The client then opens a **second, independent socket** straight to the game
server, proven in `integration_test/dotnet_interop_test.go:236-262`.

Security is not weakened by this: the join token is an HS256 JWT whose `sid` claim
is checked by the game server against its own id
(`gameserver-dotnet/GameServer/Server/GameServer.cs:225-233`), the `alg` header is
validated on both sides (`JwtValidator.cs:61`, `shared/jwt/jwt.go:65-86`), and the
token TTL is 30s (`constants.JoinTokenTTL`).

### Decision

**Keep the redirector model for the MVP, and fix the docs to match.** Rationale:

1. **No double hop.** Gameplay traffic is 10-15Hz bidirectional. Proxying would add
   a network hop to every input and every snapshot, in both directions, for zero
   gameplay benefit.
2. **The gateway is not on the critical path.** If it dies, players already in a
   world keep playing; only new logins and map entries fail. As a proxy it would
   be a hard SPOF for all gameplay traffic and would have to be scaled with CCU
   rather than with login rate.
3. **Security is preserved without proxying.** The `sid`-bound, 30-second join
   token is what authorizes the direct connection, so bypassing the gateway gains
   an attacker nothing — they still need a signed token naming that exact server.

Note the `sid` check is currently conditional: it is skipped when either the
server's configured id or the token's claim is empty (`GameServer.cs:225-228`).
That is a soft spot worth closing.

**Proxy mode is a documented future option, not a plan.** It becomes attractive
only if we need client IP hiding / DDoS shielding of game-server pods, or strict
egress control where pods must not be publicly dialable. The cost is a hop of
latency, gateway bandwidth scaling with CCU rather than logins, and the gateway
becoming a gameplay SPOF.

### Consequences

- Game-server pods must be directly reachable by clients. That is a real
  deployment constraint: public addresses or port-mapped node ports per pod.
- The client must implement two connections and handle the handoff, including the
  case where the game server refuses a stale token.
- Docs updated in this PR: root `CLAUDE.md`, `README.md`, `backend/TEAM.md`,
  `backend/gateway/CLAUDE.md`, `backend/gateway/docs/README.md`,
  `backend/gameserver-dotnet/docs/DESIGN.md`. Diagram labels in
  `IdeaATeckStack/RPG-MMO-indie.drawio` still say "UDP/KCP Router" and are listed
  as follow-up rather than hand-edited XML.

### Follow-up work

- **S** — Make the `sid` check unconditional: refuse a token with no `sid`, and
  refuse to start a server with no id, instead of skipping the comparison.
- **S** — Update the drawio diagram labels (pages 1, 2, 3, 8/9) to "Gateway
  (auth + redirect)". Page 4 is already correct — it has no gateway lifeline.
- **M** — If pod exposure becomes a problem, prototype KCP proxy mode behind the
  same `EnterWorldResponse` contract and measure the added latency before adopting.

---

## ADR-4 — Redis role overload

### Context

The criticism: Redis is being used as session store, service registry, and event
bus at once; those have different durability and eviction needs.

### Current state

Three distinct roles, all on one instance, all through typed interfaces:

| Role | Keys | Owner | Durability need |
|---|---|---|---|
| Sessions | `session:{user_id}`, TTL 1h | gateway | Expendable — TTL is the point |
| Registry | `servers:id:{id}` hash, `servers:map:{map}` set | gateway + deploy script | **Must not be evicted** |
| Events | `events:game` stream + consumer group | gateway (consumer) | **Must not be evicted or trimmed** |

Key prefixes are already namespaced by role (`shared/constants/keys.go:5-8`) and
the gateway shares one connection pool across all three stores
(`gateway/cmd/gateway/main.go:96-101`).

The concrete risk found: **no eviction policy was configured**. The compose service
(`backend/deploy/docker-compose.yml:116-143`) set `appendonly`/`appendfsync`/`save`
but no `maxmemory-policy`. Redis defaults to `noeviction`, so the deployment was
accidentally correct — but the moment anyone adds a `maxmemory` limit for capacity
planning, the default would start silently evicting registry hashes and stream
entries, i.e. dropping live game servers out of matchmaking.

### Decision

**One Redis instance is acceptable at the current scale, with the roles separated
logically by key prefix, and `noeviction` set explicitly.** Fixed in this PR:
`--maxmemory-policy noeviction` is now passed explicitly with a comment explaining
that this Redis is a system of record, not a cache
(`backend/deploy/docker-compose.yml`).

We do **not** use separate logical databases (`SELECT n`): they share one memory
limit and one eviction policy anyway, so they would give the illusion of isolation
without the substance, and they complicate the shared connection pool.

**The split path**, to be taken at the Soft Launch tier or when any single role's
memory or ops profile diverges:

1. **Events first.** Streams are the one role that grows unboundedly without
   trimming and benefits from different persistence tuning. Move `events:*` to its
   own instance.
2. **Registry second**, if registry write rate becomes material — it is the role
   whose loss is most immediately visible to players.
3. **Sessions last** — they are the most cache-like and the most tolerant of loss.

The seam is already in place: each role is constructed separately in
`gateway/cmd/gateway/main.go:96-101` and only shares a client by choice, so
splitting is a change to that constructor block, not to any business logic.

### Consequences

- With `noeviction` and a memory limit, Redis returns errors on write instead of
  silently dropping data. That is the correct failure mode here — a failed
  registration is visible, an evicted one is not — but it means memory must be
  monitored, not left to self-manage.
- Streams need explicit trimming (`XTRIM`/`MAXLEN`) once a real publisher exists,
  or `events:game` grows without bound. Today nothing publishes (ADR-5), so this
  is latent rather than live.
- A Redis outage takes out login and map entry, but not sessions already in
  progress on game servers.

### Follow-up work

- **S** — Set an explicit `maxmemory` and a Prometheus alert on Redis memory
  utilisation and rejected writes, now that eviction cannot silently absorb growth.
- **S** — Add `MAXLEN ~` trimming to `EventStream.Publish` before any real
  publisher is wired.
- **M** — Split `events:*` onto its own instance at the Soft Launch tier; the
  gateway constructor takes a second address.

---

## ADR-5 — Streams, not pub/sub

### Context

The criticism: docs mention both "pub/sub" and "Redis Streams" for cross-server
events, which are different reliability models.

### Current state

The code is unambiguous — the docs are not.

**Zero raw Redis pub/sub exists.** No `PUBLISH`, `SUBSCRIBE`, `PSUBSCRIBE`, or
`*redis.PubSub` anywhere in Go or C#. Every `Publish`/`Subscribe` in the tree is a
method on the `storage.EventStream` interface, whose Redis implementation is
Streams-based: `XADD` (`redisstore/stream.go:92`), `XGROUP CREATE MKSTREAM`
(:118), `XREADGROUP` (:138), `XACK` after the handler returns (:164).

The gateway's relay is real and wired, contradicting CORE_FLOW.md:158-159 which
calls it an unimplemented stub: `events.NewRelay(...)` at
`gateway/cmd/gateway/main.go:159-160`, passed in via `server.WithEventRelay`.

**But the pipeline is not connected end to end.** The C# game server generates a
real `entity_killed` event (`GameServer/Server/GameServer.cs:367-376`) and
publishes it into `NoopEventStream`, which discards it — because
`Program.cs:111` unconditionally supplies the noop, and no Redis-backed
`IEventStream` exists in C# at all (the project has no Redis dependency). On the
other end, the gateway's sink only logs and counts; it does not fan out to clients,
because `shared/messages` has no client-facing `MsgEvent` type
(`gateway/server/server.go:91-106`). So both halves are built and neither is joined.

### Decision

**Redis Streams with consumer-group ACK is the only mechanism for cross-server
events.** The rule:

- **Streams** for anything that needs a delivery guarantee — `boss_killed`,
  `rare_drop`, `inventory_changed`, `season_ended`, reward grants. ACK after the
  handler succeeds, so a consumer restart replays rather than drops.
- **Plain pub/sub** is reserved for genuinely fire-and-forget presence-style
  signals where a missed message is harmless. **Nothing uses it today**, and
  nothing should adopt it without a note in this file explaining why loss is
  acceptable for that signal.

All ambiguous "pub/sub" doc language is corrected in this PR to say either "Redis
Streams" or "event stream". `backend/shared/docs/DESIGN.md:82-116` already stated
this rule correctly; it is now consistent everywhere else.

### Consequences

- Every event consumer must be idempotent: ACK-after-handler means at-least-once
  delivery, so a crash between handling and ACK replays the event.
- Consumer groups need a naming convention and pending-entry monitoring; a
  consumer that dies mid-handle leaves entries in the PEL until claimed.
- Today no event crosses a process boundary, so none of the cross-server features
  that depend on it (world announcements, cross-map loot notifications) work,
  regardless of what the diagrams show.

### Follow-up work

- **M** — Add a Redis `IEventStream` to the C# server so `entity_killed` actually
  publishes. Requires taking a Redis dependency in a project that currently has
  none — coordinate with the ADR-1 self-registration work, which needs the same
  client.
- **M** — Add `MsgEvent` to `shared/messages` and turn the gateway's log-only sink
  into a real client fan-out with interest filtering.
- **S** — Document the consumer-group naming convention and add a Prometheus alert
  on pending-entry-list depth.

---

## ADR-6 — Crash recovery

### Context

The criticism: crash recovery is incomplete — there is a data-loss window and no
checkpointing.

### Current state — what exists

| Mechanism | Evidence |
|---|---|
| PostgreSQL player persistence, fail-fast on bad DSN | `GameServer/Persistence/PostgresPlayerStore.cs:98-134`; `Program.cs` returns 1 if unreachable |
| Periodic async save, off the tick thread | `AsyncSaver.cs:67-85`, interval 30s (`Program.cs`) |
| Final save on graceful shutdown (twice) | `AsyncSaver.cs:82-84` and `GameServer.cs:165-166` |
| Reconnect hold window, 30s map / 60s dungeon | `GameServer.cs:30,333-365`; cancelled on reconnect :245-251 |
| Agones health ping loop | `GameServer/Agones/AgonesSdk.cs:31-57`, 2s interval |
| Registry TTL expiry mechanism | `redisstore/registry.go:80` |

CORE_FLOW.md:250-251 claims reconnect holds do not exist; that is stale — they were
implemented in the C# server.

### Current state — what is missing

1. **Up to 30 seconds of player state is lost on an unclean crash.** The save
   interval is 30s and there is no journal, so the loss window is bounded by
   `SaveInterval` — position, HP and map only, since those are the only persisted
   fields (`schema.sql:12-23`).
2. **No final save on disconnect** — *fixed in this PR.* Hold expiry called
   `_world.RemoveEntity` without saving first (`GameServer.cs:356`), so a player
   whose hold lapsed lost everything since the last 30s sweep. The hold-expiry path
   now persists that player before removing them.
3. **`--agones` does nothing** — *documented in this PR.* `Program.cs` selected
   `NoopAgonesSdk` in **both** branches of the flag, so no `Ready`/`Health`/
   `Shutdown` was ever reported. Agones cannot detect an unhealthy C# pod today.
   The dead conditional is removed and the flag now logs a warning.
4. **No dungeon checkpointing.** Zero hits for "checkpoint" outside aspirational
   docs. Dungeons use the same 30s sweep as maps, so a dungeon-server crash loses
   the run.
5. **No WAL/journal**, and non-player entities (mobs, projectiles), pending inputs,
   in-flight combat and hold timers are never persisted — all lost on crash.
6. **Registry entries outlive dead servers** (ADR-2): heartbeat has no caller.

### Decision

**Accept a bounded ≤30s loss window for player position/HP/map as a deliberate MVP
tradeoff**, and accept total loss of transient world state (mobs, projectiles,
in-flight combat). Rationale: the persisted fields are cheap to re-derive from the
player's perspective — a rubber-band of up to 30s of walking — whereas
per-tick persistence would put synchronous I/O anywhere near a 66ms tick budget,
which `backend/gameserver-dotnet/CLAUDE.md:44` forbids outright.

**Reduce the window at the edges rather than shortening the interval**: save on the
events that matter (disconnect/removal — done here; zone transfer and dungeon
completion — follow-up) instead of paying for a faster global sweep.

This acceptance is **conditional on the loss being invisible to economy and
progression**. Anything with real value — currency, items, quest completion — must
be written through Nakama transactionally at the moment it is granted, never left
to the 30s sweep. That boundary is what makes a 30s gameplay-state window
tolerable rather than a duplication exploit.

### Consequences

- A crash costs at most 30s of movement/HP per player, plus the entire live
  encounter state (mobs reset, loot not yet granted is gone).
- Dungeon runs are not crash-safe. A dungeon crash loses the run with no checkpoint.
- Until Agones health reporting works, an unhealthy C# pod is not restarted by the
  orchestrator — the crash-recovery story assumes a supervisor that currently is not
  watching.

### Follow-up work

- **S** — Save on zone/map transfer, the other point where an entity leaves a world.
- **M** — Wire the real Agones SDK in C# so `Ready`/`Health`/`Shutdown` are
  reported and unhealthy pods are actually replaced. This gates the whole
  orchestration story.
- **M** — Dungeon checkpoints: persist party progress at encounter boundaries into
  a `dungeon_checkpoints` table (already named in `backend/shared/CLAUDE.md:29`,
  never built).
- **L** — Durable transient state (mob HP, encounter progress) if playtesting shows
  losing an in-progress boss fight is unacceptable.

---

## ADR-7 — CCU and cost figures are unbenchmarked estimates

> ## ⛔ CURRENT STATE (2026-08-07, final) — capacity work is BLOCKED on hardware
>
> Read this before quoting any number from this ADR.
>
> | Question | Answer |
> |---|---|
> | Downstream bandwidth | **SOLVED to the threshold.** 45.9 KB/s per client at 200 players, inside ADR-7's `< 50 KB/s`. Ceiling is above 200 and no longer bracketed |
> | How reliable is that? | **0.3% spread across six runs.** Bytes on the wire do not care what else the host is doing |
> | Players per game server (tick) | **UNKNOWN — and unknowable on this machine** |
> | What binds now | **Tick time.** Bandwidth was the constraint at ~⅓ of the tick ceiling; three wire changes removed it, and tick now breaks first |
> | What unblocks it | **A separate machine for the load generator. Nothing else.** |
>
> **The ~150-player figure below is STALE. Do not quote it as the current
> ceiling.** It was measured before Protobuf, before the entity-type enum and
> before id interning — three changes that removed 81% of the wire and, with it,
> the constraint that produced 150. Everything derived from it is stale too,
> including the "game servers implied" column in the root `CLAUDE.md` tier table,
> which divides tier CCU by 150.
>
> **Why a new number cannot be produced here.** The load generator runs on the
> same machine as the server under test and consumes *more CPU than it* (~261% of
> a core against ~120% at 200 players). Tick p99 is highly sensitive to that: at a
> fixed level it measured 67.4–70.8ms with the box quiet and 224.7–240.6ms with a
> deploy sharing it — a **3.3× swing**, with a monotonic dose-response against
> host load (9.00 → 67.83ms, 14.01 → 67.41ms, 15.29 → 70.28ms). The tick mean
> moves 1.7× under the same disturbance.
>
> So the generator's own load sits *inside* the measurement. Every tick figure
> from this host is a **lower bound — pessimistic, safe to plan against, but
> under-reporting headroom by an unknown amount.** Optimising against it would be
> tuning to an instrument known to be measuring the wrong thing.
>
> **Item 6 of the plan below is therefore promoted from a footnote to a
> BLOCKER.** It is not "nice to have before publishing a tier table"; it is the
> single prerequisite for any further capacity work. This is a measured
> conclusion, not a suspicion.
>
> ---
>
> **UPDATE 2026-08-07 — the primary benchmark has been run.**
>
> `backend/loadtest` now exists and the game-server capacity ceiling has been
> measured. Full write-up: **[BENCHMARK.md](BENCHMARK.md)**.
>
> | Measured (dev workstation, lower bound) | Result |
> |---|---|
> | Players per game server before tick p99 > 66.67ms | **150** ⚠️ **STALE — pre-Protobuf, see above** |
> | RAM per pod | **~30 MiB idle, ~50 MiB @100, ~82 MiB @200** — resolves the 30-45 vs 50 MB dispute |
> | Downstream bandwidth | **1.22 KB/s per in-AOI player**; 184 KB/s per client at 150 |
> | Bandwidth vs the < 50 KB/s threshold below | breached at **~41 players** ⚠️ **SUPERSEDED — now passes above 200** |
>
> **The bottleneck prediction in this ADR was wrong.** This ADR named brute-force
> AOI as "the most likely first failure". Measurement puts snapshot construction
> plus JSON serialization at ~80% of tick cost and the AOI scan at ~20% — a 5:1
> ratio, confirmed two independent ways (see BENCHMARK.md §5). Protobuf, not a
> spatial grid, is the highest-impact fix; the spatial grid drops to fourth.
>
> **Still unmeasured** (items 4 and 5 of the plan below): connection churn, and
> gateway login throughput. Also unmeasured on real hardware — every figure above
> came from a WSL2 dev box that was simultaneously running the load generator,
> Docker Desktop, Kubernetes and an AI agent. The ⚠️ markers on the tier tables
> stay until a VPS run replaces them.
>
> **A new blocker was found**: entities leak on disconnect
> (`gameserver_entities` never returns to 0), which turns the bounded O(n²) tick
> cost into an unbounded one on a long-lived server. BENCHMARK.md §7.

> **UPDATE 2026-08-07 (b) — the acceptance criterion in this ADR is not
> decidable as written, and has been changed.**
>
> This ADR defined the tick threshold as "p99 within the 66.67ms budget",
> evaluated at one player count from one run. The same build measured p99
> 53.46ms and then 72.47ms at 200 players, straddling the budget, which moved a
> reported ceiling by 50 players and forced a published claim to be withdrawn.
>
> **The cause was not what it first looked like.** The obvious reading is "p99 is
> a noisy statistic". It is not: repeated at a fixed level on a quiet machine, p99
> lands inside a **5.1% spread** over six runs. What p99 is, is
> *contention-sensitive*. Measured
> at one level with a CD deploy sharing the host, four runs sat in 72.9–74.6ms
> and two came back at 224.7 and 240.6ms — **p99 moved 3.3x while the tick mean
> moved 1.7x** under the identical disturbance. The disturbance is bimodal, not a
> smear, and this repo's load generator shares a machine with its self-hosted
> deploy runner.
>
> That correction matters for the withdrawn claim too: it means **53.46ms was the
> anomalous reading and 72.47ms the reproducible one**, so json@200 genuinely
> fails the budget.
>
> **More runs is the wrong fix, and the arithmetic says so.** A rule of "the level
> passes only if it passed every one of N runs" gets *worse* as N grows: with 2
> runs in 6 disturbed, the chance all N are clean is (4/6)^N — **0.44 at N=2, 0.30
> at N=3, 0.20 at N=4**. Unanimity at N=3 would mark a genuinely-passing level
> marginal roughly 70% of the time. N is not a defence against an outlier process.
>
> **Adopted instead**, in `backend/loadtest`:
>
> 1. **Don't measure during a deploy.** `scripts/encoding-sweep.sh` refuses to
>    start while a `cd.yml` run is in progress or queued. This is the actual fix;
>    everything below is containment for when it is bypassed.
> 2. **Decide a level on the MEDIAN p99 across its runs**, not on unanimity. A
>    minority of disturbed runs cannot move a median.
> 3. **Report the min..max bracket and flag straddles.** A level whose runs
>    straddle the budget is still named, because that is the evidence a reader
>    needs — it is just no longer allowed to silently decide the ceiling.
> 4. **Record host load per run** (`host.load_avg_1`), as evidence, never as a
>    verdict input. A tempting rule — "achieved tick rate below the configured
>    rate means the process was starved, so the run is invalid" — was tried and
>    **rejected**: a genuinely saturated server loses ticks the same way (measured
>    10.46 ticks/s at 300 players and 12.51 at 400, both real capacity limits,
>    against 12.87–13.48 for a quiet box disturbed by a deploy). Such a rule would
>    classify real saturation as an environment fault and hide the ceiling — an
>    error in the optimistic direction. The tool cannot tell the two apart from
>    its own metrics and does not pretend to.
>
> **Until a level has been measured this way, treat every player-count ceiling in
> this project as approximate to ±50**, including the 150 in the table above.
>
> **Item 6 of the plan below (re-run on real hardware) now has a measured reason,
> and it is not "the box is too noisy".** With the CD guard held and host load
> recorded, six repeats of a fixed level held a 5.1% p99 spread — usable. The
> problem is subtler: **the load generator runs on the same machine as the server
> under test and consumes more CPU than it** (~261% of a core against ~120% at 200
> players), and p99 tracked host load monotonically within that quiet set
> (load 9.00 → 67.83ms, 14.01 → 67.41ms, 15.29 → 70.28ms, rising as the generator
> warmed up).
>
> So the generator's own load is *inside* the measurement. That makes every tick
> figure from this host a **lower bound — pessimistic, not optimistic**: the
> server is competing with the thing measuring it, so its measured capacity is
> below its true capacity. Safe to plan against, but it under-reports headroom,
> and the size of the under-report is unknown. A number anyone sizes a fleet on
> needs the generator on separate hardware.
>
> **Bandwidth is unaffected and is the better criterion.** It reproduced to within
> **0.3%** across six runs at a fixed level and **0.4%** across two sweeps on
> different builds, because bytes on the wire are
> not a tail statistic and do not care what else the host is doing. It is also the
> *binding* constraint — roughly a third of the tick ceiling. **Fleet sizing should
> use the bandwidth threshold**, and a change whose purpose is sending fewer bytes
> should be judged on it rather than on a contention-sensitive CPU threshold.

### Context

The criticism: the CCU and cost tables are presented as fact but nothing has been
measured.

### Current state

The criticism is correct. Every CCU, cost, RAM and latency figure in the repo is a
planning estimate. No load test exists — the only traffic generator is
`backend/smoketest`, which drives exactly **one** simulated client through one
login and 10 inputs (`smoke/helpers.go:42-43`), and is a correctness check, not a
benchmark.

Unsourced figures, all now marked as estimates in this PR: the four-tier cost/CCU
tables (root `CLAUDE.md`, `README.md`, `backend/deploy/CLAUDE.md`,
`backend/deploy/docs/README.md`, `backend/docs/CORE_FLOW.md`); the gateway targets
"2000+ concurrent clients", "< 1ms forwarding", "< 100MB at 1000 CCU"
(`backend/gateway/CLAUDE.md:56-58` — note the latency target describes packet
forwarding the gateway does not do, per ADR-3); and game-server RAM, which the
docs give as "~30-45MB/pod" (`CLAUDE.md:109`) while the drawio says "~50MB"
(`IdeaATeckStack/RPG-MMO-indie.drawio:115`) — two different numbers for the same
thing, neither measured.

The one figure that is real is the tick budget: 66ms at 15Hz, already instrumented
as a histogram (`GameServer/Observability/GameMetrics.cs:75-78`) with an overrun
warning at 2× budget (`GameServer/Server/TickLoop.cs`).

### Decision

**Mark every CCU/cost/performance figure as an unbenchmarked estimate until a load
test produces real numbers**, and treat the tick budget as the anchor metric that
everything else is derived from. Capacity claims are not to be repeated in
customer-facing or planning material without a benchmark reference.

### Benchmark plan

**What to measure**, in priority order:

1. **Tick p99 vs entity count** — the primary ceiling. Step entity count until p99
   crosses the budget. This is the number that determines per-map capacity, and it
   is expected to bend early because AOI is a brute-force O(n²) scan per tick.
   *(Measured: breaks at 160 players. The expectation about AOI was wrong —
   serialization dominates 5:1. BENCHMARK.md §5.)*
2. **Snapshot bandwidth per client** — bytes/sec/client vs nearby-entity count.
   Snapshots are delta-encoded JSON with a keyframe every 30 snapshots, so this is
   expected to be the first thing that hurts on mobile networks. *(Measured: yes —
   1.22 KB/s per in-AOI player, breaching the 50 KB/s threshold below at ~41
   players, well before the tick budget breaks.)*
3. **RAM per 100 players** — resident set at 0 / 100 / 500 players, to replace the
   contested 30-45MB vs 50MB claim with a measured curve.
4. **Connection churn** — join/leave rate the accept path sustains, including the
   reconnect-hold bookkeeping, which allocates a timer per disconnect.
5. **Gateway login throughput** — logins/sec and p99 auth latency. Note this
   replaces the meaningless "packet forwarding latency" target: the gateway's real
   hot path is authentication and map assignment.

**Tooling**: build a `backend/loadtest` binary seeded from `backend/smoketest` —
the `Runner` already performs a full login → enter-world → join → input loop, so
the load generator is N concurrent `Runner`s with a configurable input rate,
plus latency histograms and a CSV/Prometheus output. Deliberately **not built in
this PR**; specced as a follow-up so the ADR is not blocked on it.

**Acceptance thresholds**, all tied to the 66ms budget at 15Hz:

| Metric | Threshold |
|---|---|
| Tick p99 | < 33ms (half the budget) at target entity count — headroom for GC and spikes |
| Tick p99 hard fail | ≥ 66ms — the simulation is late and clients rubber-band |
| Tick overrun warnings | zero at steady state |
| Snapshot bandwidth | < 50 KB/s per client at typical AOI density (mobile-network assumption) |
| RAM | < 128Mi per pod at target capacity — the existing Agones pod limit |
| Auth p99 | < 100ms |

A tier's CCU claim is only publishable once a run at that CCU holds every threshold.

### Consequences

- Tier tables stay in the docs as planning aids but are explicitly labelled
  estimates, so nobody sizes a launch on them.
- The AOI implementation is the most likely first failure. Measuring before
  optimising avoids rewriting it on a guess.
- Until the load test exists, per-map capacity (ADR-2) is set by assumption.

### Follow-up work

- ~~**M** — Build `backend/loadtest` per the spec above.~~ **Done** — see
  `backend/loadtest/`.
- ~~**M** — Run the benchmark matrix~~ **Partially done** — tick-vs-entity-count,
  snapshot bandwidth and RAM are measured (BENCHMARK.md). Connection churn and
  gateway login throughput are still open.
- **S** — Add explicit histogram buckets around the 66ms budget instead of the OTel
  defaults, so p99 is readable near the threshold. **Still worth doing**: the
  nearest edges are 0.05s and 0.075s, which straddle the 0.0667s budget, so an
  interpolated p99 near the threshold is imprecise. `backend/loadtest` works
  around this by also reporting the exact fraction of ticks above the 0.05 edge.
- **L** — **Protobuf/FlatBuffers on the snapshot path. Now the top-priority
  performance item**, ahead of AOI work: measurement attributes ~80% of tick cost
  and effectively all of the bandwidth problem to JSON encoding.
- **S** — Move serialization off the tick loop. `WireProtocol.NewEnvelope`
  serializes inline while `Connection.Send` merely enqueues; doing it in the
  writer task removes the dominant term from the critical path without changing
  the encoding.
- **S** — Fix the entity leak on disconnect (BENCHMARK.md §7). Highest priority of
  all — it makes the O(n²) cost grow with cumulative joins rather than concurrent
  players.
- **S** — Stagger per-connection keyframe counters to stop the keyframe stampede.
- **M** — Spatial-grid AOI. Measurement demotes this: it targets the ~20% term.
- **⛔ BLOCKER** — Put the load generator on a **separate machine** from the
  server under test, then re-run the matrix and replace the tier CCU numbers.
  This is no longer one item among several: bandwidth is solved, tick now binds,
  and tick is precisely the statistic a co-located generator distorts (measured
  3.3x on p99, with a dose-response against host load). **No further capacity
  work is meaningful until this is done** — see the CURRENT STATE block at the
  top of this ADR.
- **M** — Measure the two remaining plan items: connection churn, and gateway
  login throughput (`-auth=nakama` in `backend/loadtest` drives the real path).

---

## ADR-8 — Realtime-path security: PSK encryption, split secrets, rate limits

### Context

Three gaps existed on every internet-reachable surface of the realtime path:

1. **The wire was plaintext.** `shared/transport` created KCP sessions with a
   `nil` `BlockCrypt`. The join token and all gameplay state travelled in the
   clear over UDP, where anyone on the path could read a token and replay it
   against the game server inside its 30s TTL.
2. **One secret protected everything.** `JWT_SECRET` signed the Nakama-issued
   client auth token *and* the gateway-issued join token. The join secret has to
   be distributed to every game server pod, so the blast radius of one
   compromised pod was the whole authentication system — including the ability
   to mint auth tokens for arbitrary users.
3. **Nothing was rate limited.** Connection accepts, gateway frames and the
   `gateway_token` RPC were all unbounded. A single host could exhaust a gateway
   process, and an unbounded `gateway_token` loop is a free oracle for minting
   the credentials to do it with.

### Decision

**1. KCP encryption is opt-in via a pre-shared key.** `transport.WithKey`
(`TRANSPORT_KEY`, 32-byte hex recommended, passphrases stretched with
HKDF-SHA256) installs kcp-go's AES-256 `BlockCrypt`. Empty key = plaintext,
which stays the dev default but makes a KCP listener log a WARN on every start.

*Why a PSK and not per-session keys.* The correct end state is a per-session key
handed out with the join token (or DTLS), because a PSK is one secret shared by
every client binary: extract it from one APK and you can decrypt any session you
can capture. A PSK is still a strict improvement — it defeats the passive
observer, which is the realistic threat on mobile carrier and public Wi-Fi
networks — and it costs no protocol change, no key-exchange state, and no
change to the join-token flow. Per-session keying needs a new field in
`EnterWorldResponse`, matching Unity-client work, and a key schedule on the
game-server side; that is a deliberate follow-up, not MVP scope.

**2. Join tokens get their own secret, and both secrets rotate.**
`JOIN_TOKEN_SECRET` signs gateway→gameserver join tokens; `JWT_SECRET` keeps
signing the Nakama→client auth token. Unset `JOIN_TOKEN_SECRET` falls back to
`JWT_SECRET` (old behaviour, so no deployment breaks on upgrade) with a
start-up warning. Both variables accept a comma-separated list
(`jwt.Keyring`): the first entry signs, all entries verify, so rotating a
secret no longer logs the whole population out.

**3. Rate limits at every entry point,** using one shared token-bucket
(`shared/ratelimit`):

| Surface | Default | Key |
|---|---|---|
| Gateway accepts | 10/min, burst 10 | source IP |
| Gateway inbound frames | 60/s, burst 120 | connection |
| Nakama `gateway_token` | 0.2/s, burst 5 | user id |

Rejections increment `gateway_rate_limited_total{reason}`. The per-connection
check is a struct field, not a map lookup — 10.8 ns/op, 0 allocs — because it
runs on every inbound frame. A connection that trips it gets one explicit
`rate limited` error frame and is then closed: replying to every over-limit
frame would turn the limiter into an amplifier.

### Consequences

- **KCP is not reachable end to end today.** `gameserver-dotnet` has no KCP
  implementation at all (C# side is TCP only), so `--transport=kcp` currently
  only applies to hop 1 (client→gateway). Transport encryption therefore
  protects the auth/redirect hop, and the gameplay hop stays TCP-plaintext until
  the C# side gains both KCP and a matching key. This is the single largest
  remaining hole and it is **not** closed by this ADR.
- Encryption fails closed and *silently*: a peer with the wrong key produces
  datagrams that decrypt to noise and are dropped as malformed segments. There
  is no error, only a connection that never establishes. Operators rolling
  `TRANSPORT_KEY` must roll both sides together — there is no rotation window
  for the transport key, unlike the JWT secrets.
- The rate limiters are per process. N gateway replicas admit N x the limit per
  IP; N Nakama instances admit N x the limit per user. Accepted for the MVP
  (single-instance tiers); the upgrade is a Redis-backed counter on the Redis
  the gateway already depends on.
- Splitting the secrets means two values to manage in every deployment. The
  fallback keeps that optional, at the cost of a warning nobody can miss.

### Follow-up work

- **L** — Per-session transport keys: mint a key with the join token, return it
  in `EnterWorldResponse`, key the game server's KCP session from it. Requires
  Unity-client work.
- **M** — KCP + `TRANSPORT_KEY` in `gameserver-dotnet`, so hop 2 can be
  encrypted at all. Blocking the item above.
- **M** — `JOIN_TOKEN_SECRET` support in `gameserver-dotnet` (see the gateway
  CHANGELOG for the exact call site); until then the split cannot be turned on
  in production without breaking joins.
- **S** — Redis-backed rate limiters for multi-replica correctness.
- **S** — Fail start-up (rather than warn) on the dev-default `JWT_SECRET` when
  a `production` profile flag is set.

---

## ADR-9 — Wire encoding: Protobuf, migrated by content sniffing

**Status:** accepted, implemented 2026-08-07.

**Context.** [BENCHMARK.md](BENCHMARK.md) measured that snapshot construction
plus `JsonSerializer` accounted for ~80% of tick cost against ~20% for the
brute-force AOI scan, and that ADR-7's own acceptance threshold of
`< 50 KB/s per client` broke at **~41 players** — less than a third of the
150-player tick-budget ceiling. Bandwidth, not tick time, was the binding
constraint, and JSON was the entirety of it. Protobuf was already the stated
production encoding in the extension-seam table; the measurement moved it from
"later polish" to the first thing to do, ahead of spatial-grid AOI.

**Decision.**

1. **One schema, two generators.** `backend/shared/proto/wire.proto` is the
   single source of truth. Go and C# bindings are generated from it by
   `shared/proto/generate.sh` and **committed**, so no CI runner and no fresh
   clone needs `protoc`. A `proto-generated` CI job regenerates and fails on any
   diff, which is what stops the committed output from drifting from the schema.

2. **No parallel hand-written definitions.** The C# game server's hand-written
   mirrors of the Go structs were deleted; the generated types are now its only
   message classes. Legacy JSON is produced by a hand-written
   `Utf8JsonWriter`/`Utf8JsonReader` codec over those same generated types, so
   there is still exactly one definition of every message.

3. **Migration is by content sniffing, not negotiation.** A JSON body always
   starts with `{` (0x7B); a Protobuf `Envelope` always starts with `0x08`, the
   tag for field 1 (`type`), which proto3 never elides because `type >= 1` for
   every real message. One byte therefore identifies the encoding. Encoding is
   latched **per connection**, and a server always replies in the encoding it was
   addressed in.

**Why sniffing rather than a version field or a handshake.** The gateway, the
game servers and the Unity client deploy independently — under Agones they are
literally separate pods rolling at different times. A negotiated handshake would
add a round trip to every connection and still need a fallback path; a version
field in the envelope would have to be understood by both sides *before* it could
be read, which is the same bootstrapping problem one layer down. Sniffing gives a
rollout with no flag day, no ordering requirement between components, and no
window in which a mismatched pair is broken. It also makes the benchmark
single-variable: because the server answers in the client's encoding,
`loadtest -encoding json|proto` A/B-tests one unchanged binary.

The cost is that a corrupt frame not starting with `{` is reported as a Protobuf
parse failure rather than as "unrecognised encoding". Both are fatal for the
frame and both close the connection, so nothing is lost.

**Consequences.**

- Framing (`[4-byte big-endian length][body]`) is unchanged, so
  `shared/transport` and the TCP/KCP layer needed no changes and cannot observe
  the difference.
- `messages.UnmarshalPayload` became a method on `Envelope` so the encoding
  travels with the bytes it describes. Go API break, not a wire break.
- JSON remains supported and is still the default for existing callers. It is
  the legacy path and should be retired once no pre-Protobuf client remains; that
  retirement is a separate decision with its own deprecation window.
- `EntitySnapshot.type` is still a string and `id` is still a full string.
  Interning either would shrink the hottest message further but changes what the
  field *means*, so both are deliberately out of scope here — see
  `shared/docs/DESIGN.md`.

**Revisit if** a client appears that cannot be upgraded, or if measured
bandwidth still fails the ADR-7 threshold at the player counts a tier is sized
for — in which case the next lever is field-level (entity-type enum, interned
IDs), not another encoding swap.

---

## ADR-10 — Shared simulation: pure logic boundary, ECS on the server only

> **Narrowed by [ADR-12](#adr-12--the-server-goes-to-real-ecs-staged-under-the-constraints-adr-10-and-adr-11-set)
> (2026-08-14).** Decision 1 said Arch is the server's entity storage; it is now also
> the server's *execution* model, staged. The boundary this ADR draws is unchanged and
> is the binding constraint on how far that can go: no `Arch.Core` type crosses into
> `Shared.GameLogic`, its `in EntityState` / `in Vec2` signatures are frozen, and the
> golden vectors stay bit-exact.

**Status:** accepted 2026-08-11. **Implemented** — both halves, as of 2026-08-17.

> **What landed since.** The Context below describes the state on 2026-08-11 and is
> left as written; two of its statements are no longer true of the code:
>
> - *"the client references nothing today"* — the Unity project consumes
>   `Shared.GameLogic` as a UPM git package, pinned in `packages-lock.json` to
>   `sgl-v0.1.9`. Client and server run the same movement, combat and validation code,
>   which is what this ADR existed to make possible.
> - *"not yet implemented"* — the server's Arch migration ran to completion in five
>   stages under [ADR-12](#adr-12--the-server-goes-to-real-ecs-staged-under-the-constraints-adr-10-and-adr-11-set);
>   `SystemSchedule`, `SimulationSchedule`, `SimChunk`, `WorldReader`/`WorldWriter` and
>   `ComponentAccess` are all on `develop`.
>
> The boundary itself is unchanged and still holds: no `Arch.Core` type crosses into
> `Shared.GameLogic`, the `in EntityState` / `in Vec2` signatures are frozen, and the
> golden vectors are bit-exact across both runtimes.

**Context.** The game server is to move its entity storage to
[Arch](https://github.com/genaray/Arch), an archetype/chunk ECS for C#. At the
same time, the long-stated goal that the Unity client and the server run *the
same simulation code* (`Shared.GameLogic`) is still unrealised: the client
references nothing today. Doing both at once forces a decision that is easy to
get wrong by default — whether "share the simulation" means sharing the ECS.

It does not. An ECS is a storage and scheduling choice. The game's rules are
what must agree between the two sides, and those are separable from the
container they are iterated out of.

**Decision.**

1. **Arch replaces `GameWorld` as the server's entity storage.** Not additive,
   not alongside — two sources of truth inside one tick is a synchronisation bug
   generator. Arch owns entity identity, component storage, queries and
   iteration order.

2. **`Shared.GameLogic` stays ECS-free.** No `Arch.Core` type may appear in its
   public or internal surface: no `World`, no `Entity`, no `QueryDescription`, no
   `[Component]`. It remains static functions over plain structs. Arch systems on
   the server iterate and *call into* it; they do not absorb it. The same rule
   already bars `UnityEngine` — this extends the constraint to the second engine
   now in play, for the same reason.

3. **The client consumes it as source, not as a DLL.** `Shared.GameLogic`
   multi-targets `netstandard2.1;net10.0` and carries a `package.json` +
   `.asmdef`, consumed by Unity through a UPM git dependency with a `?path=`
   subfolder reference **pinned to a tag**. The normative form, in the client's
   `Packages/manifest.json`:

   ```json
   "com.rpgmmo.shared-gamelogic": "https://github.com/Cuvara/rpg-mmo-server.git?path=/backend/gameserver-dotnet/Shared.GameLogic#sgl-v0.1.0"
   ```

   Compiling from source is what keeps IL2CPP from having to swallow a
   `netstandard2.1` assembly, keeps the code steppable in the Editor, and removes
   any possibility of binary drift between what the server ran and what the client
   shipped. "Release" here therefore means **stamping a commit with a tag**, not
   building an artifact. A `.tgz` on GitHub Releases would be strictly worse — UPM
   consumes local paths, git URLs and registries, but not tarball URLs.

   This mechanism is not novel in this project: the Unity client already resolves
   `com.company.build-pipeline` through the identical `git?path=#ref` form, and
   `rpg-mmo-server` is public, so no credential or deploy key is involved on a
   developer machine or in CI.

   **Pin a tag, never a branch.** A branch reference means the rules the client
   predicts with change whenever someone pushes, silently, with no commit in the
   client repo to attribute the change to. A tag makes adopting new rules a
   deliberate, reviewable act, and `packages-lock.json` records the resolved
   commit so a build stays reproducible even if a tag is moved. Tags are
   `sgl-vX.Y.Z` with no `/`, since a slash is a valid git ref but its handling
   inside a UPM `#fragment` is unverified.

   Two mechanical guards make the boundary self-enforcing rather than
   review-dependent: `<LangVersion>9.0</LangVersion>` on the csproj makes the
   *server* build reject C# 10+ syntax the client could not compile, and
   `"noEngineReferences": true` in the asmdef makes the *client* build reject a
   `UnityEngine` reference. Each side fails on the rule the other side would
   otherwise not notice being broken.

4. **Conformance is mechanical, not editorial.** A committed set of golden
   vectors — `(state, input, dt) → expected state` — is executed by the server's
   xUnit suite *and* by the Unity Test Runner, from the same fixture files. A
   divergence fails CI on whichever side moved. Without this, "shared logic" is
   a shared file, not shared behaviour.

   The format is constrained by both readers, so it is fixed here rather than
   left to whoever writes first:

   - **Floats are stored as their IEEE-754 bit pattern** (`"x": "0x40551EB8"`),
     compared with `BitConverter.SingleToInt32Bits`. Decimal text does not
     round-trip identically through two different serializers, and a fixture
     compared with a tolerance would not be testing the property this ADR exists
     to protect. Storing bits also makes the assertion self-documenting.
   - **The top level is an object, not an array** (`{"cases": [...]}`), and the
     schema stays flat with public fields only — no dictionaries, no properties.
     That is the subset Unity's built-in `JsonUtility` can read, so the client
     needs no JSON package at all.
   - Fixtures are read by **EditMode** tests from the package path. A PlayMode or
     IL2CPP build cannot read arbitrary paths and would need `Resources/` or
     `StreamingAssets/`; that is deferred until something actually requires it,
     since these are a CI conformance gate rather than a runtime asset.

5. **The determinism budget is explicit.** `Shared.GameLogic` may use only
   IEEE-754-exact float operations: `+ - * /`, comparison, and
   `MathF.Min/Max/Abs/Sqrt`. Transcendentals (`Sin`, `Cos`, `Atan2`, `Pow`,
   `Exp`) are **not** permitted, because their results are implementation-defined
   and the two sides run different compilers on different architectures
   (NativeAOT x64 server, IL2CPP ARM64 client). Adding one is an amendment to
   this ADR, not a code review comment.

   **Choosing exact operations is not sufficient — every intermediate must be
   rounded to `float` explicitly.** Amended 2026-08-11, after the golden vectors
   caught a real divergence on their first run in Unity. C# permits a float
   expression to be evaluated at higher precision than `float` (ECMA-334
   §11.3.7), and the two runtimes take different options: .NET 10's RyuJIT
   evaluates strictly in float32, while Unity's Editor Mono JIT keeps
   double-precision intermediates and rounds once at the end. `X * X + Y * Y`
   therefore produced results differing by one ULP — and because that value feeds
   magnitude comparisons, the two sides could take *different branches*, not just
   report slightly different numbers.

   Two shapes carry the hazard, and both are now written defensively in the
   library:

   - **Compound arithmetic** — write `(float)((float)(a * b) + (float)(c * d))`,
     never `a * b + c * d`.
   - **Multiply-add** (`a + b * c`) — additionally at risk of being contracted
     into a single FMA instruction, which rounds **once** instead of twice and so
     yields a different float. Split the multiply into its own `float` local to
     deny the contraction.

   Note what this cost to find: the operations were already legal under the rule
   above, every server test passed, and nothing warned. Only replaying the golden
   vectors under the *other* runtime exposed it — which is the entire reason
   decision 4 exists.

**Why not share the ECS.** Four reasons, in descending weight:

- Arch's Unity story is thin. The integration guide is one paragraph — "build
  Arch as a DLL and add it to the Unity project" — with no statement about
  IL2CPP, AOT, code stripping or source generators, and it concedes there is no
  example project. That is enough to build on; it is not enough to place the
  determinism contract on top of.
- The client already ships Unity DOTS (Entities 1.4.8, Burst, Collections).
  Two ECS frameworks in one IL2CPP build means two sets of AOT generic
  instantiations and two stripping surfaces, for no gameplay benefit.
- Arch is not Burst-compatible. If client-side prediction ever moves into a
  Burst job, the logic must already be static functions over blittable structs —
  which is exactly what constraint 2 preserves and what an Arch-typed API would
  foreclose.
- Arch is at `2.1.0-beta`. Coupling the shared contract to it makes every
  upgrade of a pre-1.0-stability dependency a client rebuild with prediction
  divergence as the failure mode.

**What must change first.** Four properties of the current
`Shared.GameLogic` block both halves of this, and are prerequisites rather than
follow-ups:

| | Current | Required | Why it blocks |
|---|---|---|---|
| Target framework | `net10.0` only | `netstandard2.1;net10.0` | Needed for the server-side and tooling builds; see the note below on why it is not sufficient |
| `EntityState.Id` | `string` | integer handle | A managed reference inside an ECS component puts a pointer in every chunk and is prohibited outright under Burst |
| `EntityState.Type` | `string` | enum | Same, plus it duplicates `EntityType` which the wire already carries as an enum |
| `AoiLogic.GetNearbyEntities` | returns `new List<EntityState>()` | fills a caller-provided `Span<T>` | Allocates per call, per tick, per entity — the cost Arch exists to remove |

**Multi-targeting alone does not make the library Unity-consumable, and the
constraint that matters is not `netstandard2.1`.** Because decision 3 has the
client compile *source*, Unity never loads the netstandard assembly — Unity's own
compiler is what has to accept these files, and it is **C# 9** (Unity 6 does not
support C# 10 or later). Three properties of the current source fail there:

- **File-scoped namespaces** (`namespace X;`) are C# 10 and appear in all 11
  files. Mechanical to fix, but it is every file, so it is decided once.
- **`ImplicitUsings=enable`**, which the sources rely on — e.g. `MapBounds.cs`
  uses `MathF` with no `using System;`. Unity has no implicit usings, so every
  file needs its usings written out.
- **`System.Text.Json.Serialization` attributes** on `InputData` and
  `SnapshotData`. Unity does not ship System.Text.Json.

The third looks like it forces a choice between moving those DTOs out of the
library and taking an STJ dependency through IL2CPP. It does not: **nothing
serializes these types.** ADR-9 deleted the hand-written message mirrors and made
the generated Protobuf types the server's only message classes, with legacy JSON
produced by a hand-written `Utf8JsonWriter` codec over *those*. `InputData` and
`SnapshotData` are only ever constructed and read directly. The attributes are
dead metadata from before ADR-9 and are simply deleted — no relocation, no
dependency, no behaviour change.

`netstandard2.1` itself needs no polyfill: `MathF.Min/Max/Abs/Sqrt` is available
there, and that set is exactly the determinism budget in rule 5.

**There are three identity spaces, and conflating any two of them is the main
hazard in this work.** An earlier draft of this ADR cited the wire's interned ids
as "prior art" for the simulation handle. That was wrong and is corrected here:
the wire handle is deliberately the *opposite* kind of identity.

| Identity | Lifetime | Scope | Lives in |
|---|---|---|---|
| `user_id` (string) | durable, across sessions and servers | global | `player_states`, join-token `sub`, reconnect/hold table |
| simulation handle (integer, new) | the entity's lifetime | one server process | `EntityState.Id`, ECS components |
| wire handle (`EntitySnapshot.handle`) | **one keyframe interval** | one connection | snapshot encoding only |

The wire handle is reset at every keyframe and allocated from 1 per connection —
that is precisely what bounds a client/server disagreement to a single keyframe
interval (ADR-9 follow-on, `wire.proto`). A simulation handle must be stable for
the entity's lifetime. Reusing one as the other yields an identifier whose meaning
silently changes every keyframe.

The mapping between `user_id` and simulation handle must therefore be an explicit,
named structure, not an implicit equality. Today `EntityState.Id` *is* the user id
(`GameServer.cs:443`, `Id = userId`), so this migration is not "narrow a field" —
it is "split one identity into two", and every existing use has to be classified
as one or the other. The reconnect/hold table stays keyed by `user_id`: a hold
exists precisely when the entity does not, so it cannot be keyed by a handle.

**Consequences and accepted costs.**

- **ADR-7's measurements do not carry over.** The 45.9 KB/s per client and
  ~82 MiB at 200 players figures were taken against `GameWorld`. Storage is
  being replaced underneath the tick loop; the benchmark must be re-run before
  any of those numbers is quoted again. The per-server player ceiling remains
  unknown for the reason ADR-7 gives, and this does not change that.
- **The id migration reaches beyond the simulation.** `EntityState.Id` is a
  `string` today in persistence (`player_states`), in the snapshot encoder and
  in the reconnect/hold bookkeeping keyed by user id. The handle is an
  *in-simulation* identity; the mapping to the durable string user id has to
  live somewhere explicit, not be assumed to be the same value.
- **Bit-exactness is currently achievable and is worth keeping.** The existing
  logic already uses only the permitted operations — no transcendental appears
  anywhere in `Shared.GameLogic`. Prediction plus reconciliation does not
  strictly require bit-exactness, since the server is authoritative and a
  divergence is corrected on the next snapshot; what it buys is that
  reconciliation fires on packet loss rather than continuously. Rule 5 exists to
  keep an asset that already exists.
- Arch's own multithreading is out of scope here. The tick loop's threading
  model is unchanged by this decision and is a separate one.

**Revisit if** Arch gains a supported, exercised Unity path with an AOT
statement, in which case sharing systems (not just functions) becomes worth
re-costing — or if the golden vectors prove impossible to run under the Unity
Test Runner in CI, which would remove the mechanism this ADR relies on and put
rule 4 back in question.

---

## ADR-11 — Arch under NativeAOT: hints are generated, CommandBuffer is not used

**Status:** accepted 2026-08-11, measured. Constrains ADR-10 decision 1.

**Context.** ADR-10 chose Arch as the server's entity storage before anyone had
established that Arch works in a NativeAOT binary. The server ships NativeAOT
(`GameServer.csproj`, `<PublishAot>true</PublishAot>`), and every other dependency
in that file carries a comment justifying why it is trim/AOT-clean. Arch's
documentation says nothing about AOT at all.

A throwaway spike (`_spike/ArchAotSpike`, branch `spike/gameserver/arch-nativeaot`)
published Arch `2.1.0-beta` under the real project's AOT settings across nine
configurations and ran each native binary against a plain dictionary baseline.

**What was measured.**

`dotnet publish` succeeds **cleanly in every configuration**. The binary then
throws on the first archetype creation:

```
NotSupportedException: 'Identity[]' is missing native code or metadata.
```

All three usage modes fail — direct query, chunk iteration, command buffer. The
publish emits no warning that predicts it. This is precisely the failure mode that
survives `dotnet test`, because tests run on CoreCLR with a JIT.

The cause is that `Arch.Core.Chunk`'s constructor allocates one backing array per
component type through `System.Array.CreateInstance(Type, int)` — runtime,
`Type`-driven array creation. Under NativeAOT the array type `T[]` for a
user-defined struct exists only if ILC saw it constructed statically somewhere.

Statically constructing one array per component type in a `[ModuleInitializer]`
fixes it. With that in place:

| Mode | Result |
|---|---|
| direct query | works; state checksum identical to the baseline |
| chunk iteration | works; identical |
| source generator (`Arch.System`) | works; identical |
| **`CommandBuffer`** | **still throws** `NullReferenceException` inside `Arch.Core.World.Has<T>` |

Two residual hazards, both confirmed by experiment rather than reasoning:

1. **A missing hint is invisible.** Omitting one component type from the list
   produces no build error and no startup error — the binary crashes on the first
   tick that creates an archetype containing it. The spike demonstrates this by
   deliberately omitting `Stunned`. A rare archetype means a production crash.
2. **`CommandBuffer` is unusable**, and it is the idiomatic way to make structural
   changes (spawn, despawn, add/remove component) while iterating.

**Decision.**

1. **Arch stays** (ADR-10 decision 1 is unchanged). Where it runs, it is
   behaviourally identical to the baseline — the checksums match exactly, so this
   is a packaging problem, not a correctness one.
2. **The AOT hint list is generated or guarded, never hand-maintained.** Either a
   source generator emits one static array construction per component type, or a
   test enumerates every component type and fails when one is unhinted. A
   hand-written list whose omissions surface only in production is not an
   acceptable steady state. This is the load-bearing part of this ADR: without it,
   adding a component is a silent production hazard forever.
3. **`CommandBuffer` is not used.** Structural changes are deferred to an explicit
   phase outside iteration. At a 10–15 Hz tick this costs nothing that matters, and
   it removes a dependency on a code path that is broken in the shipping
   configuration.
4. **The `publish` CI job must run the binary, not merely build it.** A clean
   publish demonstrably does not imply a working binary here. A smoke run of the
   published artifact is what would have caught this, and is what will catch the
   next instance.

**Consequences.**

- Adding a component type is no longer a purely local act; it must reach whatever
  mechanism decision 2 settles on. That mechanism should fail loudly at build or
  test time, so the cost is paid once rather than per component.
- Arch remains at `2.1.0-beta`. This spike found one library bug that survives the
  documented workaround; treat further AOT-related breakage as likely rather than
  surprising, and re-run the spike on any Arch upgrade.
- The spike is throwaway and is not merged into the solution. It is retained on its
  branch as the reproduction for anyone revisiting this.

**Revisit if** Arch publishes an AOT statement or fixes `CommandBuffer`, or if the
hint mechanism turns out to be unable to see component types defined outside the
server assembly — which would be a stronger argument against Arch than anything
found here.

> **Amended by [ADR-12](#adr-12--the-server-goes-to-real-ecs-staged-under-the-constraints-adr-10-and-adr-11-set)
> (2026-08-14).** The server is moving to real systems over components, which is
> exactly the direction that makes this ADR's two failure modes more likely. Nothing
> here is relaxed: `CommandBuffer` stays out, and decision 2's guard becomes a
> per-commit obligation — a new component type gets its hint line in the same commit
> that introduces it. ADR-12 additionally rules out query shapes the reflection guard
> cannot enumerate, `Arch.System`'s source generator among them.

---

## ADR-12 — The server goes to real ECS, staged, under the constraints ADR-10 and ADR-11 set

**Status:** accepted 2026-08-14. Supersedes nothing; **narrows** ADR-10 decision 1 and
is governed by ADR-11.

**Context.** ADR-10 put Arch under the server as *storage* and stopped there: the layer
above it addressed the world as whole `EntityState` values, so `EcsWorld.Update(get, set)`
composed seven components into a struct on every read and wrote all seven back on every
write. No systems existed; `TickLoop.TickOnce` called a handler that addressed entities by
`string`. That is an ECS used as a dictionary with extra steps.

An analysis was commissioned on whether to finish the job. It recommended **not** to, on
three grounds, each of which is correct and none of which has gone away:

1. **NativeAOT is a landmine, not a gradient** (ADR-11). `World/ArchAotHints.cs` needs one
   `new T[1]` per component type; a missing line compiles clean, passes CI — which runs
   JIT'd and structurally cannot see the problem — and throws `NotSupportedException` on
   the first tick that creates that archetype in production. Real ECS multiplies
   archetypes, and the idiomatic tool for the structural churn systems want is
   `CommandBuffer`, which ADR-11 measured as still throwing with hints in place.
2. **It targets the wrong 20%.** `BENCHMARK.md` puts the serialization:AOI ratio at ~5:1
   and still lists "move serialization off the tick" as outstanding. The AOI hot loop is
   *already* component-native — `EcsWorld.GetEntitiesInRange` iterates
   `GetChunkIterator()` and tests `chunk.GetSpan<Position>()[i]`. Rewriting it would be
   rewriting the part that is already ECS.
3. **`Shared.GameLogic` cannot follow.** ADR-10 forbids Arch types in it, and the Unity
   client consumes it as pinned source (`sgl-v0.1.6`). Changing `in EntityState` /
   `in Vec2` signatures forces a lockstep client release for zero server throughput, and
   the golden vectors stay bit-exact only while those signatures hold.

The decision to proceed anyway was taken by the project owner with that analysis in hand.
This ADR records it, and records the constraints that make it survivable, so that a future
reader does not find ADR-10 and ADR-11 arguing against the code they are reading and
conclude the code is a mistake.

**Decision.**

1. **The server moves to real systems over components, in stages, one PR per stage**, each
   independently revertible and green: (1) the input path, (2) enemy AI, (3) the
   snapshot/AOI walk. Not one commit, and not a rewrite.
2. **`CommandBuffer` remains banned** — ADR-11 decision 3 is unchanged and is now
   load-bearing rather than incidental. Every structural operation a system needs goes
   through the existing deferred queue drained by `EcsWorld.ApplyStructuralChanges()` at
   one explicit point in `TickLoop.TickOnce`. One structural phase per tick; no hidden
   mid-iteration mutation. The queue's operation kinds are to be enumerated in the module
   `CHANGELOG` as they are added, because "which structural ops exist" is exactly the
   question a reader debugging an ordering bug will ask.
3. **Every new component type gets its AOT hint line in the same commit that introduces
   it**, and `ArchAotHintTests` — which reflects over every component struct in the
   assembly — must still pass. This is ADR-11 decision 2 restated as a per-commit
   obligation because the migration is what makes it likely to be forgotten.
4. **Generic query shapes that the reflection guard cannot see are not permitted.** In
   particular, `Arch.System`'s source generator produces query types the existing test
   does not enumerate; adopting it would create unhinted surface. Either the guard is
   extended to cover the generated shapes first, or the generator is not used.
5. **`Shared.GameLogic` stays engine-free and ECS-free.** ADR-10's boundary is unchanged.
   Systems read components and convert at the call boundary when invoking shared
   arithmetic; `in EntityState` / `in Vec2` signatures are frozen for the duration.
   `MovementSystem.Integrate`'s FMA-denying local split is not to be touched. Golden
   vectors stay bit-exact.
6. **No stage may change the wire.** Snapshot output stays byte-identical. Where the old
   code had an observable accident, the accident is preserved and pinned by a test named
   after what it is, rather than quietly corrected under cover of a refactor.
7. **Speed is not claimed without measurement.** Stage 3 in particular is structural
   clarity, not throughput: its inner loop is already chunk-iterating and compose-free.

**Consequences.**

- The archetype count is now a thing to watch. Today the server has two archetypes
  (with and without `PlayerTag`) and eight component types. If a design needs more
  archetypes than the hint list can practically track, that is a stop-and-report
  condition, not something to ship.
- CI's value against this class of bug is limited by construction — it runs JIT'd. The
  `publish`-and-run smoke job from ADR-11 decision 4 is the only automated thing that can
  catch a missing hint, which raises its priority.
- The analysis's second point stands and is not answered by this work: the tick's largest
  term is still serialization, and no stage here touches it. Moving serialization off the
  tick remains the highest-value outstanding item and is tracked separately.

**Revisit if** a stage lands and measurement shows no benefit at its own level (in which
case stop and keep what has landed — each stage is revertible precisely so that this is
cheap), or if Arch fixes `CommandBuffer` (which would relax decision 2 but not decision 3),
or if the archetype count outgrows the hint mechanism.

**Stage log.**

| Stage | Landed | Component types added | Structural op kinds | Measured at its own level |
|---|---|---|---|---|
| 1 — input path | PR #79 | none | add, remove (unchanged) | 6 762 858 → 192 984 B/tick at 200 players (35×) |
| 2 — enemy AI | PR #81 | `EnemyAi` (hinted same commit) | add, remove (unchanged) | 367 → 172 B/tick (−53%), but only −0.36% of a 50-player tick |
| 3 — snapshot/AOI | PR #83 | none | add, remove (unchanged) | 21 692 → 21 628 B/tick at 50 players (−0.3%); noise at 200. **No throughput win, as predicted.** |
| 4 — serialization off the tick | PR #86 | none | add, remove (unchanged) | tick-thread alloc 192 935 → 32 B/tick at 200 players. Work moved, not removed; wall-clock unmeasurable here |
| 5 — query-driven systems | PR #87 | `EnemySpawnState` (hinted same commit) | add, remove (unchanged) | **nothing: 108 B/tick both sides.** Commissioned as architecture after the performance premise was disproved |

Two things stage 2 settled that this ADR could only predict:

- **Spawn and despawn did not need a new structural op kind.** The prediction was that
  entity creation and destruction inside systems would force the deferred queue to grow;
  it did not. `EnemyAi` is applied at creation, so it rides on the existing *add* as a
  tag payload rather than as an add-component operation. Decision 2 holds unmodified and
  `CommandBuffer` is still not needed for anything.
- **The hint guard fires in practice, not just in principle.** Deleting the single
  `new EnemyAi[1],` line builds clean and fails `ArchAotHintTests` naming the type. That
  is the second time this has been demonstrated on real code (the first was `Locomotion`
  under ADR-11), and it is the evidence decision 3 rests on.

Stage 2 also produced the first instance of the rule in decision 7 biting: it measures a
53% cut of the phase it targets and **0.36% of the tick**, because serialization dominates
by two orders of magnitude. That is recorded rather than dressed up, and it strengthens
rather than weakens the case that the outstanding item is moving serialization off the
tick.

**Stage 3 closed the migration, and confirmed the analysis was right about it.** The AOI
inner loop was already chunk-iterating and compose-free, and stage 1 had already removed
its per-client allocation, so there was nothing left to win and nothing was won: −0.3% at
50 players, noise at 200. Decision 7 was written for exactly this and the result is
recorded as it came out.

What stage 3 did produce is the precondition for the work that actually matters.
Serialization still runs inside the tick, and it could not be lifted out while encoding
was interleaved with locked per-viewer world reads — there was no moment at which a
viewer's snapshot input existed independently of the world. There is now: the broadcast
gathers every viewer under one read lock, then encodes outside it, so the encode phase
holds no world reference and no lock. **A stage 4 that moves serialization off the tick is
therefore reachable, and is the only remaining item with a measured case behind it**
(`BENCHMARK.md` §9, and the ~5:1 serialization:AOI ratio the original analysis cited).

Net across the first three stages: the input path was worth doing (35× less allocation per
tick), the AI and snapshot stages were worth doing for structure and not for speed, and
the thing the analysis said was the real 80% was left for stage 4.

**Stage 4 did it, and in doing so found the premise was wrong.** Encoding and
serialization now happen on each connection's own write task; tick-thread allocation at
200 players fell from 192 935 to 32 B/tick. But splitting the old phase B by hand first
showed the ratio the analysis cited does not reproduce: at 200 players the AOI gather is
~874–1177 µs/tick, `SnapshotDeltaState.Encode` is ~998–1272 µs/tick, and protobuf
`ToByteArray` is **~79–144 µs/tick — 4–6% of the tick, not 80%**.

That reframes what is left. The two dominant terms are:

1. **The brute-force AOI scan**, O(viewers × entities), which the extension-seam table has
   always listed as "spatial grid / quadtree" for production. At 200 players it is 40 000
   distance tests per tick.
2. **Delta/message building inside `Encode`**, which allocates 134 699 B/tick at 200
   players, almost entirely `EntitySnapshot` objects. That is a **pooling** problem, not a
   threading one.

Neither is an ECS problem, which is the honest place for this migration to stop on
performance grounds. Further work on tick cost should be argued on its own terms rather
than as a continuation of this ADR.

**Stage 5 was then commissioned anyway, deliberately and with that measurement in hand**,
as an architectural requirement rather than a performance one — the user's position being
that the shape should be right before gameplay is written against it. It measures exactly
nothing (108 B/tick on both sides; steady-state population is 4–6 enemies, so a chunk loop
cannot show anything) and the record should say so plainly rather than dress it up. What it
changed:

- The core stopped naming the gameplay: `CountWith<TTag>` / `QueryWith<TTag>` replaced an
  `EnemyCount` property and an enemy-named query on `EcsWorld` and `WorldWriter`.
- Systems that are per-entity-linear iterate chunks; the two that are not — spawn, which
  creates, and reap, which decides per entity and then performs a structural change — keep
  handle access with the reason stated at each.
- Ordering became declared rather than call-ordered, via a `SystemSchedule` that rejects
  ambiguous orders at construction.
- **The rule this ADR states was already broken behind the seam on day one**, and is now
  enforced. `EnemySpawnSystem` kept its wave accumulator and id counter in private fields;
  they are a component now, and `SimulationStateArchitectureTests` fails the build on any
  mutable instance field in a phase or system. It was verified to fire by reintroducing the
  original field. An honour-system rule in an ADR is worth very little; this is the third
  guard in the module to be demonstrated rather than assumed.

**On parallelism, which the release gate now requires.** Stage 4 already put encode and
serialize on per-connection threads — that is real server multithreading, and the tick
thread's share of a snapshot is now a gather plus a flag. Parallel *simulation* is a
different question, and stage 5's `ComponentAccess` is the part that makes it expressible:
two systems may run concurrently exactly when neither writes what the other reads or
writes. Two preconditions are recorded and are **not** met today, both verified in the
code: `EcsWorld._structural` is an unsynchronised `List<StructuralOp>` safe only because
one thread mutates it under the write lock, and the iteration-depth guard that decides
immediate-versus-deferred structural changes is `[ThreadStatic]`, so it would become a
per-worker fact rather than a property of the world. Neither may be left to be discovered
by the change that first spawns a worker.

> **Both preconditions were fixed on 2026-08-17, ahead of any worker — as this paragraph
> asked.** The paragraph above is left as written; what changed is below.
>
> - **The structural queue is per worker slot** and drains in slot order. Locking a single
>   shared list would have fixed the data race and left the real hazard: ops are replayed
>   through `Arch.Create`, so queue order sets creation order, which sets chunk layout,
>   iteration order, and the order floats accumulate. A shared queue makes that arrival
>   order — the golden vectors would break intermittently rather than never.
> - **Deferral is now a world-level flag**, `_parallelRegion`, set for the span of a
>   parallel region. `[ThreadStatic] _iterationDepth` is kept for the same-thread
>   re-entrancy it always caught; it is simply no longer the whole rule. Inside a region
>   every worker defers, whether or not that particular worker is mid-query.
> - **`EcsWorld.UpdateComponentsParallel(workerCount, body)`** runs a body on N real
>   threads so both fixes are exercised rather than asserted, and
>   `GameServer.Tests/World/ParallelRegionDeterminismTests.cs` pins the result: identical
>   world across 25 runs, across worker counts, with replay in slot order and not in
>   completion order. The suite was verified to fire by reintroducing the shared queue —
>   exactly the two order-sensitive tests fail and the other eight still pass.
>
> **The schedule still runs serially, and that is now a workload decision rather than a
> safety one.** Of the three systems in it, two declare `Structural` and are excluded from
> concurrency by `IsDisjointFrom`'s first line; the third has nothing to pair with. Every
> pair conflicts, so a parallel step would serialise them anyway and pay for the threads —
> and decision 7 above forbids claiming speed without measurement. The condition to revisit
> is two or more non-structural systems with disjoint component sets, which arrives with
> gameplay content, not before it.

One correctness result is worth separating from the performance story: moving encoding to
the moment of writing **fixed a pre-existing data-loss bug**. The old order encoded on the
tick — advancing the delta encoder's `_lastSent` — and only then handed the envelope to a
bounded channel that drops the oldest under load, so a dropped frame's updates were
recorded as sent and never retransmitted until the next keyframe. Lazy encoding makes a
frame that is never sent also never encoded.


---

## ADR-13 — Simulation runs at three configurable rates on one integer tick timeline; replication does not follow it

**Status:** accepted 2026-08-15. Extends ADR-12 (the ECS schedule it adds a rate dimension
to). Depends on #93 being fixed in the same change, for the reason in *Consequences*.

**Context.** The server ran one fixed 15Hz loop. Everything that needed to happen more often
than every 66ms — input, movement, hit detection — could not, and everything that would have
been fine at 200ms paid the 66ms rate anyway. The obvious move, raising the global tick to
60Hz, was rejected before it was tried: it quadruples the cost of every system whether or not
it benefits, and it quadruples snapshot bandwidth, which the measured 45.9 KB/s per client at
200 players (BENCHMARK.md) cannot absorb inside ADR-7's `< 50 KB/s` mobile budget.

**Decisions.**

1. **Three groups, named for responsibility, not frequency.** `Critical`, `World`,
   `Background`. The Hz behind each is configuration — `SIM_CRITICAL_HZ`, `SIM_WORLD_HZ`,
   `SIM_BACKGROUND_HZ`, defaulting to 60/15/5 — so a group named `Hz60` would be a lie the
   first time an operator tuned it. A system declares its group and nothing else about
   timing; `GAMESERVER_TICK_RATE` still works and means "every group at that one rate".

2. **One canonical base tick, derived rather than configured.** The base rate is the critical
   rate, and every other group must divide it exactly; `RunsOn` is `(tick - 1) % every == 0`.
   A configuration whose rates have no integer timeline — 60/25/5, whose true common base is
   300Hz — is **rejected at startup**, not accommodated. Accommodating it would silently run
   the server five times faster than anyone asked for.

3. **Integer scheduling, not float accumulators.** Accumulating wall-clock deltas and firing
   on a threshold drifts, makes "which tick did this run on" unanswerable, and makes a replay
   of the same inputs produce a different schedule. The tick number has to be an identity
   because input acknowledgement, cooldowns, reconciliation and replay are all expressed in
   it.

4. **Each group integrates with its own dt.** A system in the world group receives `1/15`,
   not `1/60`, because it runs 15 times a second. Handing it the base timestep while running
   it every fourth tick is the defining bug of a multi-rate scheduler and it is silent: every
   speed and duration in that group would be wrong by the rate ratio, with nothing to
   observe but "the game feels off".

5. **Durations counted in ticks are derived from the rate that advances them.** The attack
   cooldown is `ceil(500ms x rate)` base ticks and is compared against the base tick, so it
   is derived from the *critical* rate. A 500ms cooldown stays 500ms at 15, 30 and 60Hz.

6. **Group order is fixed Critical -> World -> Background, and is not configurable.** It
   encodes write ownership: on a tick where several groups are due, the faster group's writes
   land before the slower group reads them, so a slow group can never overwrite newer state
   with a value it computed from an older read. This is the cross-rate hazard the whole
   design has to answer, and the answer is an ordering rule rather than per-component
   arbitration.

7. **Replication is gated to the world rate, not the base rate.** Simulating at 60Hz does not
   mean sending at 60Hz. Sending every base tick would quadruple downstream bandwidth to
   deliver state the client interpolates across anyway, and would silently redefine the
   keyframe interval, which counts *snapshots*: 30 snapshots is 2 seconds at 15Hz and half a
   second at 60Hz. Simulation rate and replication rate stay separate concepts.

8. **Overload drops the backlog; it never chases it.** Past 8 base ticks of lag the loop
   resynchronises to now and counts what it discarded
   (`gameserver_tick_backlog_dropped_total`). A catch-up loop is worse than useless here:
   each catch-up tick costs more than the budget it reclaims, so a server that falls behind
   falls further behind. Simulation time then runs behind wall time under sustained overload,
   which is a bounded and measurable failure rather than a spiral.

9. **The background group ships empty, deliberately.** No system in the current simulation
   tolerates a 200ms scheduling delay without a visible behaviour change — enemy reaping
   looks like cleanup but is what stops a dead enemy from being observable in the snapshot
   built later in the same tick. Inventing a tenant so the group looks used would be shipping
   a regression to satisfy a diagram. The infrastructure is built, tested and documented with
   the rule for what may enter it.

**Consequences.**

- **The base tick advances four times faster in wall-clock time at the default.** The tick
  number on the wire is unchanged in format and meaning — it is still the authoritative
  simulation tick — but its *rate* is now a deployment-time property. That is exactly the
  defect #93 describes, and it is why the tick rate goes on the wire in the same change: a
  client that assumes 15 while the server runs 60 predicts wrongly, gets corrected by every
  snapshot, and the player sees rubber-banding rather than a misconfiguration.
- **A single-rate configuration is byte-for-byte the old server.** With one rate there is one
  timeline, the world group runs every base tick, snapshots ship every tick, and the held-input
  window collapses to a single tick — so movement is one step per packet, as before. The
  snapshot byte-identity digests and the enemy characterization tests pass unchanged, which is
  the evidence for this claim rather than the assertion of it.
- **Movement became continuous, and that is a real behavioural change at multi-rate.** The
  server now integrates the newest input once per critical tick and holds it for one world
  interval, instead of once per received packet. Without it a client sending at 15Hz against a
  60Hz server would move at quarter speed — speed would be a function of the client's send
  rate, which `MovementSystem` already documents as forbidden. A client that goes quiet coasts
  for at most one world interval (66ms at the default); an explicit deadzone input stops it
  immediately.
- **Per-server capacity is not claimed to improve.** The point of multi-rate is high frequency
  where it is needed and low frequency where it is not, not throughput. No capacity claim is
  made here, and the tick ceiling remains unmeasurable on the current hardware (ADR-7).


---

## ADR-14 — Agones owns the pod, Redis owns the lookup; the C# server's SDK is a stub and must be written over the HTTP sidecar

**Status:** accepted 2026-08-17. **Superseded in part — stages 1-4 have since shipped and are proven; see ADR-16.** The line below said "not yet implemented, nothing in this ADR has shipped"; that was true when written and stopped being true the same day, which is exactly the staleness this document keeps warning about. `HttpAgonesSdk` exists and reports Ready/Health/Allocate/Shutdown, the address is read from the sidecar, and a real client has joined an Agones-managed server. Stages 5-8 remain open.
Extends ADR-2 (whose allocation branch this is the missing half of) and is constrained by
ADR-1 (one writer per datum) and ADR-7 (the unknown player ceiling).

**Context.** The gateway's Agones integration is real and tested. `AgonesAllocator` POSTs to
`/apis/allocation.agones.dev/v1/namespaces/%s/gameserverallocations` through a Kubernetes REST
client (`gateway/registry/agones_allocator.go`), with unit tests and an end-to-end
`enter_world_alloc_test.go`; it is off by default (`--allocator none`) and opted into with
`ALLOCATOR=agones`. ADR-2 already records what that allocator does when a map is full — the
registry allocates a second instance and registers it under the same `map_id`
(`gateway/registry/registry.go`, the `s.allocator != nil` branch; ADR-2 cites lines 77-91,
which have since moved to ~237-249).

The game server's half does not exist. `GameServer/Agones/AgonesSdk.cs` is 58 lines and
contains an `IAgonesSdk` interface (`ReadyAsync`/`ShutdownAsync`/`AllocateAsync`/`HealthAsync`),
a `NoopAgonesSdk` that returns `Task.CompletedTask` from all four, and an `AgonesHealthLoop`
that dutifully pings the no-op. `grep -rn ": IAgonesSdk"` returns exactly one implementation
across the whole solution, and it is the no-op. There is no HTTP client and no reference to the
sidecar port anywhere in the module. `Program.cs` is honest about it and says so at startup:

> `--agones/AGONES_ENABLED is set but has NO effect: the C# server still uses the no-op Agones
> SDK (no Ready/Health/Shutdown is reported to the sidecar). Do not rely on Agones health checks
> for this server yet.`

That warning is the fact this ADR formalises. `--agones` parses (`Program.cs:65`), logs, and
changes nothing.

Four gaps follow from it, and they are the whole case for doing the work:

1. **A map with no server means the client cannot enter.** `FindServer` returns
   `ErrNoServerAvailable` and `MsgEnterWorld` fails. Someone has to start a process by hand.
2. **A full map means rejection.** ADR-2's MVP policy is one live server per `map_id` and a
   full map refuses joins. The allocation branch that would produce a second one is written and
   tested — but with no game server able to report Ready, allocating produces a pod that never
   becomes allocatable. The branch is a no-op in practice.
3. **A crashed server is removed but not replaced.** `RegistrationService` re-arms a 15s Redis
   TTL every 5s, so a dead server leaves the registry within seconds — that half works. Nothing
   then brings a new one up.
4. **Dungeon-per-party instancing cannot exist at all.** `--mode=dungeon` currently changes one
   thing: the disconnect hold window, 60s instead of 30s (`Program.cs:358`). There is no
   allocate-per-party, no instance lifecycle, no shutdown. Instanced dungeons are a headline
   feature of the design and they are blocked on this, not on gameplay code.

Two more facts belong in the record before any decision is read.

**The manifests would currently kill the server.** `deploy/agones/` holds nine manifests (map
and dungeon fleets, autoscaler, allocation, dev and prod variants). `fleet-map-dotnet-dev.yaml`
sets `health: initialDelaySeconds: 10, periodSeconds: 5, failureThreshold: 3` and does **not**
set `disabled: true` — no manifest in the directory sets it. Deploying that file today gives a
pod that never pings, fails three checks, and is killed and restarted forever.

**The cluster is running the deleted server.** `kubectl get fleets -n rpg-realtime` shows
`map-servers-dev` and `dungeon-servers-dev`, both Ready, both 13 days old, both ALLOCATED 0, on
image `rpg-mmo/gameserver:dev` — the **Go** game server, deleted from the repo in `670a803
feat(migration): remove Go gameserver, C# .NET 10 is primary`. The cluster context is
`docker-desktop`, not a VPS.

**Decision.**

1. **Implement `IAgonesSdk` against the Agones HTTP sidecar on `localhost:9358`, not against
   the official Agones C# SDK.** That SDK is gRPC and would pull `Grpc.Net.Client` and its
   transitive tree into a module whose `CLAUDE.md` states "NativeAOT compatible — no
   reflection-based serialization" and "No other external dependencies (keep the dependency
   tree minimal)". `System.Net.Http` is in-box, the sidecar's REST surface is four POSTs, and
   this module's JSON path is already AOT-safe through source generators. A working reference
   exists in this repo's own history — `git show 670a803^:backend/gameserver/agones/sdk.go`,
   101 lines plus a 63-line test, implementing Ready/Shutdown/Allocate/Health and a health
   loop over the official Go SDK. The shape is known; only the transport changes.
2. **Agones owns pod lifecycle; Redis owns the `map_id -> server` lookup.** These are two
   sources of truth about whether a server is available — Agones writes GameServer state, the
   server writes its own Redis entry — and ADR-1 forbids two writers for one datum. They are
   made to be two *different* data: Agones answers "does this pod exist and is it healthy",
   Redis answers "which address serves this map". Neither reads the other's answer.
3. **The server registers into Redis only after reporting Ready.** Registering first
   advertises an address that Agones may still be about to kill, which is precisely the
   ordering that would let the two writers disagree. Ready first, then register; on shutdown,
   deregister first, then `ShutdownAsync`.

   > **Correction, same day: this one was already true in code.** Written as though it were
   > work to do; it is not. `GameServer.RunAsync` completes the bind at
   > `GameServer/Server/GameServer.cs:349`, calls `ReadyAsync()` at 356, and only then
   > `_registration.StartAsync()` at 364 — and the descent already deregisters at 443 before
   > `ShutdownAsync()` at 450. What was actually missing was decision 1 alone: `Program.cs:365`
   > hardcoded `new NoopAgonesSdk()`, so the correctly ordered calls all went to the no-op.
   >
   > The decision stands as the rule; what it needs is a test pinning the order so a future
   > refactor cannot quietly invert it, not a restructure. Recorded rather than edited away
   > because "the ADR asked for work that was already done" is the error that sends someone
   > looking for it.
4. **The health loop only starts once a real SDK is wired.** `AgonesHealthLoop` running
   against `NoopAgonesSdk` produces reassuring log lines about pings that were never sent, and
   is worse than no loop. Until decision 1 lands, `disabled: true` goes on the dotnet fleet
   manifest's health block rather than being left to the restart loop to discover.
5. **The autoscaler is buffer-based on server count, not threshold-based on CCU.** ADR-7's
   per-server ceiling is unknown and not measurable on this hardware — the load generator
   shares the box and uses more CPU than the server under test. A buffer policy ("keep N ready
   servers spare") needs no such number. Any policy keyed on players-per-server is a guess
   until ADR-7's blocker is cleared, and must not be written as if it were measured.

**Staged work.** Each stage is independently landable and the earlier ones change no runtime
behaviour, which is deliberate — the SDK can be written and tested long before anything is
deployed against it.

| # | Stage | Size | Changes runtime? |
|---|---|---|---|
| 1 | `HttpAgonesSdk` over the sidecar, unit-tested against a fake HTTP handler | M | No — still selected only under `--agones` |
| 2 | Lifecycle wiring: Ready on listen, Health loop, Shutdown on drain | M | Yes, under the flag |
| 3 | Registration ordering per decisions 2-3, with a test that pins the order | S | Yes, under the flag |
| 4 | Deploy `fleet-map-dotnet-dev.yaml` and prove no restart loop over a sustained run | S | Deployment only |
| 5 | Enable `ALLOCATOR=agones` in dev and demonstrate an end-to-end allocation from `MsgEnterWorld` to a joined client | M | Yes |
| 6 | Dungeon instancing: allocate per party, lifecycle, shutdown | L | Yes |
| 7 | Buffer-based `FleetAutoscaler` per decision 5 | S | Yes |
| 8 | Retire `map-servers-dev` and `dungeon-servers-dev`, and delete the Go-image manifests | S | Deployment only |

Stage 5 is the first point at which anything is *proved*. Stages 1-4 reduce risk; they do not
demonstrate that the thing works.

**Consequences.**

- **Nothing here is demonstrated.** No C# server has ever reported Ready to an Agones sidecar
  in this project. Every claim above about what will happen after stage 1 is a design
  intention, and this ADR should not be cited as evidence that Agones works for the C# server.
  The evidence, when it exists, is the stage-5 result.
- **ADR-2's warning becomes reachable rather than theoretical.** Today the multi-instance
  hazard it describes — two servers under one `map_id`, two disconnected copies of the world —
  cannot occur, because allocation cannot produce a live server. Stage 5 makes it possible for
  the first time. ADR-2's follow-up "decide the map-fleet allocator policy" therefore stops
  being an M-sized cleanup and becomes a precondition of stage 5.
- **What is NOT decided: whether the realtime tier moves to Kubernetes.** Dev, staging and
  production all run `DEPLOY_MODE=containers` under docker compose today. Agones is a
  parallel path, not the deploy path, and the only cluster it has ever run on is
  `docker-desktop`. Adopting Agones for the realtime tier changes that tier's deploy story,
  its runner requirements and its rollback procedure. That decision is deliberately left open
  here — an SDK implementation does not commit the project to it, and the two should not be
  bundled into one change.
- **The health loop is a liveness contract, not a formality.** Once decision 4's `disabled:
  true` comes off, a tick loop that stalls long enough to starve the ping task is a pod
  restart. That is arguably the correct behaviour, but it is a new failure mode, and ADR-13's
  overload path — which drops the backlog and resynchronises rather than chasing it — is what
  keeps a merely-slow server from being killed as a dead one.

**Revisit if** the official Agones C# SDK ships an AOT-friendly non-gRPC path (which would
reopen decision 1), or if ADR-7's load-generator blocker clears and a real per-server ceiling
makes a CCU-keyed autoscaler defensible (decision 5), or if the realtime tier moves to k3s for
reasons unrelated to Agones — in which case the open question above is answered elsewhere and
this ADR should record where.

---

## ADR-15 — What it would cost to run the realtime tier on Kubernetes, and the dynamic-address problem that blocks it

**Status:** **proposed 2026-08-17, not accepted.** **Partly overtaken by ADR-16 (2026-08-17).** Decision 2 below — the server reads its address from the sidecar — was implemented and then found necessary but *not sufficient*: `status.address` is the node address and is not dialable by a client, so the advertised address is composed from the Agones-assigned port and a configured host. The blocking finding and decision-3 prerequisites stand as written; read ADR-16 before acting on this one. This ADR does not decide whether the
realtime tier moves to Kubernetes. It exists to write down what answering that question
costs, because ADR-14 left it open and nothing since has priced it. Extends ADR-14 (this is
its "what is NOT decided" bullet, expanded); constrained by ADR-1 (one writer per datum),
ADR-2 (one live server per `map_id`), ADR-3 (the gateway is a redirector) and ADR-7 (the
unknown per-server ceiling).

Only the recommendation in decision 2 is a recommendation. Everything else numbered below
is a statement of what would have to be true before the go/no-go is answerable at all.

**Context.** There are two deployment stories in this repo and they do not meet.

The one that runs: push -> `.github/workflows/cd.yml` -> bundle -> self-hosted runner ->
`docker compose up -d` -> smoke test. All three environments are on it — `gh api` reports
`DEPLOY_MODE=containers` for `dev`, `staging` and `production` alike, and the workflow
contains no `kubectl` invocation anywhere. Images are built on the runner as local tags
(`rpg-mmo/gateway:<sha>`, `rpg-mmo/gameserver-dotnet:<sha>`); the `build-images` job that
pushes to `ghcr.io/cuvara/` runs only for the `production` environment or on a manual
`build_images` dispatch.

The one that does not run: `kubectl apply` by hand into whatever context is current, then
Agones fleets that nothing allocates from. Three facts about it are worth stating plainly,
because the directory names suggest otherwise:

- **There is no k3s.** `kubectl config current-context` is `docker-desktop`, the single node
  is `docker-desktop` at `v1.34.1`, and there is no `k3s` binary on `PATH`.
  `backend/deploy/k3s/` holds `lib.sh`, `namespaces.yaml`, `setup-dev.sh`,
  `teardown-dev.sh` and `validate-manifests.py`; `setup-dev.sh` installs nothing and says so
  itself — "Every step is `kubectl apply`". The directory is named for an intention.
- **`backend/deploy/k8s/` does not exist.** The repo-root `CLAUDE.md` describes
  `k8s/base/{nakama,gateway,redis,postgresql}` and `k8s/overlays/{dev,beta,launch,growth}`.
  None of it has been written. The only Kubernetes manifests in the repo are the nine in
  `deploy/agones/`, and they cover the game server only.
- **The fleets that are up are unbuildable.** `map-servers-dev` and `dungeon-servers-dev`
  are Ready, 13 days old, `ALLOCATED 0`, on image `rpg-mmo/gameserver:dev` — the Go server
  deleted in `670a803`, whose `docker/Dockerfile.gameserver` went with it. PR #137 marked
  those manifests superseded rather than deleting them; that image cannot be rebuilt.

So Agones is not "half deployed". It is a parallel path that has never carried a player, and
the reason is not only the no-op SDK that ADR-14 addresses.

**The blocking finding: the game server cannot learn its own address.**
`fleet-map-dotnet-dev.yaml:35` sets `portPolicy: Dynamic`, so Agones assigns a host port per
GameServer and the real address exists only in the GameServer status. It is visible right
now:

```
$ kubectl get gs -n rpg-realtime
NAME                              STATE   ADDRESS        PORT   NODE
dungeon-servers-dev-2kdvr-zzmxb   Ready   192.168.65.3   7101   docker-desktop
map-servers-dev-kl485-gsmrh       Ready   192.168.65.3   7691   docker-desktop
```

Nothing in the game server can read those two columns. The fleet supplies eight environment
variables — `POD_NAME`, `JWT_SECRET`, `JOIN_TOKEN_SECRET`, `SIM_CRITICAL_HZ`,
`SIM_WORLD_HZ`, `SIM_BACKGROUND_HZ`, `REDIS_ADDR`, `LOG_LEVEL` — and `GAMESERVER_PUBLIC_ADDR`
is not among them, because no static value could be correct for a port assigned at
scheduling time. `IAgonesSdk` exposes `ReadyAsync`/`ShutdownAsync`/`AllocateAsync`/
`HealthAsync` and no way to read GameServer status. `Program.cs:101` therefore resolves
`publicAddr = --public-addr ?? GAMESERVER_PUBLIC_ADDR ?? addr`, and the manifest passes
`--addr=:9000`, so the server would advertise the hostless `:9000`.

Follow that value: `RegistrationService` writes it into Redis, `transfer/map_assign.go:49`
copies `srv.Addr` into the assignment, and `server.go:843` hands it to the client as
`MsgEnterWorldResp.ServerAddr`. The client dials `:9000` and fails. `Program.cs:154` detects
the hostless address and logs a warning; it is deliberately never fatal, which is right for
host mode and means Kubernetes gets no stop.

**This is why `ALLOCATED` is 0 and the dotnet fleet has never been deployed** — not merely
the health loop. PR #139, in flight, implements the four sidecar POSTs and adds no status
read; the interface is unchanged at four methods. It is necessary and it is not sufficient.

**The handshake, and where allocation sits inside it.** ADR-3's invariant is that the
gateway authenticates and redirects, and gameplay traffic never passes through it. Nothing
here changes that:

```
Client -> Gateway     MsgAuth { JWT }                     (JWT verified locally)
Client -> Gateway     MsgEnterWorld { MapID }
                      +-- registry lookup: which address serves MapID?
                      +-- miss/full -> AgonesAllocator POST gameserverallocations   <-- k8s enters here, and only here
                      +-- ...which returns a pod that must then register ITSELF
Gateway -> Client     MsgEnterWorldResp { ServerAddr, JoinToken }
Client -> GameServer  MsgJoinToken { Token }              (direct; gateway is gone)
GameServer -> Client  MsgSnapshot ... per tick
```

The allocation sits inside step 2 and nowhere else. Under Kubernetes every hop is unchanged
except one: `ServerAddr` stops being a value someone configured and becomes a value the
scheduler chose. That single change is the whole of the problem, and it lands on the one
step in the handshake that the gateway cannot verify — it forwards an address it never
dials.

**Decision.**

1. **The go/no-go is not decided here, and must not be inferred from ADR-14.** Implementing
   the Agones SDK does not commit the project to Kubernetes; deploying one fleet on
   `docker-desktop` does not either. What follows is the price list, not the purchase.

2. **The dynamic-address problem is decided, because it blocks either answer: the game
   server learns its own address by reading GameServer status from the sidecar.** Three
   options were considered.

   - **(A) Read GameServer status.** The Agones sidecar's REST surface on `localhost:9358`
     serves the GameServer object alongside the four POSTs PR #139 already implements. The
     server reads it after `ReadyAsync`, composes `status.address` with the port named
     `game`, and registers *that* into Redis. Cost is one more endpoint on `HttpAgonesSdk`,
     one AOT-source-generated JSON model of the fields actually used, and a fallback to
     today's resolution when the read fails or Agones is off — which is what running outside
     a cluster must keep doing.
   - **(B) `portPolicy: Static`.** `containerPort == hostPort`, so `:9000` becomes true and
     a host part is all `GAMESERVER_PUBLIC_ADDR` needs. It gives up what `scheduling: Packed`
     is for: one GameServer per port per node, and ADR-2's second-instance allocation branch
     collides with the first the moment two map servers want one node.
   - **(C) The gateway trusts the allocation response and writes the registry entry
     itself.** The allocation POST already returns the address and ports, so the gateway
     could skip the server's self-registration entirely. This is rejected on two counts.
     It puts two writers on one datum, which ADR-1 forbids — `RegistrationService` still
     writes its own entry — and it inverts ADR-14 decision 2, under which Redis answers
     "which address serves this map" and Agones answers "does this pod exist and is it
     healthy", with neither reading the other. It also discards the liveness signal:
     the entry's 15s TTL re-armed every 5s is what makes a crashed server vanish, and an
     entry the gateway wrote has nothing re-arming it.

   **(A) is the recommendation.** It is the only one of the three that leaves ADR-1's
   ownership and ADR-14's split intact: the server remains the sole writer of its own
   registry entry, and it asks Agones only "what address did you give me", never "which
   server serves this map".

   > One limit belongs with the recommendation rather than after it. `status.address` is
   > the **node** address — `192.168.65.3` above — routable from wherever the node is
   > routable and no further. The status read produces the correct value inside the cluster
   > and the correct *shape* outside it; making that address reachable by a phone on a
   > mobile network is a deployment fact about ingress and node public IPs, not something
   > the read solves.

3. **Six prerequisites stand between "Agones works" and "Agones means anything", and all of
   them are outside `deploy/agones/`.** They are what `docker compose up` currently provides
   for free:

   | # | What compose does today | What Kubernetes needs |
   |---|---|---|
   | 1 | `postgres`, `postgres-game`, `redis` on named volumes (`postgres-data`, `postgres-game-data`, `redis-data`) | StatefulSets and PVCs, plus a storage class that exists on the target node |
   | 2 | `./db/init-gamestate.sql` mounted as an initdb script; `./monitoring/{prometheus.yaml,grafana-dashboards.yaml,dashboards}` mounted into `lgtm` | ConfigMaps, and a first-run story for the initdb script that a PVC's second boot does not re-run |
   | 3 | `./modules` host-mounted into `nakama` for the `nakama.so` Go plugin | A host mount is not available; the plugin is baked into an image (`nakama-plugin.Dockerfile` exists) or delivered by an initContainer |
   | 4 | Seven secrets in a `umask 077` / `chmod 600` `.env` written by CD — `JWT_SECRET`, `JOIN_TOKEN_SECRET`, `POSTGRES_PASSWORD`, `REDIS_PASSWORD`, `NAKAMA_CONSOLE_PASSWORD`, `NAKAMA_SERVER_KEY`, `GRAFANA_ADMIN_PASSWORD` | Kubernetes Secrets, and a way for CD to write them that is not "echo into a file on the runner" |
   | 5 | Local image tags; `imagePullPolicy: IfNotPresent` resolves them because Docker Desktop shares the daemon's image store | A registry push per environment and pull credentials per namespace. A real node shares nothing with the runner's daemon |
   | 6 | `AgonesAllocator` falls back to `$KUBECONFIG` then `~/.kube/config`, so it allocates as the developer | In-cluster it needs a ServiceAccount with `create` on `gameserverallocations.allocation.agones.dev`. Without it, allocation returns 403 and ADR-2's branch fails at the one moment it matters |

   Item 6 is the one most likely to be discovered late, because `agones_allocator.go` is
   already written, already tested, and already works — on a developer's kubeconfig.

4. **Partial adoption is not a middle path, and would be the worst outcome available.**
   Moving the realtime tier alone leaves the data tier in compose, so the game server reaches
   PostgreSQL and Redis across the cluster boundary by `host.docker.internal` — which is
   exactly what `fleet-map-dotnet-dev.yaml` does today and exactly why it works only on
   Docker Desktop. Two orchestrators means two rollback procedures, two health stories, and
   a smoke test that has to know which half it is testing.

5. **No autoscaling policy in this ADR is keyed on players.** ADR-7's per-server ceiling is
   unknown and not measurable on this hardware, and ADR-14 decision 5 already settled the
   fleet autoscaler as buffer-based on server count for that reason. The same constraint
   binds an HPA, a node count and a tier sizing: anything derived from players-per-server is
   a guess until ADR-7's load-generator blocker clears.

**Honest size.** This is larger than ADR-14's stages 1-8 put together, and it is a
*precondition* for several of them rather than a follow-on. ADR-14 sizes stage 4 ("deploy
`fleet-map-dotnet-dev.yaml` and prove no restart loop") at S and stage 5 ("enable
`ALLOCATOR=agones` and demonstrate end-to-end allocation") at M. Those sizes are honest on
`docker-desktop`, where the image store is shared, the kubeconfig is the developer's and the
data tier is one `host.docker.internal` away. On any cluster that is not this laptop, stage 5
inherits every row of decision 3's table plus decision 2's status read. Reading the ADR-14
table as the cost of getting Agones into production would understate it by an order of
magnitude.

**What is explicitly not decided, and what would decide it.**

- **Whether the realtime tier moves to Kubernetes at all.** The deciding evidence is a
  reason compose cannot serve: a scaling need, a multi-node need, or a fleet lifecycle
  need that a `docker compose up` on one VPS genuinely cannot meet. No such need has been
  demonstrated. Dungeon-per-party instancing (ADR-14 stage 6) is the strongest candidate
  and has not been costed against a non-Kubernetes implementation.
- **Whether the whole stack moves or nothing does.** Decision 4 argues against a split;
  it does not choose between the two remaining ends.
- **Which cluster.** `docker-desktop` is a development artefact. k3s on a VPS, k3d, and a
  managed cluster have not been compared, and the tier costs in the root `CLAUDE.md` are
  ADR-7 estimates that assume neither.
- **Whether `backend/deploy/k3s/` should be renamed now.** It describes no k3s and applies
  into whatever context is current, which is a foot-gun independent of this decision.

**Consequences.**

- **Nothing here is demonstrated, and less is demonstrated than in ADR-14.** ADR-14 could at
  least point at a tested Go SDK to port. This ADR describes work that has no precedent in
  the repo at all: no StatefulSet, no ConfigMap, no Kubernetes Secret and no cluster-side
  RBAC has ever been written here.
- **ADR-14 stage 5 acquires a dependency it does not name.** On a real cluster it cannot
  succeed without decision 2's status read, because an allocated pod that advertises `:9000`
  is indistinguishable from no pod at all from the client's side.
- **The address problem is worth fixing even if Kubernetes is rejected.** A server that can
  be told its own reachable address is the general form of the bug, and `--public-addr`
  already exists for the compose case. Decision 2 makes the Agones case work the same way
  rather than adding a second mechanism.
- **The `k3s` directory name will keep costing time.** Every reader who finds it assumes a
  cluster provisioner and finds a `kubectl apply` wrapper.

**Revisit if** a scaling or lifecycle need arrives that compose demonstrably cannot serve
(dungeon instancing is the likeliest), or if ADR-7's blocker clears and a measured ceiling
makes a fleet-sizing argument possible, or if the data tier moves to managed services — in
which case decision 4's objection to a split largely dissolves and the realtime tier could
move alone.
## ADR-16 — The realtime tier runs on Agones, and the address it hands a client is measured, not configured

**Status:** accepted 2026-08-17, **proven end to end on k3d**. Implements ADR-14 stages 1-4,
supersedes ADR-15's decision 2 in one respect (the status read alone is not sufficient), and
answers ADR-15's open "which cluster" question for local development. Constrained by ADR-1
(one writer per datum), ADR-2 (one live server per `map_id`), ADR-3 (the gateway is a
redirector) and ADR-7 (the unknown per-server ceiling).

**What is proven.** A client completed the full production flow — Nakama device auth,
`gateway_token` RPC, `MsgAuth`, `MsgEnterWorld`, direct dial, `MsgInput`/`MsgSnapshot` — against
an **Agones-managed C# game server**, at an address only Agones could have supplied:

```
PASS  gateway_auth      map=map_01  server=127.0.0.1:7097 (tcp)
PASS  gameserver_join   snapshots=15 (keyframes=1 deltas=14) final_x=4.83 ack_tick=10
SMOKE=PASS
```

The run used `--strict-addr`, so the smoke test was forbidden from rewriting a listen-style
address to loopback. That matters: without it the harness silently repairs the exact defect
this ADR exists to fix, and reports PASS while a real client fails.

A GameServer also reached `Allocated` **without any gateway allocation**, because the server
reports `Allocate` to the sidecar on first player join (`NotifyAgonesAllocatedOnce`). "A pod is
Allocated" is therefore not evidence that the gateway's allocation path ran.

**Decision 1 — Docker Desktop's Kubernetes cannot host this architecture; k3d can.**
Docker Desktop publishes *Docker* ports to the host and does **not** publish Kubernetes
`hostPort`. Measured: an Agones GameServer with `portPolicy: Dynamic` received `hostPort: 7306`
on its pod spec, answered from inside the cluster, and was unreachable from both Windows and
WSL2, with a compose port reachable at the same moment as a control.

| target | Windows | WSL2 | in-cluster |
|---|---|---|---|
| k8s hostPort `127.0.0.1:7306` | FAIL | FAIL | — |
| node IP `192.168.65.3:7306` | FAIL | FAIL | PONG |
| compose `127.0.0.1:8000` | OK | OK | — |

Under ADR-3 the gateway hands `ServerAddr` to the client verbatim and the client dials the game
server directly, so an address routable only inside the cluster means no client can ever join an
Agones-managed server. k3d works because a k3d node is a Docker container and its published
range goes through Docker's own port publishing. Cluster `rpg-dev` publishes **7000-7100**, and
Agones' `MIN_PORT`/`MAX_PORT` are set to match — a mismatch there hands out ports outside the
published range while every component still reports healthy.

**Decision 2 — the advertised address is composed: port from Agones, host from configuration.**
ADR-15 decided the server reads its address from the sidecar (`GET /gameserver`) and registers
it. That is necessary and **not sufficient**: `status.address` is the *node* address
(`172.20.0.3` on k3d) and is not dialable by a client. The port cannot come from configuration —
it is assigned at scheduling time and only the status read can know it. So:

> **host** = `GAMESERVER_ADVERTISE_HOST` if set, else `status.address`; **port** = always the
> Agones-assigned port of the `game`-named port. On a failed read the override is ignored
> entirely rather than composed with a configured port, because an address that was never
> assigned to anything is worse than an obviously wrong one.

The server remains the sole writer of its own registry entry (ADR-1); it asks Agones only "what
address did you give me", never "which server serves this map".

**This works because there is one node and one published range.** On a multi-node cluster the
correct host differs per pod and a single fleet-level env var cannot express it; the answers are
an ingress or a per-pod value via the downward API, and neither exists in this repo.

**Decision 3 — ADR-2's invariant is now enforced in code, not merely warned about.**
`FindServer` allocates **only when a map has zero live servers**. When servers exist but are all
full it returns `ErrNoServerAvailable` without allocating. The previous behaviour — allocate a
second instance for a full map — would have produced two disconnected copies of one world the
first time Agones actually worked. Refusing a join is a loud, bounded failure; a silently split
world is not.

**Decision 4 — the join token is minted last.** It is single-use, `sid`-pinned and lives 30s.
After allocating, the gateway waits (bounded, default 15s) for the pod to publish its **own**
registry entry and mints from that entry, so the 30s starts when the address is real. The
gateway never writes a server's entry: an entry it wrote would have nothing re-arming its 15s
TTL, and that TTL is what makes a crashed server vanish. A timeout returns a distinct retryable
error. The wait is refused at startup if it approaches `pongTimeout - pingInterval`, because it
blocks the connection's read loop, which is also what records `MsgPong`.

**Consequences.**

- **Allocated pods are never reclaimed.** Agones has no un-allocate and this project has no
  `Deallocate` path, so an Allocated GameServer leaves that state only by being shut down.
  Observed directly: a fleet scaled to 0 kept its Allocated pod. Single-flight per `map_id`
  bounds the leak per gateway instance; two gateway instances still allocate one pod each.
- **The map-fleet allocator policy (ADR-2's open item) is still open**, and is now the binding
  question rather than a theoretical one: fleet pods self-register at boot, so a Ready pod is
  already in the registry and `FindServer` finds it without allocating; and a fleet hardcodes
  `GAMESERVER_MAP_ID`, so an allocation for another map would hand back a server for the wrong
  one. Whether the allocation branch can fire at all for map servers is under experiment.
- **The deploy path does not change.** Dev, staging and production remain
  `DEPLOY_MODE=containers`. Agones is proven, not adopted; ADR-15's decision-3 prerequisites
  (StatefulSets, ConfigMaps, registry, RBAC) are untouched and still stand between this and a
  real cluster.
- **`--allocator-transport` is inert**, since the allocation response is now used only for its
  `ServerID`.

**Operational facts worth not re-deriving.** Pods reach the compose data tier as
`host.k3d.internal`. The gateway container reaches the API server by joining network
`k3d-<cluster>` and using `https://k3d-<cluster>-serverlb:6443`, which is in the API
certificate's SAN list — `host.docker.internal` is not, and client-go verifies by default. k3d
does **not** share the Docker image store, so local tags must be imported or
`imagePullPolicy: IfNotPresent` silently falls through to a registry pull. Image tags here lag
the branch routinely; verify `org.opencontainers.image.revision` against the commit under test
before believing a deploy.

**Revisit if** the tier moves to a multi-node cluster (decision 2's host override stops
expressing the answer), if a `Deallocate`/reap story is needed, or if ADR-7's load-generator
blocker clears and a measured per-server ceiling makes a CCU-keyed policy defensible.

---

## ADR-17 — On k8s, every component is one replica and every gateway rollout is a join outage; that is accepted for dev and must be re-decided above it

**Status:** accepted 2026-08-18, as a **statement of posture**, not a change to any manifest.
Nothing here alters the deployment; it records what the deployment already is, so that
"the realtime tier runs on Kubernetes" (ADR-16) is not read as "Kubernetes is providing
availability". It is providing scheduling and lifecycle. Constrained by ADR-1 (one writer
per datum), ADR-2 (one live server per `map_id`), ADR-3 (the gateway is a redirector),
ADR-4 (Redis is a system of record, not a cache) and ADR-6 (the ≤30s gameplay loss window).
Extends ADR-16, which proved the tier works and did not price its availability.

### Context

ADR-16 proved a real client can join an Agones-managed server on k3d. What it did not
state is that the resulting deployment has no redundancy anywhere, and that one of its
correctness fixes — `strategy: Recreate` on the two `hostPort` workloads — converts every
deploy into a planned outage of the join path. Both facts are individually deliberate.
Together they are the availability story of the tier, and until now nothing wrote them
down in one place, so the first reader of `backend/deploy/k8s/` could reasonably assume
otherwise.

### Current state

Read from the manifests on `develop` and confirmed against the live cluster
(`kubectl --context k3d-rpg-dev get deploy,sts -A`, 2026-08-18):

```
NS                 KIND          NAME             REPLICAS   READY   STRATEGY
rpg-k8s-realtime   Deployment    gateway          1          1       Recreate
rpg-k8s-data       Deployment    nakama           1          1       Recreate
rpg-k8s-data       StatefulSet   redis            1          1       -
rpg-k8s-data       StatefulSet   postgres-meta    1          1       -
rpg-k8s-data       StatefulSet   postgres-game    1          1       -
```

Plus the Agones Fleet `map-servers-dotnet-k8s`, also at 1, for a reason of its own (ADR-2:
every replica carries the same `GAMESERVER_MAP_ID`, so a second replica is a second live
server for `map_01`).

| Component | Owns | Loss of it |
|---|---|---|
| `gateway` (`app/40-gateway.yaml`) | Nothing durable — sessions and the registry live in Redis, so any replica could serve any client | No `MsgAuth`, no `MsgEnterWorld`. In-progress sessions untouched |
| `nakama` (`data/nakama.yaml`) | Accounts, economy, leaderboards, and the `gateway_token` RPC that is the flow's first hop | No new `gateway_token`. JWTs already minted stay valid until expiry |
| `redis` (`data/redis.yaml`, `data/redis.conf`) | Sessions (TTL), server registry `servers:*`, event stream `events:*`; `maxmemory-policy noeviction` set explicitly (ADR-4) | Joins fail. Gameplay does not: `RegistrationService` wraps every registry call, logs and retries off the tick loop, and **every heartbeat is also a repair**, so a wiped Redis self-heals within one heartbeat interval |
| `postgres-game` (`data/postgres-game.yaml`) | `player_states` — authoritative position/HP, written only by the game server (ADR-1) | Gameplay continues; `AsyncSaver.SaveAllAsync` catches per-player, increments `gameserver.player.saves{status="error"}` and carries on, so the loss is **silent to the player and visible only in metrics** |
| `postgres-meta` (`data/postgres-meta.yaml`) | Nakama's own database; migrated by `nakama migrate up`, never by us (ADR-1) | Nakama cannot authenticate |

One PVC each, `ReadWriteOnce`, no standby, no replica.

**Why `Recreate` is on the two `hostPort` workloads.** The gateway binds `hostPort: 7000`
and Nakama binds `hostPort: 7001`, because k3d's serverlb publishes `7000-7100` onto the
host and nothing in the default NodePort range `30000-32767` — a NodePort Service here is
allocated, printed by `kubectl get svc`, and unreachable. A hostPort is a node-level
resource, so under RollingUpdate on a single node the replacement pod cannot be scheduled
until the outgoing one releases the port, while RollingUpdate will not terminate the
outgoing one until the replacement is Ready: `kubectl rollout status` sits on
`1 old replicas are pending termination` against a Pending pod whose event reads
`node(s) didn't have free ports for the requested pod ports`. It passes the **first**
deploy, when the old pod has no hostPort yet, and wedges every deploy after it.

**Why in-progress gameplay survives a gateway restart.** Verified in code, not assumed.
Under ADR-3 the gateway hands back `{ServerAddr, JoinToken}` and leaves the path; the
client dials the game server directly. The game server verifies the join token itself —
`JwtKeyring.Parse(options.JoinTokenSecret)`, then `Verify`, a server-id claim check and an
in-process JTI replay tracker (`GameServer/Server/GameServer.cs`) — and makes no call to
the gateway at any point in a session. The blast radius of a gateway restart is joins.

### Decision

**1. `Recreate` stays, and the outage it causes is accepted for dev.** It is the only
strategy that terminates on a single node with a hostPort. Reversing it to RollingUpdate
"because that is the default" reintroduces a deadlock that passes once and then blocks
every subsequent deploy, including CD's.

**2. The cost is stated where an operator will meet it, not left to be rediscovered.**
Every gateway rollout drops the join path entirely; so does every Nakama rollout. A player
whose connection drops during either window cannot reconnect, because a reconnect needs a
fresh `gateway_token` **and** a fresh join token — join tokens are single-use and `sid`-pinned
(ADR-16 decision 4). The prose lives in `backend/deploy/k8s/README.md` §Availability posture,
next to the manifests it describes.

**3. The window is not measured, and is not claimed to be small.** It is bounded below by
the outgoing pod's termination and the incoming pod's readiness (`initialDelaySeconds: 2`,
`periodSeconds: 5` on the gateway) and above by the default 30s termination grace period,
plus an image pull when the tag is not already on the node. Under ADR-7's standing rule an
unmeasured figure is not quoted as if measured, so no number is written down. Measure it by
polling `127.0.0.1:7000` from the host across a `kubectl rollout restart`; not from
`kubectl rollout status`, which reports the pod and not the port.

**4. Nothing above dev inherits this shape by promotion.** Three questions must be answered
before a tier that is not this one:

- **The gateway's hostPort, before any multi-replica gateway.** The hostPort is what forced
  `Recreate`, and it also pins the pod to a node, capping the Deployment at one replica per
  node. The real-cluster answer is a LoadBalancer Service or an Ingress, at which point the
  hostPort and `Recreate` both disappear. Removing it is **necessary and not sufficient**:
  ADR-16 records that single-flight per `map_id` is per gateway instance, so two replicas
  racing on a cold map allocate one GameServer each and Agones has no un-allocate. Answer
  both, or the second gateway replica leaks a pod per cold map.
- **Redis persistence and replication.** ADR-4 rules out treating this Redis as an evictable
  cache, which also rules out the reflex answer of "add a read replica and let it lag": the
  registry and the event stream are systems of record. The decision is which of ADR-4's
  split path and a Sentinel/managed-Redis topology comes first, and it is a decision, not a
  replica count.
- **The two PostgreSQL instances.** One PVC each, no standby; recovery is restore-from-backup
  at backup RPO (`backend/deploy/docs/DATABASE.md`, `DISASTER-RECOVERY.md`). Accepted for dev,
  and the thing that most obviously does not survive contact with real accounts and economy,
  which `postgres-meta` owns.

**5. This ADR decides nothing about the deploy mode.** ADR-16's position stands: dev runs on
k8s; staging and production remain `DEPLOY_MODE=containers` and reach none of this.

### Consequences

- **CD deploys are outages.** A push to `develop` with `vars.DEPLOY_MODE=k8s` runs `dev-up.sh`,
  which restarts the gateway whenever the image pin changes. Any client-side or load
  measurement running across a deploy will see connection refusals on the join path that are
  not a defect.
- **"Runs on k8s" cannot be cited as an availability property** of this project anywhere —
  docs, issues, or a pitch. There is exactly one of everything.
- **A rolling-update-shaped fix is not available while the hostPort is.** Anyone reaching for
  `maxSurge`/`maxUnavailable` here is fixing the symptom of the port binding.
- **The fleet's single replica is a separate constraint with a separate cause** (ADR-2, map
  assignment is fleet-wide) and is not solved by anything in this ADR. It is load-bearing,
  not a capacity dial: measured on k3d 2026-08-18, scaling `map-servers-dotnet-k8s` from 1 to
  2 put the new GameServer in `Ready` at t=5.38s and **both** pods into `servers:map:map_01`
  within a second of that, with no allocation involved, because the C# server self-registers
  right after `ReadyAsync` rather than on allocation — and `FindServer` then hands clients the
  least-loaded of the two, i.e. the second copy of the world. (5.38s is pod-start latency, not
  a capacity figure.) So the answer to "when does the single replica stop being necessary" is
  **#151** — gate self-registration on `Allocated` rather than `Ready` — not a larger fleet.
  #151 unlocks `replicas > 1` **for one map only**: allocation targets a fleet and every pod
  in it still carries the same `GAMESERVER_MAP_ID`, so a second *map* remains unserved
  (`ErrFleetMapMismatch`) until map id is per-pod. Two separate unlocks, not one.
  Absent a FleetAutoscaler, the first player into a cold map also waits for a pod (#148), and
  an exhausted fleet is reported with a terminal refusal (#152). ADR-18 decides the autoscaler
  question; this ADR does not.
- **Known-adjacent, already open:** #143 — k3d's serverlb sits in the gameplay data path and
  triples snapshot jitter, so local capacity numbers measure the proxy; #147 — a reported 54 Hz
  tick against an advertised 60 Hz base rate, which that investigation reports as a
  measurement artifact — the loop paces on `CLOCK_MONOTONIC` while the observer timed it
  against a `CLOCK_REALTIME` running ~10% fast on the WSL2 host — rather than a code defect;
  #148 — no FleetAutoscaler and `replicas: 1`.

### Follow-up work

- **S** — Measure the gateway rollout outage window with a host-side poll across
  `kubectl rollout restart`, and record the number next to the posture section. Until then it
  stays explicitly unmeasured.
- **S** — Alert on `gameserver.player.saves{status="error"}` (`AsyncSaver`), so a `postgres-game` outage is not
  a silent one; it is the only failure in the table that a player cannot see and an operator
  currently would not either.
- **M** — Answer the gateway exposure question (LoadBalancer/Ingress vs hostPort) together
  with cross-instance single-flight, as one decision. Either alone makes the deployment worse.
- **L** — Decide the Redis topology for the first tier above dev, against ADR-4's split path
  rather than by adding replicas.

---

## Summary of decisions

| # | Area | Decision |
|---|---|---|
| 1 | Data ownership | One writer per datum; matrix is normative. PostgreSQL is the only recoverable gameplay store |
| 2 | Map sharding | One live server per map for the MVP; violations warned, not enforced; deterministic selection |
| 3 | Gateway role | Redirector, not router — keep it, fix the docs; proxy mode is a future option with a latency cost |
| 4 | Redis | One instance, prefix-separated roles, `noeviction` explicit; events split off first at Soft Launch |
| 5 | Events | Redis Streams with ACK only; raw pub/sub reserved and currently unused |
| 6 | Crash recovery | ≤30s loss window accepted for gameplay state, conditional on economy going through Nakama transactionally |
| 7 | CCU/cost | All figures are unbenchmarked estimates; benchmark plan anchored to the 66ms tick budget |
| 8 | Realtime security | Opt-in KCP PSK encryption (per-session keys deferred); `JOIN_TOKEN_SECRET` split from `JWT_SECRET`, both rotatable; token-bucket rate limits on accepts, frames and `gateway_token`. KCP is **not** reachable end to end — the C# game server has no KCP |
| 9 | Wire encoding | Protobuf from one committed schema; JSON kept during migration and distinguished by the first body byte, so components upgrade in any order |
| 10 | Shared simulation | Arch replaces `GameWorld` on the server; `Shared.GameLogic` stays ECS-free and ships to Unity as multi-targeted **source**; golden vectors run on both sides in CI; only IEEE-exact float ops permitted in shared code |
| 11 | Arch under NativeAOT | Arch publishes clean and then throws at runtime without per-component AOT hints; hints are **generated or guarded, never hand-written**; `CommandBuffer` is broken under AOT and is not used; the `publish` CI job must **run** the binary, not just build it |
| 12 | ECS migration | Server goes to **real ECS with Arch**, staged one PR at a time, over the analysis's objection and by owner decision; `CommandBuffer` stays banned and structural ops stay deferred to one phase per tick; every new component gets its AOT hint line in the same commit; no query shape the hint guard cannot see; `Shared.GameLogic` and the wire are frozen throughout |
| 13 | Simulation rates | Three responsibility-named groups (`Critical`/`World`/`Background`) at configurable Hz (`SIM_*_HZ`, default 60/15/5) on one derived integer base-tick timeline; rates that do not divide the base are rejected at startup; each group integrates with its own dt; group order Critical->World->Background is the cross-rate write-ownership rule; **replication stays at the world rate**; overload drops the backlog and counts it; the background group ships empty because nothing currently tolerates 200ms |
| 14 | Agones | **Stages 1-4 shipped 2026-08-17 and are proven — see ADR-16.** The decisions below stand; the status claim does not. Originally: the C# server's Agones SDK is a no-op and `--agones` changes nothing; implement it over the **HTTP sidecar on `localhost:9358`**, not the gRPC C# SDK, to hold the module's NativeAOT/no-dependencies rule. Agones owns pod lifecycle, Redis owns the `map_id -> server` lookup, and the server registers **only after** reporting Ready — an ordering `GameServer.RunAsync` already implements, so what it needs is a test pinning it, not a change. Health stays `disabled: true` until a real SDK is wired; the autoscaler is buffer-based on server count because ADR-7's CCU ceiling is unknown. Whether the realtime tier moves off `DEPLOY_MODE=containers` to k8s is **not** decided here |
| 15 | Realtime tier on k8s | **Proposed, not accepted — this one records an open question.** All three environments are `DEPLOY_MODE=containers`; there is no k3s (context is `docker-desktop`), no `deploy/k8s/`, and `cd.yml` applies no manifest. One thing *is* decided because it blocks either answer: with `portPolicy: Dynamic` the server cannot learn its own address, advertises `:9000` and the gateway hands that to clients verbatim — so the SDK must **read GameServer status from the sidecar**, not use static ports and not let the gateway write the registry entry (which would break ADR-1 and ADR-14's split). Six prerequisites — StatefulSets/PVCs, ConfigMaps, plugin packaging, Secrets, a registry, and allocation RBAC — sit outside `deploy/agones/` and outweigh ADR-14's stages 1-8, which they precede. ADR-3 is unchanged: allocation lives inside `MsgEnterWorld` and the gateway stays out of the gameplay path |
| 16 | Agones on k3s | Realtime tier **proven** on Agones/k3d: a real client joined an Agones-managed server in strict-address mode. Docker Desktop k8s cannot host it (Kubernetes `hostPort` is never published to the host); k3d with a mapped port range can. The advertised address is **composed** — port from the Agones status read, host from `GAMESERVER_ADVERTISE_HOST` — because `status.address` is the node address and is not dialable. ADR-2 is now enforced in code (allocate only for a map with no live server); the join token is minted only after the pod self-registers. Allocated pods are never reclaimed; the map-fleet allocator policy stays open; the deploy path stays `DEPLOY_MODE=containers` |
| 17 | Availability posture on k8s | **Statement of posture, not a manifest change.** Every workload in `deploy/k8s/` is **one replica** — gateway, Nakama, Redis, both PostgreSQL instances, and the map Fleet — so k8s provides scheduling and lifecycle here, **not redundancy**. `strategy: Recreate` on the two `hostPort` workloads (gateway 7000, Nakama 7001) is **required**: RollingUpdate deadlocks on a single node because the replacement cannot schedule until the outgoing pod frees the port. The accepted cost is that **every gateway or Nakama rollout drops the join path entirely**; in-progress sessions survive, because the game server verifies the join token itself and never calls the gateway (ADR-3). The window is **unmeasured** and not quoted. Before any tier above dev: answer the gateway hostPort exposure question *together with* cross-instance single-flight (ADR-16), and decide Redis persistence/replication against ADR-4 rather than by adding replicas |
