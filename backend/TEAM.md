# Backend Agent Team

RPG MMO Indie — Backend Agent Team Definition.
Go modules (gateway, shared, nakama): Go 1.26. Game server: C# .NET 10.
All modules share: Protobuf/FlatBuffers serialization, PostgreSQL + Redis.

## Team Roster

| Role | Module | Owner Agent | Primary Responsibility |
|------|--------|-------------|----------------------|
| **Shared Architect** | `shared/` | `agent-shared` | Proto definitions, common types, DB models, Redis wrappers, error codes |
| **Nakama Engineer** | `nakama/` | `agent-nakama` | Auth, economy, leaderboard, social, matchmaking — Nakama Go plugins |
| **Gateway Engineer** | `gateway/` | `agent-gateway` | UDP/KCP router, session manager, server registry, JWT verify, pub/sub |
| **GameServer Engineer (C#)** | `gameserver-dotnet/` | `agent-gameserver-dotnet` | C# .NET 10 game server: tick loop, combat/skill, AI/NPC, loot. `Shared.GameLogic` shared with Unity client |
| **DevOps Engineer** | `deploy/` | `agent-devops` | k3s + Agones manifests, Docker, CI/CD, monitoring, DB migrations |

## Dependency Order

```
shared (foundation — no deps, Go)
  ├── nakama (depends on shared, Go)
  └── gateway (depends on shared, Go)
gameserver-dotnet (standalone C# .NET 10 — wire-compatible with gateway)
  └── Shared.GameLogic (pure C# — shared with Unity client)
deploy (depends on all above — build artifacts)
```

## Cross-Team Contracts

### Communication Channels
- **Nakama <-> Gateway**: JWT shared secret for local verification (no roundtrip)
- **Nakama <-> GameServer**: Internal RPC (signed) for reward granting
- **Gateway (Go) <-> GameServer (C# .NET 10)**: TCP wire protocol (4-byte BE length prefix + JSON, `snake_case` fields). Gateway forwards opaquely — cannot distinguish Go vs C# backends. Join token handoff via HS256 JWT with shared secret
- **Gateway <-> Redis**: Session store (TTL), server registry
- **GameServer <-> Redis**: Heartbeat, pub/sub events (Redis Streams with ACK)
- **GameServer <-> PostgreSQL**: Async batch save (30-60s), checkpoint on transfer

### Shared Definitions (owned by agent-shared, Go)
- Protobuf message types (input, snapshot, events)
- Error codes and status enums
- DB schema models (sqlc or raw)
- Redis key patterns and TTL constants
- Config structs and env loading

### Shared Game Logic (owned by agent-gameserver-dotnet, C#)
`gameserver-dotnet/Shared.GameLogic/` is a pure C# .NET 10 class library with zero Unity dependencies. It contains all deterministic game logic (movement, combat, validation, AOI, constants) and is designed to be used by both:
- **GameServer (.NET 10)**: referenced via `<ProjectReference>`
- **Unity DOTS client**: imported as a local package / Git submodule with an Assembly Definition (`.asmdef`)

Constraints: no Unity refs, no server-specific code (networking/persistence/logging), no allocations in hot paths, NativeAOT compatible (no reflection).

## Mandatory: Documentation & Changelog

**Every agent MUST follow these rules:**

### 1. Documentation (per module)
Each module maintains `docs/` directory:
```
module/
  docs/
    README.md          — Module overview, architecture, how to run
    API.md             — RPC/endpoint/message reference
    DESIGN.md          — Design decisions and trade-offs
    RUNBOOK.md         — Operational guide (deploy, debug, rollback)
```

Rules:
- Update docs BEFORE marking any task as completed
- New public function/RPC = new entry in API.md
- Architecture change = update DESIGN.md with date and rationale
- All docs in English

### 2. Changelog (per module)
Each module maintains `CHANGELOG.md` using [Keep a Changelog](https://keepachangelog.com/) format:

```markdown
# Changelog

All notable changes to this module will be documented in this file.

## [Unreleased]

### Added
- New feature description

### Changed
- Existing feature modification

### Fixed
- Bug fix description

### Removed
- Removed feature description
```

Rules:
- Every PR/commit MUST have a changelog entry
- Use semantic sections: Added, Changed, Fixed, Removed, Security, Deprecated
- Include ticket/issue reference when available
- Date format: YYYY-MM-DD when releasing

### 3. Code Documentation
- All exported functions: GoDoc comment
- Complex logic: inline comments explaining WHY (not what)
- Package-level doc.go for each package

## Development Standards

### Go Agents (gateway, shared, nakama, legacy gameserver)
- Language: Go 1.26
- Error handling: `fmt.Errorf("context: %w", err)` — always wrap
- Logging: structured (zerolog or slog)
- Config: env vars with defaults, validated at startup
- Testing: table-driven tests, `_test.go` alongside source
- Linting: `golangci-lint` with shared config

### C# Agent (gameserver-dotnet)
- Language: C# / .NET 10, NativeAOT compatible
- Serialization: `System.Text.Json` with source generators (no reflection)
- Logging: `Microsoft.Extensions.Logging` (structured)
- Testing: xUnit, tests in `GameServer.Tests/`
- No allocations in hot paths (tick loop, snapshot, input processing)
- All public APIs: XML doc comments

### Naming Conventions
- Package names: lowercase, single word when possible
- Proto files: `snake_case.proto`
- Redis keys: `prefix:entity:{id}:field` (e.g., `session:{user_id}`)
- DB tables: `snake_case`, plural (e.g., `player_states`)

### Git Workflow
- Branch: `feat/<module>/<feature>`, `fix/<module>/<issue>`
- Commit: conventional commits (`feat(gateway): add session manager`)
- PR: must include changelog + doc updates
