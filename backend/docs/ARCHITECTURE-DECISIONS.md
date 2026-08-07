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

> **UPDATE 2026-08-07 — partially resolved. The primary benchmark has been run.**
>
> `backend/loadtest` now exists and the game-server capacity ceiling has been
> measured. Full write-up: **[BENCHMARK.md](BENCHMARK.md)**.
>
> | Measured (dev workstation, lower bound) | Result |
> |---|---|
> | Players per game server before tick p99 > 66.67ms | **150** (160 breaches) |
> | RAM per pod | **~30 MiB idle, ~50 MiB @100, ~82 MiB @200** — resolves the 30-45 vs 50 MB dispute |
> | Downstream bandwidth | **1.22 KB/s per in-AOI player**; 184 KB/s per client at 150 |
> | Bandwidth vs the < 50 KB/s threshold below | breached at **~41 players** |
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
- **M** — Re-run the matrix on VPS hardware with the generator on a separate host,
  then replace the tier CCU numbers and drop the ⚠️ markers.
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
