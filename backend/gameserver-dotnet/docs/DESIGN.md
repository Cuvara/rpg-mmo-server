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
.NET 10 project and a Unity 2022+ project with an Assembly Definition:

- **Zero Unity dependencies** — must compile as a standard .NET class library.
  No `UnityEngine`, `Unity.Mathematics`, or `Unity.Collections` references.
- **No server-specific code** — no networking, no persistence, no logging
  framework. The server wraps shared logic with its own I/O layer.
- **All game constants centralized** — damage formulas, speed caps, cooldown
  durations, AOI radius — all live in `Shared.GameLogic` so both sides agree.
- **Value types (struct) for performance** — hot-path data structures are
  structs to avoid GC pressure, which matters for both server tick loops and
  Unity DOTS jobs.
- **No allocations in hot paths** — methods called per-tick must not allocate
  heap objects. Use `Span<T>`, stackalloc, or pre-allocated buffers.

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
  connection handlers take read locks.
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
DOTS client links the same `Shared.GameLogic` assembly and calls the identical
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
