# GameServer .NET Module — Agent Instructions

**Role**: GameServer .NET Engineer
**Module**: `backend/gameserver-dotnet`
**Language**: C# / .NET 10

## Structure

- `Shared.GameLogic/` — Pure C# game logic library (shared with Unity client)
- `GameServer/` — Server application (.NET 10 console, NativeAOT)
- `GameServer.Tests/` — xUnit test suite
- `docs/` — Module documentation (README, DESIGN)

## Commands

```bash
# Build entire solution
dotnet build

# Build release
dotnet build -c Release

# Run tests
dotnet test

# Run tests with output
dotnet test --verbosity normal

# Run server locally
dotnet run --project GameServer -- --addr=:9000 --map-id=map_01

# Publish NativeAOT binary
dotnet publish GameServer/GameServer.csproj -c Release -o ./publish

# Docker build (from backend/ directory)
docker build -f deploy/docker/Dockerfile.gameserver-dotnet -t rpg-mmo/gameserver-dotnet:dev .
```

## Key Constraints

- Wire protocol MUST match Go gateway exactly (4-byte BE length + JSON, snake_case fields)
- `Shared.GameLogic` MUST have zero Unity dependencies (standard .NET 10 class library)
- `Shared.GameLogic` MUST NOT contain server-specific code (no networking, persistence, logging)
- Tick loop MUST NOT do synchronous I/O (persistence is async background task)
- No allocations in hot paths (tick loop, snapshot broadcast, input processing)
- All public APIs need XML doc comments
- NativeAOT compatible — no reflection-based serialization, no dynamic assembly loading
- System.Text.Json with source generators for AOT-safe serialization
- All comments, notes, and code in English

## Testing

- Unit tests for shared logic (movement, combat, validation, AOI)
- Unit tests for server systems (world state, connection handling, protocol)
- Integration tests for full tick loop + client simulation
- All tests must pass before merge (CI enforces this)

## Dependencies

- `System.Text.Json` — JSON serialization (AOT-compatible with source generators)
- `Microsoft.Extensions.Logging` — Structured logging
- `xunit` — Test framework
- No other external dependencies (keep the dependency tree minimal)
