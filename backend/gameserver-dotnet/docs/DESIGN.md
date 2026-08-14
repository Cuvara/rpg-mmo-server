# Design Decisions

## Why C# for GameServer

The primary motivation is **shared game logic** between server and client:

- The Unity DOTS client and the .NET server both reference `Shared.GameLogic`,
  a pure C# library with zero Unity dependencies.
- Client prediction accuracy improves when the same code runs on both sides —
  movement validation, combat formulas, and cooldown checks produce identical
  results, reducing reconciliation frequency.
- A single language for the gameplay team reduces context-switching between
  Go and C# when implementing new skills, items, or mechanics.

The Go gateway remains unchanged. It assigns a client to whichever backend is
allocated by Agones and hands back a join token; the client then connects to that
server directly, so the gateway never sees gameplay traffic (see
`backend/docs/ARCHITECTURE-DECISIONS.md`, ADR-3). The wire protocol is identical.

## Shared.GameLogic Constraints

These constraints exist so the library compiles cleanly in both a standard
.NET 10 project and in **Unity 6**, which consumes these files as *source*
through a UPM git dependency rather than as a compiled assembly (ADR-10):

- **Zero Unity dependencies** — must compile as a standard .NET class library.
  No `UnityEngine`, `Unity.Mathematics`, or `Unity.Collections` references.
- **Zero ECS dependencies** — no `Arch.Core` type in any signature either. Arch
  is the server's storage choice; the rules must not be coupled to it (ADR-10).
- **No server-specific code** — no networking, no persistence, no logging
  framework. The server wraps shared logic with its own I/O layer.
- **All game constants centralized** — damage formulas, speed caps, cooldown
  durations, AOI radius — all live in `Shared.GameLogic` so both sides agree.
- **Value types (struct) for performance** — hot-path data structures are
  structs to avoid GC pressure, which matters for both server tick loops and
  Unity DOTS jobs.
- **No allocations in hot paths** — methods called per-tick must not allocate
  heap objects. Use `Span<T>`, stackalloc, or pre-allocated buffers.
- **Only IEEE-exact float operations** — `+ - * /`, comparison, and
  `MathF.Min/Max/Abs/Sqrt`. Transcendentals (`Sin`, `Cos`, `Atan2`, `Pow`,
  `Exp`), `double`, `System.Random` and wall-clock reads are barred: their
  results are implementation-defined and the two sides run different compilers
  on different architectures (NativeAOT x64 server, IL2CPP ARM64 client).
  Adding one is an amendment to ADR-10, not a review comment.

### What Unity's compiler forces on this code

Unity 6's C# compiler is **C# 9** and has neither implicit usings nor
System.Text.Json. Because the client compiles the source, the *server* build is
what has to catch a violation — the alternative is the client discovering it at
package-import time. Three project settings make that happen, and they are load
bearing:

| Setting | Value | Why |
|---|---|---|
| `TargetFrameworks` | `netstandard2.1;net10.0` | The netstandard build proves nothing in the library reaches past Unity's runtime profile. No polyfill is needed — `MathF.Min/Max/Abs/Sqrt`, `HashCode.Combine`, `float.IsFinite` and `Span<T>` are all in netstandard2.1 |
| `LangVersion` | `9.0` | Rejects C# 10+ syntax the client cannot compile. This is why the namespaces here are block-scoped: file-scoped `namespace X;` is C# 10 |
| `ImplicitUsings` | `disable` | Unity has no implicit usings, so every file writes its own. A missing `using System;` now fails the server build |

`InputData` / `SnapshotData` carry no `System.Text.Json` attributes. They are
**simulation** types, not wire types: since ADR-9 the generated Protobuf classes
are the server's only message types, and the legacy JSON path is a hand-written
`Utf8JsonWriter` codec over *those*. Nothing serializes these two.

### Packaging for Unity

`Shared.GameLogic/` doubles as a UPM package root: `package.json`
(`com.rpgmmo.shared-gamelogic`, no dependencies) plus
`Shared.GameLogic.asmdef`. The client consumes it as a git dependency with a
`?path=` subfolder reference pinned to a tag — the same mechanism the Unity
project already uses for `com.company.build-pipeline`:

```
https://github.com/<org>/rpg-mmo-server.git?path=/backend/gameserver-dotnet/Shared.GameLogic#sgl-v0.1.0
```

Tags are `sgl-vX.Y.Z`, and `package.json`'s `version` must be bumped in the
commit that is tagged — a tag whose `package.json` still says the old version
gives the client a package that misreports itself, silently.

The asmdef declares `"noEngineReferences": true` and an empty `references` list,
which makes ADR-10's zero-engine-dependency rule a compile error on the client
rather than a review convention. It also bounds what Unity compiles: without an
asmdef the sources would join the default assembly and lose all dependency
control.

Two build systems now read this folder. MSBuild's `Compile` glob takes the 11
`.cs` files and treats `package.json`/`.asmdef` as `None`; Unity compiles every
`.cs` under the package root, which is safe only because `bin/` and `obj/` are
gitignored and a git fetch never delivers them. Consuming the package by local
path into a *built* working tree would feed Unity the generated
`AssemblyInfo.cs` and fail on duplicate assembly attributes — use the git URL.

### Golden vectors

`Shared.GameLogic/GoldenVectors/` holds committed
`(state, input, dt) → expected state` fixtures — 77 cases across `vec2.json`,
`movement.json`, `combat.json` and `validation.json`. They are replayed by
`GameServer.Tests/Golden/` and, from the same files at the same package-relative
path, by the client's Unity Test Runner. That is the mechanism that makes
"shared logic" mean shared *behaviour* rather than a shared file.

`vec2.json` exists separately because the three `MathF.Sqrt` call sites
(`Vec2.Magnitude`, `Vec2.Distance`, `MovementSystem.ResolveDirection`) are where
a NativeAOT-x64 / IL2CPP-ARM64 divergence appears first, and two of them are not
reachable from a behaviour vector: `Vec2.Magnitude`/`Normalized` have no caller
inside the library, and `Vec2.Distance` only formats the out-of-range error
message. The movement vectors additionally cover both sides of the magnitude-1
branch — the test that decides whether `Sqrt` runs at all.

Format constraints (fixed by ADR-10, both readers must agree):

- Floats are stored as **IEEE-754 bit patterns** (`"x": "0x40551EB8"`) and
  compared with `BitConverter.SingleToInt32Bits`. Decimal text does not
  round-trip identically through two serializers, and a tolerance comparison
  would not test the property the vectors exist to protect.
- The top level is an object (`{"cases": [...]}`) and cases are flat, public
  fields only — the subset Unity's built-in `JsonUtility` reads, so the client
  needs no JSON package. `FixtureShapeIsUnityJsonUtilityCompatible` enforces it.
- Expected values are **generated by running the implementation**, never hand
  computed: `GOLDEN_REGEN=1 dotnet test --filter Regenerate`.
  `CommittedFixturesAreUpToDate` fails the build if the committed files drift
  from what the current code produces, so a behavioural change shows up as a
  fixture diff in the same PR — which is exactly the review signal that the
  client's prediction changed.

## Performance

### NativeAOT

The `GameServer` project publishes with `<PublishAot>true</PublishAot>`. This
produces a single native binary with:

- No JIT warmup — consistent tick timing from the first frame
- ~30-45 MB baseline memory (vs ~80-120 MB with CoreCLR JIT)
- Faster cold start (important for Agones pod scaling)
- Smaller container image (Alpine runtime-deps only, no SDK)

### Server GC

`<ServerGarbageCollection>true</ServerGarbageCollection>` is enabled. Server GC
uses dedicated threads and larger generations, trading memory for lower pause
times — the right tradeoff for a tick-loop server.

### Concurrency

- `ReaderWriterLockSlim` protects the world state (same pattern as Go's
  `sync.RWMutex`). The tick loop takes a write lock; snapshot reads and
  connection handlers take read locks. Arch's `World` is not thread-safe, so this
  lock is load-bearing rather than incidental — see "Entity storage: Arch ECS".
- `Channel<T>` (bounded) for per-connection send queues. Back-pressure drops
  messages for slow clients rather than letting buffers grow unbounded.
- The tick loop itself is synchronous and never awaits I/O. Persistence is
  dispatched to a background task on a configurable interval (30-60s).

### Invariant Globalization

`<InvariantGlobalization>true</InvariantGlobalization>` is set. The game server
does not need locale-aware string formatting or collation. This reduces binary
size and avoids locale-dependent behavior across deployment environments.

## Wire Protocol Compatibility

The C# server produces byte-identical wire output to the Go server:

- **Same framing**: 4-byte big-endian length prefix + UTF-8 JSON payload.
- **Same message type numbering**: `1 = join`, `2 = join_ack`, etc.
- **Same field naming**: `snake_case` in JSON (configured via
  `JsonNamingPolicy.SnakeCaseLower` in System.Text.Json).
- **Same numeric precision**: `float` (32-bit) for coordinates, matching Go's
  `float32`.
