# GameServer .NET

C# port of the Go game server. Shares game logic with Unity DOTS client via `Shared.GameLogic`.

## Architecture

The solution contains three projects:

```
gameserver-dotnet/
  Shared.GameLogic/      Pure C# game logic library (shared with Unity client)
  GameServer/            Server application (.NET 10 console, NativeAOT)
  GameServer.Tests/      xUnit test suite
```

### Shared.GameLogic

A standard .NET 10 class library with **zero Unity dependencies**. Contains all
deterministic game logic: movement validation, combat calculations, cooldown
checks, AOI (Area of Interest) queries, and game constants.

This library is designed to be imported by the Unity DOTS client as a local
package or Git submodule, enabling client-side prediction with identical code
paths on both server and client.

### GameServer

A .NET 10 console application that hosts the authoritative game world. It speaks
the same wire protocol as the Go game server, so the Go gateway cannot
distinguish between Go and C# backends.

### GameServer.Tests

xUnit-based test suite covering shared logic, server systems, protocol encoding,
and integration scenarios.

## Quick Start

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- For NativeAOT publishing: `clang` and `zlib` development headers

### Build & Run

```bash
# Build the entire solution
dotnet build

# Run the game server (from repo root or gameserver-dotnet/)
dotnet run --project GameServer -- --addr=:9000 --map-id=map_01

# Run with all flags
dotnet run --project GameServer -- \
  --addr=:9000 \
  --map-id=map_01 \
  --tick-rate=15 \
  --map-width=1000 \
  --map-height=1000 \
  --jwt-secret=dev-secret-change-me \
  --agones \
  --redis=localhost:6379
```

### Configuration

Every flag has an environment-variable equivalent; the flag wins when both are
set. Flags are **space-separated** (`--addr :9000`).

| Flag | Environment variable | Default | Description |
|------|----------------------|---------|-------------|
| `--mode` | `GAMESERVER_MODE` | `map` | `map` or `dungeon` (dungeon uses a 60s reconnect hold) |
| `--addr` | `GAMESERVER_ADDR` | `:9000` | Game traffic listen address |
| `--map-id` | `GAMESERVER_MAP_ID` | `map_01` | Map identifier, also the `map_id` metric label |
| `--server-id` | `GAMESERVER_ID` / `POD_NAME` | random | Server identity checked against the join token |
| `--capacity` | `GAMESERVER_CAPACITY` | `100` | Maximum concurrent players |
| `--tick-rate` | `GAMESERVER_TICK_RATE` | `15` | Simulation ticks per second |
| `--keyframe-interval` | `GAMESERVER_KEYFRAME_INTERVAL` | `30` | Delta snapshots between full keyframes; `0` disables delta encoding (see `docs/API.md`) |
| `--map-width` | `GAMESERVER_MAP_WIDTH` | `1000` | Map width in world units |
| `--map-height` | `GAMESERVER_MAP_HEIGHT` | `1000` | Map height in world units |
| `--jwt-secret` | `JWT_SECRET` | *(empty)* | HS256 secret shared with the gateway |
| `--metrics-addr` | `METRICS_ADDR` | `:9101` | Prometheus `/metrics` + `/healthz`; empty disables |
| `--game-db-url` | `GAME_DB_URL` | *(unset)* | Game-state PostgreSQL DSN — see below |
| `--migrate-only` | `GAMESERVER_MIGRATE_ONLY=true` | off | Apply pending migrations, then exit — see below |
| `--agones` | `AGONES_ENABLED=true` | off | Enable the Agones SDK integration |

#### Player state persistence (`GAME_DB_URL`)

When set, player state is persisted to the game-state PostgreSQL database and
survives a server restart. When unset the server falls back to an in-memory
store and **all player state is lost on restart** — the startup log says which
store is active.

```bash
dotnet run --project GameServer -- \
  --addr :9000 \
  --game-db-url 'postgres://game:localdev@localhost:5433/gamestate?sslmode=disable'
```

Accepted formats are a libpq URL (above) or a native Npgsql keyword string
(`Host=...;Database=...;Username=...;Password=...`). The password is masked in
every log line.

The schema is created on boot if missing (idempotent), so pointing at an empty
database is enough. If the database is configured but unreachable the server
logs a critical error and **exits with status 1** rather than silently
degrading to the memory store and losing writes.

#### Schema migrations (`--migrate-only`)

Schema history lives in numbered migrations under
`GameServer/Persistence/Migrations/`, embedded into the binary. They are applied
transactionally, in order, exactly once, and the checksums of already-applied
migrations are verified on every run — an edited migration fails loudly instead
of letting environments drift apart.

