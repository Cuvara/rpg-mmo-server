# Backend Agent Team

RPG MMO Indie — Backend Agent Team Definition.
All agents share: Go 1.26, Protobuf/FlatBuffers serialization, PostgreSQL + Redis.

## Team Roster

| Role | Module | Owner Agent | Primary Responsibility |
|------|--------|-------------|----------------------|
| **Shared Architect** | `shared/` | `agent-shared` | Proto definitions, common types, DB models, Redis wrappers, error codes |
| **Nakama Engineer** | `nakama/` | `agent-nakama` | Auth, economy, leaderboard, social, matchmaking — Nakama Go plugins |
| **Gateway Engineer** | `gateway/` | `agent-gateway` | UDP/KCP router, session manager, server registry, JWT verify, pub/sub |
| **GameServer Engineer** | `gameserver/` | `agent-gameserver` | Map server tick loop, dungeon instances, combat/skill, AI/NPC, loot |
| **DevOps Engineer** | `deploy/` | `agent-devops` | k3s + Agones manifests, Docker, CI/CD, monitoring, DB migrations |

## Dependency Order

```
shared (foundation — no deps)
  ├── nakama (depends on shared)
  ├── gateway (depends on shared)
  └── gameserver (depends on shared)
deploy (depends on all above — build artifacts)
```

## Cross-Team Contracts

### Communication Channels
- **Nakama <-> Gateway**: JWT shared secret for local verification (no roundtrip)
- **Nakama <-> GameServer**: Internal RPC (signed) for reward granting
- **Gateway <-> GameServer**: UDP/KCP forwarding, join_token handoff
- **Gateway <-> Redis**: Session store (TTL), server registry
- **GameServer <-> Redis**: Heartbeat, pub/sub events (Redis Streams with ACK)
- **GameServer <-> PostgreSQL**: Async batch save (30-60s), checkpoint on transfer

### Shared Definitions (owned by agent-shared)
- Protobuf message types (input, snapshot, events)
- Error codes and status enums
- DB schema models (sqlc or raw)
- Redis key patterns and TTL constants
- Config structs and env loading

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

### All Agents
- Language: Go 1.26
- Error handling: `fmt.Errorf("context: %w", err)` — always wrap
- Logging: structured (zerolog or slog)
- Config: env vars with defaults, validated at startup
- Testing: table-driven tests, `_test.go` alongside source
- Linting: `golangci-lint` with shared config

### Naming Conventions
- Package names: lowercase, single word when possible
- Proto files: `snake_case.proto`
- Redis keys: `prefix:entity:{id}:field` (e.g., `session:{user_id}`)
- DB tables: `snake_case`, plural (e.g., `player_states`)

### Git Workflow
- Branch: `feat/<module>/<feature>`, `fix/<module>/<issue>`
- Commit: conventional commits (`feat(gateway): add session manager`)
- PR: must include changelog + doc updates