- **Binary compatible**: The Go gateway performs no server-type detection. It
  forwards bytes opaquely. An Agones fleet can mix Go and C# game servers.

## Movement Model (2026-08-05)

### What it replaced

The first C# port carried the Go MVP's model verbatim: `move_x`/`move_y` were a
**displacement**, applied as `position += (move_x, move_y)` once per received input
message, with a per-message cap of `MaxMovePerTick (5.0) * Speed`. Three problems:

1. **Speed scaled with packet rate.** Every input message moved the entity, and the
   tick loop applied *all* buffered inputs in one tick. Sending 10 inputs per tick
   moved 10x further — a speed hack that required no malformed packet at all.
2. **Diagonals were faster.** `(1,1)` moved `sqrt(2)` ≈ 1.414 units against `(1,0)`'s
   1.0, so holding two keys was strictly better than one.
3. **Not simulatable client-side.** With displacement-per-message there is no
   timestep, so the Unity client had nothing to integrate and could not predict.

### What it is now

`Shared.GameLogic.Systems.MovementSystem` implements a fixed-timestep model:

```
direction = clamp(input, |v| <= 1)          // normalize when |v| > 1
position += direction * speed * dt          // dt = 1 / tickRate
position  = mapBounds.Clamp(position)
```

- **`move_x`/`move_y` are a direction, not a displacement.** The wire format is
  unchanged (same fields, same types, same framing) — only the semantics changed.
  Magnitudes below 1 are preserved so analog sticks give proportional speed.
- **Diagonal == cardinal.** Any vector with magnitude > 1 is normalized, so raw
  keyboard `(1,1)` becomes `(0.707, 0.707)` and travels exactly as far as `(1,0)`.
- **`EntityState.Speed` is world units per second** (previously a per-tick
  displacement multiplier). Default player speed moved from `1.0` to `5.0` u/s,
  which reproduces the old effective cap of 5 units of travel per second while
  making the number mean something physical.
- **Map bounds.** `MapBounds` is an axis-aligned rectangle, default 1000x1000
  centered on the origin (players spawn at `(0,0)`), configurable per server with
  `--map-width` / `--map-height`. Clamping is per-axis, so a player pressed against
  a wall keeps sliding along it instead of stopping dead. Positions loaded from the
  player store are clamped on join, so a map resize cannot leave an entity outside.
- **Tick-rate independence.** Because `dt = 1/tickRate`, a server at 10Hz and one at
  15Hz move a player the same distance per wall-clock second. Tick rate is now a
  smoothness knob, not a balance knob.

### Input buffering: latest-wins, not accumulate

The old loop applied **every** buffered input each tick. The new loop coalesces:
per player, only the newest input (highest client tick) performs the movement
integration; superseded inputs are still processed for their attack payload, which
has its own cooldown gate.

Rationale — accumulating N inputs into one tick would multiply displacement by N and
resurrect exactly the exploit this rework removes. Latest-wins makes travel a pure
function of wall-clock time and speed, and it matches what the client predicts (a
client also integrates one input per fixed step). The cost is that inputs produced
faster than the tick rate are dropped for movement purposes; that is intended —
input rate above the simulation rate carries no additional authority.

`LastInputTick` still tracks the newest processed input and is echoed for
reconciliation, so a client can identify which input the authoritative position
corresponds to.

### Validation (enum, never exceptions)

`MoveResult` is returned by value — a hostile packet must not allocate an exception
inside the tick loop:

| Result     | Condition                                            | Server action        |
|------------|------------------------------------------------------|----------------------|
| `None`     | vector inside the deadzone                            | no-op                |
| `Accepted` | `\|v\| <= 1`                                          | integrate as-is      |
| `Clamped`  | `1 < \|v\| <= MaxInputMagnitude (1.5)`                | normalize, integrate |
| `Rejected` | NaN / infinity / `\|v\| > 1.5`                        | log at Debug, drop   |
| `Blocked`  | entity dead, `speed <= 0`, or `dt <= 0`               | no-op                |

`MaxInputMagnitude = 1.5` is chosen so an honest client sending raw diagonal input
(magnitude 1.414) is clamped rather than punished, while anything wilder is dropped.
`dt` is additionally capped at `MaxDeltaTime = 0.5s` so a stalled process cannot
produce a teleport on resume. `MovementSystem.IsDisplacementLegal` provides the
displacement audit (`distance <= speed * dt * 1.05`) for validating client-reported
positions during reconciliation.

### Unity client prediction reuse

`MovementSystem` is pure: static methods over structs, no randomness, no
`DateTime`, no allocations, no collections — `dt` is always a parameter. The Unity
DOTS client compiles the same `Shared.GameLogic` sources (ADR-10: source, not a
DLL, so IL2CPP never has to swallow a netstandard assembly) and calls the identical
`ResolveDirection` / `Integrate` for local prediction, so a predicted position and
the server's authoritative position agree given the same input, speed and `dt`.
The planned client loop:

1. On each fixed step, sample input, call `ResolveDirection` + `Integrate`, render
   the predicted position immediately, and push `(tick, direction)` to a local ring
   buffer alongside the input sent to the server.
2. On each snapshot, look up the entry for `LastInputTick`. If the predicted
   position matches the authoritative one within epsilon, discard the acked history.
3. Otherwise snap to the authoritative position and replay the unacked inputs
   through `Integrate` — the rewind/replay step, cheap because it is the same
   deterministic function.

The prerequisite for step 2/3 is that snapshots carry the acked input tick per
entity; that is the next piece of work on the wire format.

## Player State Persistence (2026-08-05)

### What it replaced

The Go game server persisted player state through
`shared/storage/pgstore.PostgresPlayerStore`. The C# migration shipped with only
`MemoryPlayerStore`, so every restart silently wiped player state while the
`rpg-postgres-game` container sat idle with nothing writing to it. The Go
package is now orphaned; `GameServer/Persistence/PostgresPlayerStore.cs` is a
direct port of its semantics.

### Npgsql, and why no ORM

`Npgsql` 10.0.3 is the only new dependency. It is the reference PostgreSQL
driver for .NET and is annotated for trimming and NativeAOT.

The AOT constraint drives the entire shape of the store: `PublishAot` is on, so
anything resolved by reflection at runtime is unsafe. The store therefore uses
raw `NpgsqlCommand` objects, parameters with an explicit `NpgsqlDbType`, and
positional reader accessors (`GetString(0)`, `GetFloat(2)`, ...). There is no
ORM, no `[Table]`/`[Column]` mapping, no anonymous-type projection and no
runtime type inference — every access path is statically resolvable. Dapper or
EF Core would each reintroduce reflection-based materialisation.

Connections are pooled through a single `NpgsqlDataSource` built once at boot.
Every command carries an explicit `CommandTimeout` (5s for queries, 30s for the
migration) so a stalled database can never wedge the async saver.

### Upsert, not read-modify-write

`SavePlayerAsync` is a single `INSERT ... ON CONFLICT (user_id) DO UPDATE` that
also refreshes `updated_at`. The async batch saver can call it unconditionally
for every player each cycle with no prior existence check, no transaction, and
no read round-trip — one statement per player per save tick. `LoadPlayerAsync`
returns `null` for a missing row, matching `MemoryPlayerStore`, so join
handling needs no store-specific branch.

Persistence stays off the tick thread: `AsyncSaver` runs as its own background
task, and a save failure increments
`gameserver_player_saves_total{status="error"}` instead of propagating into the
simulation.

### Migration on boot

`MigrateAsync` applies `CREATE TABLE IF NOT EXISTS` / `CREATE INDEX IF NOT
EXISTS` DDL on every start; concurrent servers are serialised by PostgreSQL's
own DDL locking. The SQL is duplicated in
`backend/deploy/db/init-gamestate.sql`, which seeds a fresh container volume
before any server connects. Both files carry a cross-reference comment and
`SchemaSql_MatchesInitGamestateSql` fails the build if they drift.

### Fail fast on an unreachable database

`GAME_DB_URL` unset means the operator asked for a memory store, which is fine
for local development. `GAME_DB_URL` set but unreachable means the operator
asked for durability and is not getting it. Falling back to memory there would
produce a server that looks healthy, accepts players, and discards their
progress — a silent data-loss mode that only surfaces as player complaints.
So the server logs a critical error and exits 1, which surfaces immediately as
a crash-looping pod. DSN passwords are masked in every log line and in the
exception message.

### Position is map-scoped; carried stats are not (2026-08-07)

`player_states` holds **one row per player**, overwritten by whichever server
currently hosts them — there is no per-map row. That is a deliberate consequence
of ADR-1 (one writer per datum): the hosting server owns the player's state, and
a player is only ever hosted by one server.

The join path originally restored `x`/`y` from that row unconditionally. With one
map live nobody noticed; with two, a player who last stood at (480, 12) on
`map_02` and then joined `map_01` was recreated at (480, 12) on `map_01` — a
different place entirely, possibly inside terrain. Worse, the row never
converged: each join wrote back the stale base plus whatever they walked, so the
drift compounded.

**A coordinate pair only means something relative to the map it was recorded on.**
So `PlayerSpawn.Resolve` compares the row's `map_id` against the map being joined:

