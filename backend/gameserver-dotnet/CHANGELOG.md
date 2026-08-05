# Changelog

All notable changes to the GameServer .NET module will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Added
- `Shared.GameLogic.Systems.MovementSystem` — server-authoritative, deterministic,
  allocation-free movement model shared with the Unity client for prediction:
  `ResolveDirection` (normalize/clamp/reject a raw input vector), `Integrate`
  (`position += direction * speed * dt`, bounds-clamped), `TryMove`,
  `DeltaTimeForTickRate`, `MaxDisplacementPerTick`, `IsDisplacementLegal`
- `MoveResult` enum (`None` / `Accepted` / `Clamped` / `Rejected` / `Blocked`) —
  validation results are returned by value, never thrown
- `Shared.GameLogic.Components.MapBounds` — axis-aligned play area with per-axis
  clamping (`FromSize`, `Default`, `Contains`, `Clamp`). Default 1000x1000 world
  units centered on the origin; configurable via `--map-width` / `--map-height`
  (`GAMESERVER_MAP_WIDTH` / `GAMESERVER_MAP_HEIGHT`) and `ServerOptions.MapBounds`.
  Positions restored from the player store are clamped on join
- `GameConstants`: `MaxInputMagnitude`, `InputDeadzoneSq`, `MaxDeltaTime`,
  `DisplacementTolerance`, `DefaultMapWidth`, `DefaultMapHeight`
- Movement tests: direction matrix, diagonal-vs-cardinal parity, dt scaling across
  tick rates, bounds clamping at all four edges + corners + edge sliding, validator
  accept/clamp/reject/block matrix, determinism, tick-loop integration with scripted
  input sequences and an input-spam anti-cheat regression test
- OpenTelemetry metrics (Meter `rpg.gameserver`) with a Prometheus scrape
  endpoint + `/healthz` on `--metrics-addr` / `METRICS_ADDR` (default `:9101`,
  empty disables). Instruments: tick duration histogram (66 ms budget buckets),
  processed inputs, players online, entities, snapshots sent, player saves by
  status, events published by type. Windows dev falls back to a localhost
  prefix when the wildcard bind needs an URL ACL. See docs/METRICS.md.

### Changed
- **Wire semantics (protocol format unchanged)**: `move_x`/`move_y` in `MsgInput`
  now carry a movement **direction**, not a per-message displacement. The server
  integrates `direction * speed * dt` (`dt = 1 / tickRate`) once per tick. Vectors
  with magnitude > 1 are normalized, so diagonal movement is no longer faster than
  cardinal; magnitude > 1.5, NaN and infinity are dropped and logged at Debug
- `EntityState.Speed` is now **world units per second** instead of a per-tick
  displacement multiplier; `ServerDefaults.DefaultPlayerSpeed` 1.0 → 5.0 u/s
- `TickLoop` coalesces buffered inputs: only the newest input per player per tick
  performs the movement integration (superseded inputs still resolve their attack).
  Closes the speed hack where movement scaled with client packet rate
- `InputHandler` takes the tick rate and map bounds; `ProcessInput` /
  `ProcessInputLocked` gained an `applyMovement` flag (defaults to `true`)
- `ValidationLogic.ValidateInput` audits the input direction via `MovementSystem`
  (timestep-independent) instead of a per-tick distance cap
- Travel distance is now tick-rate independent — tick rate is a smoothness knob,
  not a balance knob
- docs/DESIGN.md: dated "Movement Model" section (rationale, buffering choice,
  validation table, Unity prediction reuse plan); docs/README.md input semantics,
  flags, and Unity DOTS example updated

### Removed
- `Shared.GameLogic.Systems.MovementLogic` (`ApplyMove` / `ValidateMove`) and
  `GameConstants.MaxMovePerTick` — superseded by `MovementSystem`

### Fixed
- JWT claim field names (`user_id`/`server_id` → `sub`/`sid`) to match Go
  gateway wire format — cross-language token validation now works correctly

## [0.1.0] - 2026-08-04

### Added
- Initial C# port of Go game server
- `Shared.GameLogic` library with pure C# game logic (movement, combat, validation, AOI)
  - Designed for sharing between .NET server and Unity DOTS client
  - Zero Unity dependencies — standard .NET 10 class library
- `GameServer` .NET 10 console application
  - Wire protocol compatible with existing Go gateway (4-byte length prefix + JSON)
  - Server-authoritative tick loop at configurable rate (default 15Hz)
  - Thread-safe GameWorld with reader-writer locking
  - Input validation and anti-cheat (speed hack, range, cooldown)
  - Combat system (damage calculation, death handling)
  - AOI-filtered snapshot broadcasting
  - Async batch persistence (in-memory store, PostgreSQL interface ready)
  - Agones SDK interface (NoopSdk for local dev)
  - Event publisher interface for cross-server events
  - HS256 JWT validation (shared secret with gateway)
  - Entity hold on disconnect (30s map / 60s dungeon reconnect window)
  - Graceful shutdown on SIGINT/SIGTERM
- `GameServer.Tests` — comprehensive xUnit test suite
- NativeAOT publish support for minimal container images
- Docker multi-stage build (`deploy/docker/Dockerfile.gameserver-dotnet`)
- GitHub Actions CI pipeline (`ci-dotnet.yml`)
