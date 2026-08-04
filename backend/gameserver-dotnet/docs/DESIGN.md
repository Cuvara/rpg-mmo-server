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

The Go gateway remains unchanged. It routes traffic to whichever backend
(Go or C#) is allocated by Agones. The wire protocol is identical.

## Shared.GameLogic Constraints

These constraints exist so the library compiles cleanly in both a standard
.NET 9 project and a Unity 2022+ project with an Assembly Definition:

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

## Anti-Cheat

All validation is server-authoritative:

- **Speed hack**: Server compares displacement between ticks against maximum
  allowed speed. Excessive movement is clamped or rejected.
- **Range check**: Attack and skill targets must be within range according to
  the shared logic's distance calculation.
- **Cooldown enforcement**: Skill cooldowns are tracked server-side. Early
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