The server applies pending migrations at boot. `--migrate-only` does just that
and exits, which is how CD migrates before restarting anything:

```bash
gameserver-dotnet --migrate-only --game-db-url "$GAME_DB_URL"
# exit 0 = applied or already current, 1 = failure, 2 = no DSN given
```

Adding a migration and the backward-compatibility rules are documented in
`backend/deploy/docs/DATABASE.md`.

### Run Tests

```bash
# Run all tests
dotnet test

# Run with detailed output
dotnet test --verbosity normal

# Run with test results file
dotnet test --logger "trx;LogFileName=test-results.trx"
```

### Docker Build

The Dockerfile expects `backend/` as the build context:

```bash
cd backend/
docker build -f deploy/docker/Dockerfile.gameserver-dotnet \
  -t rpg-mmo/gameserver-dotnet:dev .
```

### NativeAOT Publish (local)

```bash
# On Ubuntu/Debian, install prerequisites first:
sudo apt-get install -y clang zlib1g-dev

# On Alpine:
apk add clang build-base zlib-dev

# Publish
dotnet publish GameServer/GameServer.csproj -c Release -o ./publish
```

The resulting binary is a single self-contained executable (~30-45 MB), with no
dependency on the .NET runtime.

## Shared Logic Usage

### In .NET Server

The `GameServer` project references `Shared.GameLogic` directly via
`<ProjectReference>`. All movement, combat, and validation calls go through the
shared library.

### In Unity Client

Add `Shared.GameLogic` to the Unity project as a local package or source folder:

1. Copy or symlink `Shared.GameLogic/` into `Assets/Plugins/Shared.GameLogic/`
2. Create an Assembly Definition (`Shared.GameLogic.asmdef`) in that folder:
   ```json
   {
     "name": "Shared.GameLogic",
     "rootNamespace": "Shared.GameLogic",
     "references": [],
     "includePlatforms": [],
     "excludePlatforms": [],
     "allowUnsafeCode": true
   }
   ```
3. Reference the assembly from your DOTS systems:

```csharp
using Shared.GameLogic.Components;
using Shared.GameLogic.Systems;

public partial struct PlayerMovementSystem : ISystem
{
    public void OnUpdate(ref SystemState state)
    {
        // Same fixed timestep the server uses: dt = 1 / tickRate.
        float dt = MovementSystem.DeltaTimeForTickRate(GameConstants.DefaultTickRate);
        MapBounds bounds = MapBounds.Default;

        foreach (var (transform, input) in
            SystemAPI.Query<RefRW<LocalTransform>, RefRO<PlayerInput>>())
        {
            // Identical call the server makes -> prediction matches authority.
            var result = MovementSystem.ResolveDirection(
                input.ValueRO.MoveX, input.ValueRO.MoveY, out Vec2 direction);

            if (result is MoveResult.Accepted or MoveResult.Clamped)
            {
                var predicted = MovementSystem.Integrate(
                    ToVec2(transform.ValueRO.Position), direction,
                    input.ValueRO.Speed, dt, bounds);

                transform.ValueRW.Position = ToFloat3(predicted);
            }
        }
    }
}
```

**Important**: `Shared.GameLogic` must never reference Unity-specific assemblies.
If you need Unity math types, create thin adapter methods in the client project
that convert between `System.Numerics` and `Unity.Mathematics`.

### Detailed Unity Integration Guide

There are three ways to add `Shared.GameLogic` to a Unity 2022+ project:

#### Option A: Git Submodule (recommended for team workflows)

```bash
# From Unity project root
git submodule add https://github.com/dyCuong03/rpg-mmo-indie.git \
  Packages/com.rpgmmo.shared-gamelogic
```

Add to `Packages/manifest.json`:
```json
{
  "dependencies": {
    "com.rpgmmo.shared-gamelogic": "file:com.rpgmmo.shared-gamelogic/backend/gameserver-dotnet/Shared.GameLogic"
  }
}
```

The `Shared.GameLogic/` folder needs a `package.json` for UPM:
```json
{
  "name": "com.rpgmmo.shared-gamelogic",
  "version": "0.1.0",
  "displayName": "RPG MMO Shared Game Logic",
  "description": "Pure C# game logic shared between server and client",
  "unity": "2022.3"
}
```

And an Assembly Definition (`Shared.GameLogic.asmdef`):
```json
{
  "name": "Shared.GameLogic",
  "rootNamespace": "Shared.GameLogic",
  "references": [],
  "includePlatforms": [],
  "excludePlatforms": [],
  "allowUnsafeCode": true
}
```