| Saved row | Position | HP / max HP |
|---|---|---|
| none | spawn point | defaults |
| `map_id` == joining map | restored, clamped into bounds | restored |
| `map_id` != joining map | **spawn point** | restored |
| `map_id` empty | **spawn point** | restored |

**HP crosses unchanged.** It is a property of the character, not of the ground
under it; healing or damaging someone for walking through a door would be a
gameplay decision nobody made. Everything else the entity needs (speed, attack,
defense) is a server default today and is not persisted at all.

**An empty `map_id` counts as a mismatch, not a wildcard.** The column defaults to
`''`, so a row written before the id meant anything — or by a server started
without one — has unknown provenance. Spawning such a player at the spawn point
is recoverable; dropping them at coordinates from an unknown map is not.

**Comparison is ordinal.** Map ids are opaque identifiers matched byte for byte in
the registry keys and the join-token claim; a culture-sensitive compare here could
disagree with them.

The row converges on the next save without any extra write: `AsyncSaver` is
constructed with the hosting server's own `MapId`, so the first sweep after the
join rewrites `map_id` to the map the player is actually on.

Policy lives in `GameServer/Persistence/PlayerSpawn.cs` as a pure function rather
than inline in the join handler, so every branch is testable without a database, a
socket or a running server — and so the decision has one home when zone/shard
routing (ADR-2) eventually makes the key richer than a bare map id.

**Known gap, deliberately not fixed here:** `player_states` has no `dead` column,
so a player persisted at `hp = 0` reloads with `Hp = 0` and `Dead = false`. That is
a pre-existing death/respawn design question — it needs respawn rules and a schema
change, not a tweak to the placement policy — and this change preserves the
existing HP behaviour exactly rather than quietly reviving anyone.

## Delta Snapshots, Input Ack and Tick-Based Timers (2026-08-06)

Wire format reference: `docs/API.md`. This section is the *why*.

### What it replaced

Every tick, every connected client received the **complete** AOI entity set as JSON,
and the server never told the client which of its inputs had been applied:

1. **Bandwidth grew as O(entities × tick rate) per client.** Measured on a trivial
   scene (1 moving player + 8 stationary mobs, 15 Hz): **592 B/tick/client**, ≈ 8.9
   KB/s down *per client* before TCP/IP overhead. Most of those bytes re-sent
   coordinates that had not moved since the previous tick. A real map — dozens of
   entities in AOI — puts this straight through a mobile data budget.
2. **Reconciliation was impossible.** `EntityState.LastInputTick` existed and was
   maintained correctly, but was never serialized. A predicting client could see the
   authoritative position but not *which input that position included*, so it could
   not decide which of its buffered inputs to replay. Without that, client prediction
   is either absent (input lag) or permanently divergent.
3. **Cooldowns were wall-clock.** `DateTime.UtcNow.Ticks` gated attacks. The same
   input sequence replayed twice could produce different outcomes, so neither
   client-side prediction nor server-side replay of a disputed sequence was sound.

### Delta encoding

Each connection owns a `SnapshotDeltaState` (created with the `Connection`, discarded
with it) holding the visible state it last sent. Per tick, per client:

- **Keyframe** (`full: true`) — the complete AOI set; the client discards anything
  not listed. Sent on join, on `resync` request, and every `keyframeInterval`
  snapshots (default 30 ≈ 2s at 15 Hz).
- **Delta** — only entities whose `type/x/y/hp/max_hp` changed since the previous
  snapshot to *this* connection, plus a `removed[]` list of entities that left AOI or
  the world. Comparison is exact (bitwise float equality), not tolerance-based: a
  tolerance would let slow drift accumulate on the client with nothing to correct it.

Measured on the same scene, 100 ticks: **126.6 B/tick/client, a 78.6% reduction**
(`DeltaSnapshotWireTests.DeltaEncoding_UsesLessBandwidthThanFullSnapshots`). The
saving grows with the number of *stationary* entities in AOI, which is the normal
case — most of a map is not moving in any given 66 ms.

Correctness rests on the transport being ordered and reliable (TCP today), so the
server may treat "last sent" as "last received". That assumption is stated, not
hidden: the periodic keyframe bounds how long any violation can persist, `resync`
lets the client force recovery immediately, and `--keyframe-interval 0` turns delta
encoding off entirely if a client cannot merge. Under KCP in unreliable mode the
keyframe interval is the knob to tighten.

Allocation behaviour: the per-connection `Dictionary` of last-sent state and the
`HashSet` scratch are reused across ticks; only the outgoing message and its lists
are allocated, and the entity list is allocated **lazily** — a tick where nothing
changed produces a message carrying a shared empty list. No LINQ, no enumerator
boxing, indexed `for` loops only. Snapshots cannot use a reused buffer because they
are handed to the per-connection send channel and serialized later on the write loop.

### Input acknowledgement

Every snapshot carries `ack_tick`: the newest `input.tick` accepted for **that
client's own entity**, read under the same world lock as its position. It is
per-connection by construction — one player's ack can never reflect another's
inputs. `0` means "nothing accepted yet", which is why the field is `omitempty`:
it is also the pre-delta wire shape.

### Tick-based timers

`EntityState.CooldownUntilTicks` (a `DateTime.Ticks` value) became
`CooldownUntilTick` (a `ulong` simulation tick). `CombatLogic.ValidateAttack` takes
the current tick; `InputHandler` receives it from the tick loop and sets
`CooldownUntilTick = currentTick + AttackCooldownTicks(tickRate)`.

`AttackCooldownTicks` rounds **up** — `ceil(500 ms × 15 Hz / 1000)` = 8 ticks =
533 ms — so the tick cooldown is never *shorter* than the wall-clock one it
replaced (never a new exploit window). Behaviour at 15 Hz is equivalent: a player
spamming attacks lands 19 hits per 150 ticks where the wall-clock gate allowed 20
per 10 s. The simulation now has exactly one clock, the tick counter, so the same
input sequence always yields the same result — a property the tests assert directly
rather than assume.

## Anti-Cheat

All validation is server-authoritative:

- **Speed hack**: The server integrates movement itself from a normalized
  direction (`direction * speed * dt`, one integration per player per tick), so a
  client cannot travel further by sending larger vectors or more packets. See
  *Movement Model* above.
- **Range check**: Attack and skill targets must be within range according to
  the shared logic's distance calculation.
- **Cooldown enforcement**: Cooldowns are tracked server-side in **simulation
  ticks** (`EntityState.CooldownUntilTick`), never wall-clock, so an early input is
  rejected deterministically and the client can predict the same ruling. Early
  inputs are silently dropped.
- **Rate limiting**: Input messages are bounded per tick. Excess messages from
  a client are discarded.

## Disconnect and Reconnect

- On TCP disconnect, the server holds the player entity for a grace period:
  **30 seconds** on map servers, **60 seconds** on dungeon servers.
- During the hold, the entity is marked inactive (no AI targeting, no damage).
- If the client reconnects with a valid session token within the window, it
  resumes with full state. Otherwise the entity is removed and the session is
  invalidated.

### Measured constants (2026-08-12)

Two constants below are quoted as specifications throughout these docs. They
have now been **measured against the running local stack from two independent
directions each**, and the figures agree. Recorded here so they are evidence
rather than restatement — this repo has at least one figure that outlived the
measurement behind it (the 150-players-per-server ceiling, ADR-7), and the way
to avoid repeating that is to say what a number is *of*.

| Constant | Specified | Measured — how | Result |
|---|---|---|---|
| AOI radius (`GameConstants.DefaultAoiRadius`) | 50.0 units | **server**: distance between two players' persisted `player_states` rows, compared against whether each appeared in the other's snapshots | **61.00 units** apart → mutually invisible |
| | | **client**: Unity client tracking at what separation a remote player left its world set | last visible **50.5**, absent by **62.2** |
| Map-server entity hold | 30 s | **server, gauge sampling**: lag of `gameserver_entities` behind `players_online` across two disconnects | **29 s** and **32 s**, then converged |
| | | **server, log end-to-end**: disconnect line to "Entity hold expired" line, across three two-process runs | **30, 31, 30, 30, 30, 31 s** |
| | | **client**: time from a deliberate disconnect to the `removed` entry arriving at the surviving client | **30.1 s** |

