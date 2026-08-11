# Backend Agent Team

RPG MMO Indie — Backend Agent Team Definition.
Go modules (gateway, shared, nakama): Go 1.26. Game server: C# .NET 10.
All modules share: Protobuf/FlatBuffers serialization, PostgreSQL + Redis.

## Team Roster

| Role | Module | Owner Agent | Primary Responsibility |
|------|--------|-------------|----------------------|
| **Shared Architect** | `shared/` | `agent-shared` | Proto definitions, common types, DB models, Redis wrappers, error codes |
| **Nakama Engineer** | `nakama/` | `agent-nakama` | Auth, economy, leaderboard, social, matchmaking — Nakama Go plugins |
| **Gateway Engineer** | `gateway/` | `agent-gateway` | TCP/KCP listener (auth + redirect, not a gameplay proxy), session manager, server registry, JWT verify, event-stream relay |
| **GameServer Engineer (C#)** | `gameserver-dotnet/` | `agent-gameserver-dotnet` | C# .NET 10 game server: tick loop, combat/skill, AI/NPC, loot. `Shared.GameLogic` shared with Unity client |
| **DevOps Engineer** | `deploy/` | `agent-devops` | k3s + Agones manifests, Docker, CI/CD, monitoring, DB migrations |

## Current phase: core plumbing only — no gameplay content

**Directive, 2026-08-11.** The goal right now is to get every *flow* end to end
and testable: handshake, wire codec, tick loop, snapshot merge, prediction
scaffolding, the shared-logic package boundary, CI gates. Gameplay is
deliberately **not** being built yet — no skills, no items, no loot tables, no AI
behaviours, no dungeon content, no balance work.

The reason is sequencing, not disinterest: gameplay written before the flows are
proven has to be rewritten when a flow changes, and it makes every failure
ambiguous between "the rule is wrong" and "the plumbing is wrong". Keep the
simulation surface as thin as it takes to exercise a flow honestly — a movement
rule and a damage rule are enough to prove prediction and reconciliation work.

If a task seems to require inventing a gameplay rule to proceed, that is a signal
to stop and ask, not to invent one.

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
- **Gateway (Go) <-> GameServer (C# .NET 10)**: no runtime connection. The gateway never talks to a game server; it issues a join token and the *client* dials the server directly (ADR-3). Both speak the same wire protocol (4-byte BE length prefix + JSON, `snake_case`), joined by an HS256 join token whose `sid` names the target server
- **Gateway <-> Redis**: Session store (TTL), server registry, event-stream consumer
- **GameServer <-> Redis**: ⬜ **not implemented** — the C# server has no Redis client. It cannot self-register or heartbeat (a deploy script does it) and its events go to a noop stream (ADR-1, ADR-5)
- **GameServer <-> PostgreSQL**: Async batch save every 30s + save on entity removal. No checkpoint-on-transfer yet (ADR-6)

### Shared Definitions (owned by agent-shared, Go)
- Protobuf message types (input, snapshot, events)
- Error codes and status enums
- DB schema models (sqlc or raw)
- Redis key patterns and TTL constants
- Config structs and env loading

### Shared Game Logic (owned by agent-gameserver-dotnet, C#)
`gameserver-dotnet/Shared.GameLogic/` is a pure C# class library with zero engine dependencies. It contains all deterministic game logic (movement, combat, validation, AOI, constants) and is used by both:
- **GameServer (.NET 10)**: referenced via `<ProjectReference>`
- **Unity 6 client**: consumed as **source** via a UPM git dependency with a `?path=` subfolder reference, pinned to a tag

Constraints: no Unity refs, **no ECS refs (`Arch.Core` included)**, no server-specific code (networking/persistence/logging), no allocations in hot paths, NativeAOT compatible (no reflection).

> **This is a two-repo contract — ADR-10.** The client repo compiles this exact
> source. Changing a signature here changes the client's build; changing a
> *behaviour* here changes what the client predicts. Neither is a local edit.

**How the client consumes it — use this exact form, do not invent another:**

```json
"com.rpgmmo.shared-gamelogic": "https://github.com/dyCuong03/rpg-mmo-server.git?path=/backend/gameserver-dotnet/Shared.GameLogic#sgl-v0.1.0"
```

- **Tag, never branch.** A branch ref changes what the client predicts whenever
  someone pushes, with nothing in the client repo to attribute it to.
- Tags are `sgl-vX.Y.Z`, no `/` in the name.
- `package.json`'s `version` is bumped in the same commit that gets tagged.
  Otherwise the client installs `sgl-v0.2.0` and gets a package reporting `0.1.0`,
  which UPM will not warn about.
- **Tagging is a release action and belongs to the lead.** Do not create one.
- No `.tgz`, no NuGet, no registry. UPM does not consume tarball URLs, and the
  client must compile *source* (Unity 6 is C# 9).

**Rules that are not negotiable at review time** (see ADR-10 for the reasoning):

| Rule | Consequence of breaking it |
|---|---|
| Target `netstandard2.1;net10.0` — never `net10.0` alone | Unity cannot reference the assembly at all |
| No ECS type in any signature | Couples the client to a server storage choice, and to a pre-1.0 dependency |
| Float ops limited to `+ - * /`, comparison, `MathF.Min/Max/Abs/Sqrt` | Transcendentals are implementation-defined; server (NativeAOT x64) and client (IL2CPP ARM64) stop agreeing |
| Entity identity is an integer handle, not a `string` | A managed reference in an ECS component is a pointer per chunk, and is prohibited under Burst |
| No allocation on a per-tick path — fill caller-provided `Span<T>` | The cost the ECS migration exists to remove |

**Golden vectors are the conformance mechanism.** Fixtures of
`(state, input, dt) → expected state` are committed alongside the logic and run
by the server's xUnit suite *and* the client's Unity Test Runner. A behavioural
change is expected to update the vectors in the same commit; an *unintended* one
fails CI on whichever side moved. Shared code without these is a shared file,
not shared behaviour.

### Server ECS (owned by agent-gameserver-dotnet, C#)
The game server's entity storage is [Arch](https://github.com/genaray/Arch),
replacing the hand-rolled `GameWorld` (ADR-10). Arch owns entity identity,
component storage, queries and iteration; it does **not** own the rules. Arch
systems iterate and call into `Shared.GameLogic`. Arch stays server-side and
never reaches the client.

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
