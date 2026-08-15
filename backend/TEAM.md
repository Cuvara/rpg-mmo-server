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
"com.rpgmmo.shared-gamelogic": "https://github.com/Cuvara/rpg-mmo-server.git?path=/backend/gameserver-dotnet/Shared.GameLogic#sgl-v0.1.0"
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

### State the expected value and its units before you run the measurement

**Write down what the number should be, and in what units, before you look at what it is.**
Then compare. If you cannot say what to expect, you are not yet in a position to interpret
the result — and that is the finding, not an obstacle to it.

This is not pedantry about rigour. Every measurement mistake this team has made so far had
the same shape: **the number that came back was plausible, so nobody checked it against a
number that had been written down first.** A wrong-looking result gets investigated. A
plausible-looking one gets used, quoted, and built on, and it is still wrong.

Four instances, all real, all from a single week:

| The number | Why it looked right | What it actually was |
|---|---|---|
| A 0.333-unit step read as a **20x jump** | compared against the base tick step | positions came from **snapshots**, which ship at the *world* rate — one sample already contains `WorldEvery` base steps, so 0.333 was a perfectly normal frame |
| `effective speed 5` read as **evidence snapshots had arrived** | the value was correct | it was the **configured constant being echoed back**; it would have read 5 with no snapshot ever received |
| A burstiness figure quoted as a **baseline** | it was measured, not guessed | the metric ranges **33–45 across runs**; a baseline of one sample is not a baseline |
| A predictor reading **0.133 s** between sends | 15 Hz sends, and 0.133 is a real interval | the predictor's clock ran at **2x** — the true gap was 0.0669 s, and 0.133 is exactly what a doubled clock produces |
| A send rate of **7.5 Hz** against a configured 15 | it *disagreed* with the prediction, which is what this rule tells you to look for — so it was investigated and treated as a second, independent defect | **the same 2x clock**, read through an instrument measured in the predictor's own elapsed time. 0.138 s on a doubled clock is ~0.069 s real: the sender had been delivering ~15 Hz all along, and there was no second defect |

Note what these have in common: none produced an implausible value. Three produced values
that were *arithmetically consistent with a wrong model*, which is the hardest kind to catch
after the fact and the easiest to catch before, because the prediction and the model are
written down together.

**The last row is the failure mode that survives this rule, so read it twice.** There the
rule was followed: the expected value was written down (15 Hz), the measurement disagreed,
and the mismatch was investigated. It still produced a wrong conclusion — a second
independent defect that did not exist — because **the discrepancy was in the measuring
device, not in the thing being measured**, and both readings came from the same broken
clock. The arithmetic was consistent with two wrong models at once, and the one chosen was
the one that flattered a change already made.

So: **when a number disagrees with your prediction, the instrument is a candidate, not just
the subject.** Before concluding that the system is wrong, ask what the number was measured
*with*, whether that instrument shares any state with the thing under suspicion, and whether
an independent clock or counter says the same. In the row above, one `Stopwatch` outside the
predictor would have settled it — and the reading that eventually did settle it was taken
after the "fix", so it could not attribute anything to it either way.

In practice this costs one line in the test or the report:

```csharp
// 15 sends/s over 1.2s at speed 5 = 9 units, sampled at the WORLD rate (15Hz),
// so a normal sample interval is speed/15 = 0.333 units.
float normal = speed / rates.WorldHz;
```

Three habits that follow from it:

- **Name the units in the variable or the comment**, not just in your head. `normal` versus
  `normalPerBaseTick` versus `normalPerSnapshot` is the whole of the first row above.
- **Say which rate a per-something figure is per.** This codebase now has three simulation
  rates and a separate replication rate; "per tick" is ambiguous and has already been wrong
  twice.
- **One sample is not a baseline.** If a metric's spread has not been measured, measure it
  before quoting a value from it — `BENCHMARK.md` reports run-to-run spread beside every
  figure for exactly this reason.

### Movement-adjacent behaviour must be tested through the live path

**A test that builds its own world can be true while the path a client takes is not.**
Anything that changes how far, how fast or how often an entity moves needs at least one
test that goes through the socket: join over the transport, send input as a client does,
and read the position back **out of the snapshot stream**. That last part is the point —
it is the number a client actually sees, rather than a number read out of the world by a
test that also put it there.

The pattern to copy is `GameServer.Tests/Server/SlowClientMovementTests.cs`.

This rule was bought, not theorised. A run of defects in movement and tick-rate handling
shared one shape: a description and an implementation disagreed, and **every existing test
could pass while the live behaviour was wrong**, because the unit tests pushed inputs
straight into the queue and so skipped the join handshake, the network thread, the entity's
real creation path, per-tick input coalescing and the encoder. The defect that finally
surfaced this — bursty input arrival losing most of a player's travel (#100) — was invisible
to a full green suite and had been live in every configuration, including the one running on
staging, for as long as coalescing had existed.

Specifically, a unit test cannot see:

- **per-tick input coalescing** — several packets arriving together become one step, which
  a test that pushes one input per tick never produces;
- **arrival pattern** — TCP batching, a client GC pause or a mobile radio waking up clump
  packets that a `Task.Delay` loop spaces evenly;
- **the entity's real archetype** — a hand-added entity may carry different components from
  one created by the join handler, and a query that filters on a tag will silently match
  neither;
- **what the client is actually told** — the snapshot is delta-encoded, so a value being
  correct in the world does not mean it reached anyone.

Keep the unit tests: they are faster, they localise a failure, and they express intent. Add
the live-path one so that when the two disagree, there is something that can settle it.

### Naming Conventions
- Package names: lowercase, single word when possible
- Proto files: `snake_case.proto`
- Redis keys: `prefix:entity:{id}:field` (e.g., `session:{user_id}`)
- DB tables: `snake_case`, plural (e.g., `player_states`)

### Git Workflow
- Branch: `feat/<module>/<feature>`, `fix/<module>/<issue>`
- Commit: conventional commits (`feat(gateway): add session manager`)
- PR: must include changelog + doc updates
