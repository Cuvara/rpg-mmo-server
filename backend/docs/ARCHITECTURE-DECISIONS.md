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
| `servers:id:{id}`, `servers:map:{map}` | Redis (TTL) | **two writers — see below** | `gateway/registry/registry.go:85,106,111-112` and `scripts/register-gameserver.sh:83-91` |
| `events:game` | Redis Streams | nothing live (see ADR-5) | `gateway/events/relay.go:16` subscribes; no live publisher |
| Live world (entities, mobs, pending input) | C# process memory | gameserver-dotnet | `GameServer/World/GameWorld.cs:22-27` |

Three findings the criticism is right about:

1. **The server registry genuinely has two writers.** The gateway writes through the
   typed interface (`registry.go:85` registers Agones-allocated servers), while
   `scripts/register-gameserver.sh:83-91` writes the same keys with raw
   `redis-cli HSET`/`SADD`/`EXPIRE`. The script exists because the C# server has no
   Redis client and cannot self-register; its own header says "Delete this script
   the day the C# server registers itself" (`scripts/register-gameserver.sh:6-14`).
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
- **M** — Give the C# server a Redis client so it self-registers and heartbeats,
  then delete `scripts/register-gameserver.sh`. This collapses the registry to one
  writer and fixes the dead heartbeat in ADR-2.

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

Compounding this, the registry TTL is never refreshed: `ServerRegistry.Heartbeat`
exists (`redisstore/registry.go:90-99`) but has **zero callers** in the gateway.
The dev entry is written once with a 1-hour TTL by
`scripts/register-gameserver.sh:42,55` and never re-armed.

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
2. **Snapshot bandwidth per client** — bytes/sec/client vs nearby-entity count.
   Snapshots are full-state JSON with no delta encoding, so this is expected to be
   the first thing that hurts on mobile networks.
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

- **M** — Build `backend/loadtest` per the spec above.
- **S** — Add explicit histogram buckets around the 66ms budget instead of the OTel
  defaults, so p99 is readable near the threshold.
- **M** — Run the benchmark matrix and replace the estimate tables with measured
  numbers plus the commit they were measured at.
- **M** — Spatial-grid AOI, if and only if measurement confirms it is the ceiling.

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
