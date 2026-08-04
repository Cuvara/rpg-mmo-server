# Changelog

All notable changes to the GameServer .NET module will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [0.1.0] - 2026-08-04

### Added
- Initial C# port of Go game server
- `Shared.GameLogic` library with pure C# game logic (movement, combat, validation, AOI)
  - Designed for sharing between .NET server and Unity DOTS client
  - Zero Unity dependencies — standard .NET 9 class library
- `GameServer` .NET 9 console application
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
