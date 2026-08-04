# GameServer .NET

C# port of the Go game server. Shares game logic with Unity DOTS client via `Shared.GameLogic`.

## Architecture

The solution contains three projects:

```
gameserver-dotnet/
  Shared.GameLogic/      Pure C# game logic library (shared with Unity client)
  GameServer/            Server application (.NET 9 console, NativeAOT)
  GameServer.Tests/      xUnit test suite
```

### Shared.GameLogic

A standard .NET 9 class library with **zero Unity dependencies**. Contains all
deterministic game logic: movement validation, combat calculations, cooldown
checks, AOI (Area of Interest) queries, and game constants.

This library is designed to be imported by the Unity DOTS client as a local
package or Git submodule, enabling client-side prediction with identical code
paths on both server and client.

### GameServer

A .NET 9 console application that hosts the authoritative game world. It speaks
the same wire protocol as the Go game server, so the Go gateway cannot
distinguish between Go and C# backends.

### GameServer.Tests

xUnit-based test suite covering shared logic, server systems, protocol encoding,
and integration scenarios.

## Quick Start

### Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
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
  --jwt-secret=dev-secret-change-me \
  --agones \
  --redis=localhost:6379
```

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
using Shared.GameLogic;

public partial struct PlayerMovementSystem : ISystem
{
    public void OnUpdate(ref SystemState state)
    {
        foreach (var (transform, input) in
            SystemAPI.Query<RefRW<LocalTransform>, RefRO<PlayerInput>>())
        {
            var newPos = MovementLogic.ApplyInput(
                transform.ValueRO.Position,
                input.ValueRO.Direction,
                input.ValueRO.DeltaTime);

            if (MovementLogic.ValidateSpeed(
                    transform.ValueRO.Position, newPos, input.ValueRO.DeltaTime))
            {
                transform.ValueRW.Position = newPos;
            }
        }
    }
}
```

**Important**: `Shared.GameLogic` must never reference Unity-specific assemblies.
If you need Unity math types, create thin adapter methods in the client project
that convert between `System.Numerics` and `Unity.Mathematics`.

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
{"type": 3, "data": {"tick": 1042, "dx": 1.0, "dy": 0.0, "seq": 523}}
```

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