Three independent methods for the hold — two server-side using different data
(Prometheus gauges vs the server's own log lines) and one client-side timing the
wire — landing on 29-32 s. Both figures for AOI land the boundary within about a
unit. The agreement across methods is the evidence; no single one of these is
worth much alone.

**The persistence write lands with the hold EXPIRY, not the disconnect.** In the
measured run a client disconnected at 08:00:52 and its `player_states` row is
stamped 08:01:21.56 — the save fires as the entity is reaped, ~30 s later. Anyone
assuming save-on-disconnect will misjudge the crash-loss window by the full hold
duration: if the process dies during a hold, up to 30 s of movement was never
written.

The agreement is the evidence, not either row alone. The two paths share no
code and neither measurement was taken with sight of the other: the server-side
AOI figure comes from persisted Postgres rows, the client-side one from a Unity
client tracking what appeared in its own snapshots; the server-side hold figure
comes from Prometheus gauge sampling, the client-side one from timing a `removed`
entry on the wire. Individually each is arguable — a persisted position is only
a snapshot of when the save fired, and a client-side inference could be a merge
bug. Landing within ~1 unit and ~1 second of each other, they are not.

A third constant, the heartbeat, is specified in `Net/Connection.cs` as a 10 s
ping interval with a 30 s pong timeout. It is confirmed exercised: a two-process
client run held connections in-world for **75 s and 74 s** — ~7 pings each, more
than twice the timeout — and no `Heartbeat timeout` line appeared for either
connection.

Keep the two kinds of run distinct when reading results:

- **Short runs prove visibility, not liveness.** A run under 30 s ends before the
  pong timeout could fire, so it cannot test the heartbeat at all. Several ~25 s
  two-client runs passed cleanly while saying nothing whatsoever about ping/pong.
- **Long runs prove liveness.** Only a run exceeding the timeout — comfortably,
  so a late pong is not mistaken for a passing one — is evidence the client
  answers pings.

Reading the first as the second is an easy mistake and it is why this note
exists.

## Shutdown (2026-08-06)

`GameServerHost.ShutdownAsync` always has at least two callers on a real
termination, and they overlap:

1. `RunAsync` calls it at its tail, as soon as the run token is cancelled and one
   of the tick / save / accept tasks returns.
2. The process owner calls it directly — the SIGTERM handler, an Agones drain, or
   a test harness that wants the final save flushed before it reads the store.

So shutdown is **idempotent and concurrency-safe** by contract:

- The first caller wins an `Interlocked.Exchange` on a guard flag and performs the
  teardown. Every other caller awaits the same `TaskCompletionSource` and observes
  the same outcome — success or the same exception. A caller that gets a normal
  return from `ShutdownAsync` may therefore assume the final save has completed;
  it never means "somebody else has started one".
- The entity-hold table is drained with `TryRemove`, not iterated, so every hold
  `CancellationTokenSource` has exactly one owner. The reconnect path in
  `HandleConnectionAsync` competes for the same entries and takes ownership the
  same way, so whoever wins cancels and disposes it exactly once.
- The linked `CancellationTokenSource` created in `RunAsync` is disposed only in
  `DisposeAsync`, after the run task has completed. Disposing it inside
  `ShutdownAsync` would hand the still-unwinding background loops a dead token
  source.

Before this contract existed, two racing teardowns could cancel a hold CTS the
other had already disposed, so a pod that should have drained cleanly threw
`ObjectDisposedException` out of `RunAsync`. Regression coverage:
`GameServer.Tests/Server/GameServerHostShutdownTests.cs`.

## Join-Token Secret Split and Rotation (2026-08-06)

### What it replaced

The game server verified the join token with `JWT_SECRET` — the same secret
Nakama uses to sign client auth tokens. That secret has to be present on every
game-server pod for the join hop to work, which means the blast radius of one
compromised pod was "can mint an auth token for any user", not "can accept a
join". The two hops have completely different trust boundaries and were sharing
one key purely by accident of implementation.

### What it is now

Two independent secrets:

| Secret | Signs | Distributed to |
|--------|-------|----------------|
| `JWT_SECRET` | Nakama → client auth token | Nakama, gateway |
| `JOIN_TOKEN_SECRET` | gateway → game server join token | gateway, **every game-server pod** |

`GameServerHost` verifies the join token against `JOIN_TOKEN_SECRET` only. The
Go gateway signs it with the same variable (`backend/gateway/cmd/gateway/main.go`),
so the two halves are symmetric by construction.

**Fallback, and why it exists.** When `JOIN_TOKEN_SECRET` is unset both halves
fall back to `JWT_SECRET` and both log the same start-up warning. This is the
pre-split behaviour and keeps existing deployments working; it is also the reason
the split has to be enabled on gateway and game servers *together*. Setting it on
the gateway alone means the gateway signs with a key no game server holds and
every join fails — which is exactly why the C# half had to land before the split
could be turned on anywhere. The fallback decision lives in one place,
`ServerOptions.EffectiveJoinTokenSecret`, mirroring Go's
`config.Config.EffectiveJoinTokenSecret`.

### Rotation contract

Both variables accept a comma-separated list, `"current,previous"`, parsed by
`JwtKeyring` — the C# counterpart of Go's `shared/jwt.Keyring`. The contract is
deliberately identical on both sides, because any divergence surfaces only during
a rotation, i.e. at the worst possible moment:

- **The first entry signs.** Only the gateway signs; this server never does. The
  ordering still matters because it decides which key the gateway used.
- **Every entry verifies, in order.** A token signed with the previous secret is
  still accepted, so the old population drains over the join-token TTL instead of
  being logged out at the deploy.
- **Whitespace is trimmed, empty entries dropped.** `"new, old"`, `"new,old"` and
  `"new,old,"` are the same keyring.
- **An empty keyring fails closed.** No secrets means every token is rejected —
  never "accept anything" — and start-up logs it loudly.
- **Expiry short-circuits.** When a key matches the signature but `exp` has
  passed, verification stops there instead of trying the remaining keys: a valid
  signature with a dead expiry cannot become valid under a different key. This
  mirrors Go's short-circuit, where `Verify` returns non-zero claims on an
  expiry failure and the keyring treats that as definitive.

Verification cost is O(keys) HMAC-SHA256 computations in the worst case, which is
why a keyring is meant to hold two secrets during a rotation window, not an
archive of every secret ever used.

### Known behavioural difference from the Go verifier

One drift predates this change and is **not** rotation-related, but it is worth
recording: Go's `jwt.Verify` rejects a token whose `exp` claim is absent (its
zero value is in the past), and it additionally requires the header's `typ` to be
`JWT`; the C# validator only enforces `exp` when it is greater than zero, and
checks only `alg`. Both only ever *accept more* than the Go side, and the gateway
always emits `exp` and `typ`, so no gateway-produced token is affected. Tightening
it would change nothing for real traffic and is left alone rather than risking a
behaviour change on the join path.

Coverage: `GameServer.Tests/Server/JwtKeyringTests.cs` (keyring semantics) and
`GameServer.Tests/Server/JoinTokenSecretTests.cs` (the real TCP handshake for
split-active, fallback, rotation, post-rotation, fail-closed and expiry).

## KCP Transport for the Gameplay Hop (2026-08-07)

Until now `--transport=kcp` only ever covered the client→gateway hop. The
gateway advertised a transport to clients through `EnterWorldResponse.Transport`,
and the registry carried a `transport` field, but this server had no KCP at all —
it always bound TCP, so a KCP deployment was half a deployment. This entry covers
closing that gap.

### Requirement: interoperability, not "some KCP"

The constraint that decided everything here is that the Go side already ships
KCP (`backend/shared/transport` over `github.com/xtaci/kcp-go/v5`). A C# listener
that speaks a *different* dialect of KCP is worse than no KCP: it type-checks, it
passes its own tests, and it silently cannot talk to the clients and tools the
rest of the project already has. So wire compatibility with kcp-go was treated as
the acceptance criterion, and it was verified **before** anything was built on
top of the transport.

### Library evaluation