#### Option B: Local Folder / Symlink

```bash
# Symlink into Assets (works on Linux/macOS; on Windows use mklink /D)
ln -s /path/to/backend/gameserver-dotnet/Shared.GameLogic \
  Assets/Plugins/Shared.GameLogic
```

Then create the `.asmdef` file as shown above. Reference from your DOTS
assemblies via the `references` array.

#### Option C: Copy (simplest, no auto-sync)

Copy `Shared.GameLogic/*.cs` files into `Assets/Plugins/Shared.GameLogic/`.
Add the `.asmdef` file. Manually sync when the server version updates.

#### Unity Type Adapters

`Shared.GameLogic` uses `System.Numerics.Vector2` for positions. Create a thin
adapter in the client project:

```csharp
using Unity.Mathematics;
using SysVec2 = System.Numerics.Vector2;

public static class MathAdapter
{
    public static float2 ToFloat2(this SysVec2 v) => new(v.X, v.Y);
    public static SysVec2 ToSysVec2(this float2 v) => new(v.x, v.y);
}
```

## Wire Protocol

The game server communicates over TCP using a length-prefixed JSON protocol,
identical to the Go game server:

```
+-------------------+--------------------+
| Length (4 bytes BE)| JSON payload       |
+-------------------+--------------------+
```

- **Length**: 4-byte big-endian unsigned integer, size of the JSON payload in bytes.
- **Payload**: UTF-8 JSON object with a `type` field and a `data` field.

### Envelope Format

```json
{
  "type": <integer>,
  "data": { ... }
}
```

### Message Types

| Type | Name               | Direction       | Description                          |
|------|--------------------|-----------------|--------------------------------------|
| 1    | `join`             | Client -> Server | Player join request (JWT token)      |
| 2    | `join_ack`         | Server -> Client | Join accepted (player ID, world state)|
| 3    | `input`            | Client -> Server | Player input (movement, actions)     |
| 4    | `snapshot`          | Server -> Client | World state snapshot (AOI-filtered)  |
| 5    | `attack`           | Client -> Server | Attack / skill use request           |
| 6    | `damage`           | Server -> Client | Damage event notification            |
| 7    | `death`            | Server -> Client | Entity death notification            |
| 8    | `spawn`            | Server -> Client | Entity spawn notification            |
| 9    | `despawn`          | Server -> Client | Entity despawn notification          |
| 10   | `disconnect`       | Either           | Graceful disconnect                  |
| 11   | `ping`             | Client -> Server | Latency measurement                  |
| 12   | `pong`             | Server -> Client | Latency measurement response         |

### Example: Join

```json
{"type": 1, "data": {"token": "eyJhbGciOiJIUzI1NiIs..."}}
```

### Example: Input

```json
{"type": 3, "data": {"tick": 1042, "move_x": 1.0, "move_y": 0.0, "attack_target_id": null}}
```

**`move_x` / `move_y` are a movement DIRECTION, not a displacement.** The server
integrates `direction * speed * dt` once per tick (`dt = 1 / tickRate`), then clamps
the result to the map bounds:

| Client sends           | Server does                                              |
|------------------------|----------------------------------------------------------|
| `(1, 0)`               | move right at full speed                                  |
| `(0.5, 0)`             | move right at half speed (analog stick)                   |
| `(1, 1)`               | normalized to `(0.707, 0.707)` — diagonals are not faster |
| `(1.2, 0)`             | normalized to `(1, 0)`                                    |
| `(5, 0)` / `NaN` / `∞` | rejected, logged at Debug, entity does not move           |
| `(0, 0)`               | no movement                                               |

Only the newest input per player is integrated each tick, so sending inputs faster
than the tick rate does not move the player further. Distance travelled depends only
on wall-clock time and the entity's `speed` stat (world units per second, default
5.0). `tick` is echoed back as the entity's `LastInputTick` for client reconciliation.

Map bounds default to 1000x1000 world units centered on the origin and are
configurable per server via `--map-width` / `--map-height`
(`GAMESERVER_MAP_WIDTH` / `GAMESERVER_MAP_HEIGHT`).

### Example: Snapshot

```json
{
  "type": 4,
  "data": {
    "tick": 1043,
    "entities": [
      {"id": "p_abc", "x": 10.5, "y": 20.3, "hp": 100, "state": "idle"},
      {"id": "p_def", "x": 12.0, "y": 19.8, "hp": 85, "state": "moving"}
    ]
  }
}
```

### JSON Field Naming

All JSON fields use `snake_case` to match the Go gateway convention. The C#
serializer is configured with `JsonNamingPolicy.SnakeCaseLower`.