| Option | Verdict |
|--------|---------|
| **kcp2k** (Mirror's C# KCP) | **Rejected — not wire-compatible.** kcp2k is mature and widely used, but it is not a transport-level port of kcp-go. It runs its own reliable-handshake layer above KCP: a channel byte prefixed to every datagram (`1` reliable / `2` unreliable), a Hello/cookie exchange that must complete before any payload moves, and a cookie value mixed into subsequent packets for address-spoof resistance. kcp-go has none of that — its listener creates a session from the first well-formed KCP header it sees. A kcp-go dialer's first datagram would be read by kcp2k as a channel byte plus garbage, and kcp2k's Hello would be read by kcp-go as a segment with a nonsense conv. Bridging would mean forking kcp2k's framing, which is strictly more work than porting the protocol *and* leaves a permanently diverged dependency. |
| **P/Invoke binding to `ikcp.c`** | **Rejected.** Wire-correct by construction, but it puts a native artifact into a NativeAOT build that currently has none, and it needs per-platform build and packaging for linux-x64 (CI + prod) and win-x64 (dev). The protocol is ~700 lines; the build plumbing would outlive it. |
| **Port the kcp-go protocol subset (chosen)** | The KCP protocol is small, frozen, and fully specified by `ikcp.c`, of which kcp-go's `kcp.go` is itself a port. FEC is disabled on the Go side (`KCPDataShards = 0`), so no FEC header ever appears, and the only framing above KCP is kcp-go's optional crypt header. That reduces the surface to: the 24-byte segment header, the ARQ, and a 20-byte crypt header. No new dependency, NativeAOT-clean, and every byte is auditable against the Go source it must match. |

### What the port actually had to match

Three things, none of which is negotiated on the wire — a mismatch in any of them
is a silent failure rather than an error:

1. **Segment framing.** Little-endian `conv(4) cmd(1) frg(1) wnd(2) ts(4) sn(4)
   una(4) len(4)`, several segments packed per datagram up to the MTU. Commands
   81-84 (PUSH/ACK/WASK/WINS).
2. **The crypt header.** kcp-go's block ciphers are *not* an AEAD. Each datagram
   is `nonce(16) | crc32-IEEE(4, little endian) | KCP bytes`, and then the whole
   buffer — nonce included — is AES-CFB encrypted under a **fixed** IV
   (kcp-go's `initialVector`). The random nonce is what makes the first
   ciphertext block differ per packet; the trailing partial block is XORed
   against the live keystream with no padding. `KcpCrypto` reproduces this with
   `Aes.EncryptEcb` as the block primitive, because .NET's built-in CFB mode is
   not guaranteed to be available with the same feedback size everywhere.
3. **Key derivation.** `TRANSPORT_KEY` → 32 bytes, by the same two rules as
   `shared/transport/crypto.go`: 64 hex characters verbatim, anything else
   through HKDF-SHA256 with no salt and the info string
   `rpg-mmo/transport/kcp/aes-256`. Go's `hkdf.Key(sha256.New, k, nil, info, 32)`
   and .NET's `HKDF.DeriveKey(SHA256, k, 32, salt: null, info)` agree — and
   `KcpInteropTests.GoDeriveKey_MatchesCSharpDeriveKey` asserts it against the
   real Go implementation rather than trusting that reading.

The tuning profile (nodelay 1, interval 10ms, resend 2, congestion control off,
128/128 packet windows, MTU 1350, stream mode, ACK-no-delay, FEC off) is copied
from `shared/transport`'s `KCP*` constants into `KcpTuning`. These do not change
the wire *format*, but they do change latency and window behaviour, and an
asymmetric profile is far harder to notice than an outright failure.

### Interop evidence

`interop/kcpprobe` is a small Go client that dials through
`backend/shared/transport` — deliberately not through kcp-go directly, so the
thing being tested is "a client configured the way this project configures its
Go clients", including any drift in the tuning or key handling.

`GameServer.Tests/Net/KcpInteropTests.cs` drives it:

- the same `TRANSPORT_KEY` derives to the same 32 bytes on both sides, for a hex
  key and for a passphrase;
- a Go client completes an echo against the C# listener in plaintext, with a hex
  key, and with a passphrase-derived key;
- a Go client completes a **full game join** (join token → accepted → inputs →
  snapshots showing the player actually moved) against a real `GameServerHost`
  bound with `--transport kcp`, both plaintext and encrypted;
- all three key-mismatch cases (server encrypted/client plaintext, server
  plaintext/client encrypted, both encrypted with different keys) fail closed.

These skip rather than fail when no Go toolchain is present, because the dotnet
CI image has none.

### Deviations from kcp-go, and why they are not observable

- **Data structures.** Plain `List<T>` instead of kcp-go's ring buffers and
  receive heap. Same ordering semantics, O(n) where kcp-go is O(1) on a few
  paths; with a 128-packet window that is not a hot spot worth the machinery.
- **No FEC, no SNMP counters, no trace logging.** FEC is off on the Go side too,
  so a peer configured by `shared/transport` never emits an FEC header. The
  listener explicitly drops packets carrying kcp-go's FEC/OOB type markers
  (0xf1/0xf2/0xf3 at offset 4) with a warning, rather than misparsing them as
  KCP headers.
- **Flush emits after the state machine settles.** kcp-go calls its output
  callback while walking the send buffer. Doing that in C# with a synchronous
  callback (a loopback peer, or any transport that feeds a reply straight back
  into `Input`) re-enters the instance and mutates the list being enumerated.
  `Kcp.Flush` therefore collects datagrams and hands them to the output callback
  only once every state update is complete. Wire output is byte-identical; only
  the ordering of the callback relative to internal bookkeeping changed.

### Session lifecycle

KCP has no handshake and no FIN. A session is created when a datagram arrives
from an unknown endpoint with a well-formed KCP header, adopting the conv from
that packet — exactly kcp-go's `Listener.packetInput`. A datagram from a *known*
endpoint carrying a different conv means the peer restarted; the old session is
replaced, but only if the packet is a fresh session's first (`sn == 0`), so a
stray cannot evict a live player.

Because there is no FIN, two sweeps replace what TCP gets from the kernel: the
ARQ's dead-link detection (20 failed retransmissions of one segment) and a 60s
idle timeout on inbound datagrams. Without the latter a vanished client would
hold its session — and its world entity — forever. Closing the listener tears
down every session directly, since there is no per-peer socket whose close the
kernel would propagate.

### What is and is not encrypted end to end

With `--transport kcp` and `TRANSPORT_KEY` set on both peers, the **gameplay
hop** is genuinely encrypted: every UDP datagram, including the one carrying the
join token, is AES-256 encrypted below the ARQ, with no negotiation and no
downgrade. What that does *not* cover:

- **The client↔gateway hop** is a separate connection with its own transport
  setting. It is encrypted only if the gateway also runs KCP with the same key.
- **TCP mode is unencrypted, full stop.** `TRANSPORT_KEY` is ignored there.
- **A pre-shared key is not per-session key material.** Every server and every
  client in a deployment holds the same key, so it provides confidentiality
  against a passive network observer, not against a peer that already has the
  key. There is no forward secrecy: a leaked key retroactively decrypts captured
  traffic. Per-session key exchange remains the production target.
- **Key distribution is the operator's problem.** A key left unset gets a
  start-up WARNING and nothing more; nothing enforces that the two halves of a
  deployment agree, and when they disagree the only symptom is that joins time
  out.

Coverage: `GameServer.Tests/Net/KcpTransportTests.cs` (crypto vectors, CFB
round-trips including the unpadded tail, ARQ loopback and fragmentation, conv
rejection, listener binding, stream adapter chunking and EOF, transport-kind
normalisation) and `GameServer.Tests/Net/KcpInteropTests.cs` (the Go client).

## Entity Lifecycle and Keyframe Phase (2026-08-07)

### Entity removal has exactly one owner, so a missed removal is permanent

`EcsWorld.AddEntity` has one call site — the join path — and `RemoveEntity` runs from
one place: the reconnect-hold expiry task. There is no sweep, no GC, no reconciliation
against the connection table. That makes the hold task the **sole owner** of every
player entity's removal, and it means a join that attaches an entity without scheduling
a hold leaks it for the life of the process.

That is what happened. `OnPlayerDisconnected` was the last statement of the `try` block,
so it ran only when the connection loop exited normally. An abort after the entity was
attached — most easily the `WriteOneAsync` sending `JoinTokenResp`, against a client
that gave up mid-handshake — skipped it. The entity then stayed in the world, still
scanned by the AOI pass and diffed by the delta encoder on every tick, forever. It turns
a cost bounded by *concurrent* players into one that grows with *cumulative* joins.

Teardown therefore runs from `finally`, gated on an `entityAttached` flag set the moment
the world holds an entity for that user. The rule is: **once the world owns an entity for
a connection, every exit path must hand it to the hold scheduler.**

`players_online` is a separate counter, not derived from the connection table, and it is
incremented *after* the write that could throw. That asymmetry is why the symptom looked
the way it did — correct player count, stuck entity count — and it is why teardown needs
a second flag (`countedOnline`): decrementing a counter an aborted join never incremented
would corrupt the count for the players who really are online.

The expiry task claims its removal with `TryRemove(KeyValuePair)`, which succeeds only
while *its* hold is still the registered one, and additionally declines to remove an
entity that has a live connection. A reconnect that lands during the pre-removal save
must not have its freshly reattached entity deleted underneath it. A narrow residual
window remains — a reconnect can still interleave between the task's claim and its
connection lookup — and closing it fully needs per-user serialisation of the join and
expiry paths, which is not worth the lock today: the consequence is a reconnecting
player being respawned from the state that was just saved, not corruption.

### Keyframe phase is per-connection and derived from the user id

Every connection began its keyframe counter at zero, so a cohort that joined on the same
tick kept keyframing on the same tick. Full state for all of them serialises in one tick,
periodically, which is a latency spike exactly while a server fills up.

`SnapshotDeltaState` now takes a phase offset. Three properties matter:

- **Deterministic, not random.** A random offset spreads load just as well, but the same
  session replayed would produce different frames. Same reasoning as tick-based cooldowns
  above: reproducibility is a property we keep on purpose.
- **FNV-1a over the user id, not `string.GetHashCode()`.** .NET randomizes string hashing
  per process, so the built-in hash is stable within a run and different across runs —
  precisely the non-determinism being avoided.
- **Applied once, after the join keyframe.** It shortens exactly one cycle. Applying it
  on every keyframe would permanently shorten this client's period to
  `interval - phase`, giving it more keyframes, and more bandwidth, than its peers.

The parameterless constructor is unstaggered and behaves exactly as before.

The keyframe period is `interval + 1` snapshots, not `interval` — the counter is compared
before being incremented. That predates the stagger and is left alone; the tests derive
the period rather than hard-coding it, so they describe what the code does.

Coverage: `GameServer.Tests/Server/EntityLifecycleTests.cs` (clean disconnect, aborted
join, cohort join/leave, reconnect-within-hold, online-count integrity) and
`GameServer.Tests/Snapshot/KeyframeStaggerTests.cs` (spread, determinism, single
shortened cycle, unstaggered default).

## The join handshake runs on a throwaway Connection (2026-08-07)

`GameServerHost` accepts a socket, wraps it in a **temporary** `Connection` to
read `MsgJoinToken` and verify the JWT, and only then constructs the *real*
session `Connection` over that same socket. The temporary one is discarded.

**Any per-connection state established during the handshake is therefore lost
unless it is explicitly handed to the session connection.** This is not
hypothetical: the Protobuf migration hit it immediately. The client's wire
encoding is latched from the first frame decoded, which is the handshake frame
on the temporary connection — so every reply silently fell back to legacy JSON
until the encoding was threaded through the session constructor. The bug was
invisible to every single-encoding test and only surfaced against a client that
spoke Protobuf and expected Protobuf back.

The shape generalises: negotiated compression, a client-declared protocol
version, a per-connection cipher, an observed MTU — each would be dropped the
same way, and each would fail silently rather than loudly, because the fresh
connection's defaults are always *valid*, just wrong.

If you add per-connection state, add it to the `Connection` constructor and hand
it over at the handoff in `HandleConnectionAsync`. A default parameter is fine
for the legacy value; what is not fine is assuming the handshake's observations
survive on their own.

Two cleaner fixes exist and were both considered too invasive for the migration:
carry the handshake result in a small struct that the session constructor takes
wholesale, or stop rebuilding the connection at all and make `UserId` mutable
after verification. The second is probably right long term — the rebuild exists
only because `UserId` is `readonly` and unknown until the JWT is checked.

## Entity storage: Arch ECS (2026-08-11)

### What replaced `GameWorld`

`GameServer/World/GameWorld.cs` — a `Dictionary<string, EntityState>` behind a
`ReaderWriterLockSlim` — is deleted. `GameServer/World/EcsWorld.cs` replaces it and
stores every entity in an [Arch](https://github.com/genaray/Arch) world (ADR-10).
Arch owns entity identity, component storage, queries and iteration order; there is
no second store and no fallback path.

`EntityState` is decomposed into seven components plus one tag
(`GameServer/World/Components.cs`):

| Component | Fields | Notes |
|---|---|---|
| `EntityIdRef` | `string Value` | Still a string, still equal to the user id — see below |
| `EntityKind` | `string Value` | `"player"` / `"npc"` / `"mob"` / `"boss"` |
| `Position` | `Vec2 Value` | |
| `Health` | `Hp`, `MaxHp`, `Dead` | |
| `Combat` | `Attack`, `Defense`, `CooldownUntilTick` | Cooldown is a simulation tick |
| `Locomotion` | `Speed` | |
| `InputCursor` | `LastInputTick` | The client's `ack_tick` |
| `PlayerTag` | — | Archetype tag mirroring `EntityKind == "player"` |

Two archetypes exist: player (all eight) and non-player (the first seven). The
persistence sweep (`AsyncSaver.SaveAllAsync`) is now an archetype query on
`PlayerTag` rather than a full scan with a string comparison per entity.

### What `EcsWorld` owns on top of Arch, and why

Three things, none of which Arch provides:

1. **A `string -> Entity` index.** `EntityState.Id` is still a `string` and is still
   the user id. ADR-10 calls for an integer simulation handle; that migration reaches
   persistence, the snapshot encoder and the reconnect/hold bookkeeping (~21 call
   sites) and is deliberately **not** part of this change. Until it lands,
   `EntityIdRef` puts a managed reference in every chunk — the exact cost ADR-10 says
   the handle exists to remove.
2. **The reader/writer lock.** Arch's `World` is not thread-safe, and network threads
   spawn/despawn entities and push input while the tick loop reads. The lock discipline
   is unchanged from `GameWorld`; it is now protecting something that genuinely
   requires it.
3. **A deferred structural-change phase**, below.

Everything else — lookup, mutation, range scan, player enumeration — goes through Arch
queries and chunk spans. `GetEntitiesInRange` iterates chunks and materialises an
`EntityState` only for entities that pass the distance test, instead of copying every
entity as the dictionary scan did.

### Chunk iteration, not the delegate `Query` overloads

Arch's ergonomic `world.Query(in desc, (ref A a) => ...)` allocates a closure whenever
the lambda captures anything, which a tick loop's lambdas invariably do. `EcsWorld`
uses `GetChunkIterator()` + `chunk.GetSpan<T>()` throughout — the allocation-free shape,
and the one the AOT spike exercised.

### No `CommandBuffer`; structural changes are an explicit phase

ADR-11 measured that `Arch.Buffer.CommandBuffer` throws `NullReferenceException` inside
`Arch.Core.World.Has<T>` under NativeAOT even with array hints in place. It is not used
here at all.

Instead, spawns and despawns raised while a query is being iterated are queued and
applied by `EcsWorld.ApplyStructuralChanges()`, which `TickLoop.TickOnce` calls once per
tick before anything iterates.

**Be honest about what this currently does:** the queue is normally empty. The
reader/writer lock already prevents a writer from overlapping a reader, and nothing in
the present call graph mutates the world during iteration, so structural ops take the
immediate path. The deferral is a backstop that keeps "nothing mutates during
iteration" from being an unstated assumption that a future system quietly breaks. The
re-entrancy counter that drives it is `[ThreadStatic]`, because same-thread re-entrancy
is the only case the lock does not already exclude.

### Two ways in: `Update(get, set)` and `UpdateComponents(writer)`

`EcsWorld` exposes the world at two granularities, and which one a caller uses is the
current dividing line of the ECS migration.

`Update(get, set)` is the original, whole-entity form: `get` composes an `EntityState`
out of all seven components, `set` writes all seven back. It is convenient and it is
expensive in a way that scales with how often it is called, not with how much actually
changed — moving a player costs fourteen component lookups plus two managed-reference
stores, because `EntityIdRef` and `EntityKind` are rewritten even though neither can
change after spawn.

`UpdateComponents(Action<WorldWriter>)` takes the same write lock and runs the same
deferred structural phase, but hands the callback a `WorldWriter`: `Resolve(id)` returns
an `EntityHandle`, and `PositionOf`/`HealthOf`/`CombatOf`/`LocomotionOf`/`InputCursorOf`
return `ref` to the individual components. A system then reads what it reads and writes
what it writes. The string id is paid for once per entity per scope instead of once per
access — the first half of ADR-10's integer simulation handle, with the wire, the
persistence layer and `EntityState.Id` all still on strings.

`EntityHandle` wraps Arch's `Entity` so no `Arch.Core` type is public, and it is only
valid inside the scope that produced it: a structural change can move an entity's slot
and nothing revalidates a stored handle.

**Who is on which side today.** The per-tick input path and `TryGetSnapshotAnchor` (the
per-connection AOI centre + ack tick) use the component form. `EnemySpawner`,
`AsyncSaver`, `GetEntitiesInRange` and the join/leave paths still use the whole-entity
form. The attack branch of `InputHandler` is deliberately mixed: it composes whole
`EntityState` values because `CombatLogic` and the death callback are `Shared.GameLogic`
entry points shaped that way, but its write-back is component-level.

**The AOI scan fills a caller-owned buffer.** `GetEntitiesInRange` used to open with
`new List<EntityState>()` and was called once per connected client per tick. There is now
a `Span<EntityState>` overload whose overflow contract is deliberately the same one
`AoiLogic.GetNearbyEntities` publishes — *count, do not saturate*, so a short buffer
returns the size it needed to be rather than a saturated length that cannot be
distinguished from an exact fit. Each `Connection` owns its buffer and grows it once.
Both forms share one scan implementation: the delta encoder's bookkeeping is
order-sensitive, so a divergence in iteration order between them would be a wire change.

**Input is bound to its entity at ingest.** `PushInput` resolves the user id on the
network thread, so the simulation thread never hashes a string — movement coalescing is
keyed by `EntityHandle`. `_index` is still the authority for join, reconnect and
persistence; it is just off the per-input path. A handle can be invalidated by
destruction (not by archetype moves — Arch's `Entity` is a stable identity), so
`RebindStale` re-resolves the reconnect case at the top of the input phase.

Measured end to end on `TickLoop.TickOnce`, the same probe run on this branch and on
`develop` (Release, real `Connection` objects over a null transport, Protobuf, clustered
so AOI matches): **436 276 → 21 692 B/tick at 50 players (20×)** and **6 762 858 →
192 984 B/tick at 200 players (35×)**. Allocation is deterministic to under 0.05% across
paired runs. Wall-clock is *not* claimed: this host's spread on one binary is ±50%, which
is the contamination ADR-7 documents.

**Where the risk moved.** The movement step still calls `MovementSystem.TryMove` — the
arithmetic is not re-derived, and the golden vectors (ADR-10) are untouched. What is new
is that it is handed only the three fields `TryMove` reads rather than a composed
entity, and that assumption would fail *silently* if `TryMove` grew a fourth. So
`ComponentInputPathTests.Movement_ComponentPath_IsBitExactAgainstWholeEntityTryMove`
replays every position/speed/move/bounds combination in the movement fixture through the
real handler and compares the stored position bit-for-bit against `TryMove` called with
a fully populated `EntityState`.

**One preserved oddity.** A self-targeted attack applies its cooldown and fires the death
callback but discards its damage. That is the `get`/`set` path's behaviour — its final
`set(userId, attackerCopy)` overwrote the target write when attacker and target were the
same entity — and component writes have no such last-writer-wins accident, so the new
path discards it explicitly to keep the wire output identical. It is pinned by a test
named after what it is, not fixed here.

### The enemy AI: three systems over an archetype query

The AI is `EnemySpawnSystem` → `EnemyMoveSystem` → `EnemyReapSystem`, run in
`EnemyAiPhase` order inside one world write scope. It replaced a single
`Tick(get, set, tick)` that walked a `List<string>` of enemy ids and round-tripped a whole
`EntityState` per enemy per tick.

**Order is load-bearing, and explicit rather than declarative.** Spawn runs first so a new
enemy takes its first step on the tick it appears — the original got that by spawning into
the list it was about to walk, and it is visible in the snapshot. Reap runs last because
"arrived at the centre" is a fact the move system produces earlier in the same tick.
There is no `[UpdateInGroup]` because there is no server-side group tree: the one in the
codebase is the Unity *client* package's DOTS scheduler, and the server-side equivalent
would be `Arch.System`'s source generator, banned by ADR-12 because `ArchAotHintTests`
cannot enumerate the query shapes it generates.

**`EnemyAi` is ownership, not type.** Entities carry the tag only if the spawner created
them, and it is preserved rather than re-derived when an existing entity is written back.
Deriving it from `EntityKind.Value == "mob"` would put every test-placed mob on a march to
the origin; re-deriving it on update would strip it from any enemy written back through
`AddEntity`, which the combat path does on every hit.

**This is where the deferred structural phase earns its keep.** Despawns are raised inside
the write scope and drained by `ApplyStructuralChangesLocked` on the way out — before the
snapshot broadcast, so a client never observes an enemy inside the despawn radius. The old
shape could not do this: `RemoveEntity` inside the lock would deadlock, so ids were
collected into a `PendingRemovals` list the tick loop drained after releasing it. Op kinds
are still just *add* and *remove*; `EnemyAi` rides on the add as a tag payload.

**Enemies deliberately do not use `MovementSystem`.** Their step is unclamped by map bounds
and normalises with a reciprocal square root. It was preserved character-for-character and
is pinned bit-exactly, because unifying the two movement models would move every enemy onto
different floats — a gameplay decision with a wire consequence, not a refactor.

Measured: the AI phase went 367 → 172 B/tick (−53%), deterministic across paired runs. In
context that is **0.36%** of a 50-player tick, because snapshot encoding dominates. Per
ADR-12 that is recorded, not dressed up.

### The snapshot broadcast: gather under one lock, encode under none

`TickLoop.TickOnce` broadcasts in two phases. Phase A calls `EcsWorld.ReadAll` once and,
inside that single read scope, has every `Connection` gather its AOI anchor and its
visible entities into buffers the connection owns. Phase B leaves the scope and encodes
and sends, touching no world state.

Before this, each viewer took the read lock twice — anchor, then AOI scan — so a
200-player tick acquired it 400 times, and `WireProtocol.NewEnvelope` ran interleaved
between those acquisitions.

**The measurable effect is nil and that is the honest summary**: −0.3% allocation at 50
players, noise at 200. The AOI inner loop was already chunk-iterating and compose-free and
stage 1 had already removed its per-client list, so there was nothing left there.

**The structural effect is the reason it exists.** Serialization still runs inside the
tick. It could not be moved out while encoding was interleaved with locked world reads,
because no point in the tick had a viewer's snapshot input standing free of the world.
After phase A every connection holds a self-contained view — no world reference, no lock —
so phase B can move to another thread without `EcsWorld` being involved. Whether to do
that is BENCHMARK.md §9's outstanding item, and it is the one with a measured case behind
it.

The trade: a join or leave arriving mid-broadcast waits for the whole gather rather than
slipping between two viewers. The gather is position tests over chunk spans with no
serialization in it, which is why serialization was moved out of the locked phase rather
than left in it.

Wire output is unchanged, and is proven so rather than asserted: `SnapshotByteIdentityTests`
SHA-256s every snapshot envelope of a deterministic 120-tick scenario, for Protobuf and
JSON separately, against digests generated before the change.

### Snapshot encoding runs on the connection, not on the tick

The tick stages each viewer's AOI view on its own `Connection` and signals; the
connection's existing write task encodes the `SnapshotMessage`, serializes it and writes
it. Tick-thread allocation at 200 players went from 192 935 to 32 B/tick. Total CPU is
unchanged — this moves work, it does not remove it, so it is a win where there are spare
cores and roughly a wash on a single-vCPU pod.

**Ordering is structural.** One write task per connection reads one channel, so tick
N+1's frame cannot overtake tick N's. The queue carries a `SendItem` that is either a
built envelope or a "snapshot staged" marker, so both share that one ordered path.

**Two AOI buffers, and two is provably enough.** Buffer selection belongs to the tick
thread and advances only when the previous job was claimed, so the buffer an encoder holds
is never the one being written, and an unclaimed job is overwritten in place.

**Back-pressure coalesces to the newest staged snapshot, losslessly.** A snapshot that is
never claimed is never encoded, so it never advances the delta encoder's `_lastSent`, and
the next one encoded carries every change since the last snapshot actually sent. This
*fixed* a pre-existing bug: the old order encoded on the tick, advanced `_lastSent`, and
only then handed the envelope to a bounded channel that drops the oldest under load — so a
dropped frame's updates were recorded as sent and never retransmitted until the next
keyframe.

The visible consequence is that **ticks and snapshots are no longer 1:1 when the writer
lags**. A lagging client now gets fewer, fresher snapshots instead of a backlog of stale
ones. That is a deliberate behaviour change, not an internal detail.

**Two bugs were made and caught here, both silent by nature.** Gating the marker on "no
job already pending" is a permanent starvation bug, because the bounded queue drops the
oldest and a lost marker would have stopped that connection's snapshots forever; markers
are now unconditional and surplus ones cost nothing. And reading the buffer index outside
the lock while the write task flipped it let a slow gather write into the buffer being
encoded.

**What this measured about the tick, which matters more than the change itself.** Splitting
the old broadcast by hand at 200 players: AOI gather ~874–1177 µs/tick,
`SnapshotDeltaState.Encode` ~998–1272 µs/tick, protobuf `ToByteArray` ~79–144 µs/tick.
Serialization proper is 4–6% of the tick, not the 80% the original analysis assumed. The
two real terms are the brute-force AOI scan (a spatial index is the standing production
item) and `Encode`'s 134 699 B/tick of `EntitySnapshot` objects, which is a pooling
problem. Neither is an ECS problem.

### Systems, the schedule, and where simulation state lives

The enemy phase is three `IEcsSystem`s in a `SystemSchedule`, run inside one world write
scope. Each declares an `Order` and a `ComponentAccess` of reads and writes; ordering is
declared, not the order three calls happen to appear in, and a duplicate `Order` is
rejected at construction.

**Systems iterate chunks when the work is per-entity-linear.** `EnemyMoveSystem` walks
`Span<Position>` and `Span<Health>` through a `SimChunk` view, with the per-chunk body a
`struct` visitor passed by `ref` so the call devirtualises and nothing is allocated.
`EnemySpawnSystem` and `EnemyReapSystem` keep handle access on purpose — spawn creates and
has no array to walk; reap decides per entity and then performs a structural change, which
needs an identity a component span does not carry.

**Simulation state lives in the world.** The spawner's wave accumulator and id counter were
private fields on the system — invisible to the world, so unsnapshotable, unpersistable,
and not reset when the world is. They are an `EnemySpawnState` component on a singleton
entity that carries only that component, so it matches none of the queries requiring the
seven standard ones and can never surface in a snapshot, an AOI scan or the entity count.
`SimulationStateArchitectureTests` now fails the build on any mutable instance field in a
phase or system, with `[SimulationScratch]` the one sanctioned exception for buffers that
carry nothing between ticks. It was verified to fire by putting the original field back.

**The core does not name the gameplay.** `CountWith<TTag>()` and
`QueryWith<TTag>(Span<EntityHandle>)` replaced an `EnemyCount` property and an enemy-named
query. The status endpoint's number comes from `ServerOptions.StatusEntityCount`, supplied
by `Program.cs`; the `EnemiesAlive` JSON field name is a client contract and is unchanged.

**Cost of the generator ban.** `SimChunk` exposes one fixed component set because a general
N-component chunk query needs either a source generator — banned, since the AOT hint guard
cannot enumerate generated query shapes — or a combinatorial hand-written API. A system
with a different set means adding an explicit shape or justifying handle access.

This measured nothing: 108 B/tick before and after. Steady state is 4–6 enemies. It was
taken as a shape change, before gameplay is written against the seam.

### The `Shared.GameLogic` boundary is unchanged

No `Arch.Core` type appears anywhere in `Shared.GameLogic` — not `World`, not `Entity`,
not `QueryDescription`, not a component attribute. `EcsWorld` composes an `EntityState`
out of components on the way out and writes components back on the way in; the shared
static functions (`MovementSystem.TryMove`, `CombatLogic.*`, `Vec2.DistanceSq`) are
called with plain structs and never see the ECS.

The cost is a struct copy per entity per read, on the paths that still compose one — the
input path no longer does (see "Two ways in", above). It is the price of keeping the
client's prediction code free of a pre-1.0 server dependency (ADR-10, "Why not share the
ECS"), and it is why the remaining round trips are being removed one caller at a time
rather than by reshaping `Shared.GameLogic`.

### AOT hints: what breaks, and the guard that stops it (ADR-11)

`Arch.Core.Chunk` allocates one backing array per component type through
`System.Array.CreateInstance(Type, int)`. Under NativeAOT the array type `T[]` for a
user-defined struct exists only if ILC saw it constructed statically somewhere.
`GameServer/World/ArchAotHints.cs` constructs one array per component type in a
`[ModuleInitializer]`.

**This was re-measured on this branch, not taken on trust.** Commenting out the single
`new Locomotion[1],` line and publishing produced:

- `dotnet build`: clean.
- `dotnet test`: 500 passed, 0 failed. The suite runs on CoreCLR with a JIT and
  structurally cannot see this.
- `dotnet publish -c Release`: clean. **No warning naming `Locomotion`.**
- The native binary: started, logged `Game server listening on 127.0.0.1:...`,
  accepted the TCP connection — and then threw on the first player join:

  ```
  System.NotSupportedException: 'GameServer.World.Components.Locomotion[]' is
  missing native code or metadata.
  ```

Note where it threw. The first archetype creation is the **first player spawn**, not
startup. A smoke check that starts the binary and confirms it listens would report
green on a binary that cannot accept a single player. The CI smoke step therefore runs
the real cross-language handshake (`GAMESERVER_NATIVE_BIN` in
`.github/workflows/ci-dotnet.yml`), not a liveness probe.

**The guard.** `GameServer.Tests/World/ArchAotHintTests.cs` reflects over the GameServer
assembly, collects every struct that is either declared in `GameServer.World.Components`
or carries `[EcsComponent]`, and fails when one is absent from
`ArchAotHints.HintedComponentTypes`. That property is derived from the constructed
arrays themselves via `GetElementType()`, so there is no second list to drift out of
sync — the guard checks the hints, not a description of them. A companion test rejects
stale entries in the other direction.

**The guard has been observed to fire.** Adding an unhinted `GuardProbe` component made
`EveryComponentType_IsHintedForNativeAot` fail naming `GuardProbe` and telling the
author which line to add. It was then removed.

### Known follow-ups

- **ADR-7's benchmark numbers are void.** 45.9 KB/s per client and ~82 MiB at 200
  players were measured against `GameWorld`. Storage changed underneath the tick loop;
  re-run `backend/docs/BENCHMARK.md` before quoting either again.
- **`EntityState.Id` is still a `string`.** See above. Arch's ergonomics make the split
  more attractive than before — the index lookup is now the only hash on the read path,
  and it would disappear — but it is a separate piece of work.
- **Arch pulls in `Collections.Pooled` 2.0.0-preview.27, which produces AOT and trim
  analysis warnings** (`IL3053`, `IL2104`) on publish. Every other dependency in
  `GameServer.csproj` is warning-free, so this is a new exception to that file's
  standard. The binary works — the E2E suite passes against it — but the warnings are
  unexamined, and they are the noise a future real warning would hide in.

## Collections.Pooled AOT warnings (audited 2026-08-11)

`Arch` pulls in `Collections.Pooled 2.0.0-preview.27`, and it is the only dependency in
`GameServer.csproj` that is not AOT-clean. The publish summarises it as two lines:

```
warning IL3053: Assembly 'Collections.Pooled' produced AOT analysis warnings.
warning IL2104: Assembly 'Collections.Pooled' produced trim warnings.
```

Published with `-p:TrimmerSingleWarn=false`, those expand to **37 individual
diagnostics — 19 IL3050 and 18 IL2026 — all in a single type**,
`Collections.Pooled.PooledEnumerableJsonConverter` and its six nested
`*ConverterInner<T>` classes. They are the expected trio for a reflection-based JSON
converter: `Type.MakeGenericType`, `JsonSerializerOptions.GetConverter`, and the
reflection-based `JsonSerializer.Serialize`/`Deserialize` overloads.

### Why the converter is in the binary at all

`PooledList<T>`, `PooledSet<T>`, `PooledQueue<T>`, `PooledStack<T>`,
`PooledCollection<T>` and `PooledObservableCollection<T>` each carry
`[JsonConverter(typeof(PooledEnumerableJsonConverter))]` (confirmed by reading the
assembly's metadata). That attribute is a static reference from a type Arch uses to the
converter, so ILC roots the converter, compiles it — 69 method bodies appear in the ILC
map file — and analyses it. **The warnings are produced by the attribute, not by any
call site.** Nothing has to call the converter for them to appear.

### Why it cannot fire

A `JsonConverterFactory` is invoked only when System.Text.Json builds a `JsonTypeInfo`
for an annotated type, and only the **reflection-based resolver** consults
`[JsonConverter]` on an arbitrary type. Four facts, each checked separately:

1. This assembly never uses that resolver. All three `JsonSerializer` call sites
   (`EventPublisher`, and two in `JwtValidator`) pass a source-generated `JsonTypeInfo`.
   The serialised types — `DeathPayload`, `JwtHeaderJson`, `JwtClaimsJson` — are strings
   only; no collection of any kind.
2. `Arch.dll` has **zero** System.Text.Json member references. It uses the Pooled
   collections as storage and never serialises them.
3. `Collections.Pooled` has **no `ModuleInitializer` and no `<Module>` cctor**, so it
   never registers these converters globally into a shared `JsonSerializerOptions`.
4. No source file in `GameServer/` or `Shared.GameLogic/` names a Pooled type.

### The trigger condition, and why it is not the Arch-hint hazard

For any of the 37 to become a real failure, someone would have to (de)serialise a Pooled
collection — directly or as a member of another type — through a reflection-based
`JsonSerializer` overload. `CreateConverter` would then call `Type.MakeGenericType`, and
under NativeAOT that instantiation may not exist in the binary.

This is worth contrasting with ADR-11's missing-hint bug, because they look similar and
are not. **The hint bug fired on an ordinary action — a player joining.** No new code was
needed; the hazard was latent in the normal path and only the rarity of an archetype
decided when you noticed. **This one requires code that does not exist**, and writing it
would be a deliberate act.

### The guard

That distinction is what the justification rests on, and it is a property of *our* code
rather than of the dependency — so it is enforced rather than asserted.
`GameServer.Tests/Aot/JsonReflectionGuardTests.cs` reads the compiled GameServer
assembly's metadata, finds every `JsonSerializer` member reference, and fails on any
whose signature lacks a `JsonTypeInfo` or `JsonSerializerContext` parameter. It reads
metadata rather than source, so generated code counts and a `using` alias or helper
wrapper cannot hide a call.

**Verified to fire:** replacing `EventPublisher`'s source-generated call with the
reflection-based `JsonSerializer.SerializeToUtf8Bytes(payload)` failed the test, naming
`JsonSerializer.SerializeToUtf8Bytes(!!0, JsonSerializerOptions)`. Reverted.

### What was rejected

- **Suppressing with `NoWarn`.** The whole value of `GameServer.csproj`'s convention is
  that every entry was actually checked; a suppression would make the next genuinely
  dangerous dependency indistinguishable from this one. The summary IL3053/IL2104 lines
  stay as the standing reminder that this package is on a `-preview` version.
- **`<JsonSerializerIsReflectionEnabledByDefault>false</JsonSerializerIsReflectionEnabledByDefault>`.**
  Tried it; **the warning count stayed at exactly 37.** The feature switch trims the
  reflection resolver, but the `[JsonConverter]` attribute roots the converter
  independently, so ILC still compiles and analyses it. It would be a defensible setting
  on its own merits — it turns "we only use source-generated JSON" into a runtime
  guarantee — but it does not address these warnings and is not part of this change.

### Standing risk

`Collections.Pooled 2.0.0-preview.27`'s newest target framework is **`netcoreapp3.0`**;
the build resolves `lib/netcoreapp3.0/` into a .NET 10 NativeAOT binary. That is a large
version gap on a pre-release package this project did not choose directly. Re-run this
audit on any Arch upgrade — `dotnet publish -p:TrimmerSingleWarn=false` and diff the
diagnostic list against the 37 recorded here.

