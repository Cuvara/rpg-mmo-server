# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Indie RPG MMO — mobile/PC Unity client with server-authoritative multiplayer. Open-world maps + instanced dungeons. This repo contains the backend (Go gateway + C# .NET 10 game server, shared game logic library). All notes, comments, and code in English.

## Commands

Go modules are separate (own `go.mod`, linked via `replace` directives to `../shared`). Always `cd` into the module directory first — there is no root go.work. The C# game server is a separate .NET 10 solution.

```bash
# === Go modules (gateway, shared, integration_test) ===
# Tests (per module; CI runs Go 1.26)
cd backend/shared      && go test ./... -race
cd backend/gateway     && go test ./... -race
cd backend/integration_test && go test -v -race -timeout 60s -tags integration   # E2E: gateway + C# gameserver

# Vet (CI enforces)
go vet ./...

# Build binaries
cd backend/gateway    && go build ./cmd/gateway/

# Run locally (two terminals)
cd backend/gateway    && go run ./cmd/gateway/ --addr=:8000

# === C# game server (.NET 10) ===
cd backend/gameserver-dotnet

# Build
dotnet build
dotnet build -c Release

# Test
dotnet test
dotnet test --verbosity normal

# Run server locally
dotnet run --project GameServer -- --addr=:9000 --map-id=map_01

# Publish NativeAOT binary
dotnet publish GameServer/GameServer.csproj -c Release -o ./publish

# Docker build (from backend/ directory)
cd backend/
docker build -f deploy/docker/Dockerfile.gameserver-dotnet -t rpg-mmo/gameserver-dotnet:dev .
```

CI: Go — `.github/workflows/ci.yml`: test shared → gateway → integration → build. Triggered on `backend/**` paths only.
CI: C# — `.github/workflows/ci-dotnet.yml`: build + test gameserver-dotnet. Triggered on `backend/gameserver-dotnet/**` paths.

## Repo Structure & Current State

The MVP is intentionally interface-driven: the default wiring is TCP + JSON + in-memory stores, but the production implementations (KCP, PostgreSQL, Redis, Agones allocation, Protobuf) are already written and selected by flag/env. Architecture rationale and known limitations: **`backend/docs/ARCHITECTURE-DECISIONS.md`** (read this before trusting any older diagram; `backend/docs/CORE_FLOW.md` predates the C# migration and is partly stale).

| Module | Path | Status | Contents |
|--------|------|--------|----------|
| shared | `backend/shared/` | ✅ | Foundation, no deps: config, constants (tick rates, TTLs, Redis keys), error codes, JWT (HS256), logger (slog), wire protocol (`messages/` — Envelope + codec), storage interfaces + in-memory impls |
| gameserver-dotnet | `backend/gameserver-dotnet/` | ✅ | C# .NET 10 game server (primary). `Shared.GameLogic/` (pure C# logic shared with Unity client), `GameServer/` (NativeAOT console app), `GameServer.Tests/` (xUnit). Wire-compatible with Go gateway |
| gateway | `backend/gateway/` | ✅ | TCP/KCP listener (auth + redirect, **not** a gameplay proxy), JWT auth + session manager (`session/`), server registry + Agones allocator (`registry/`), join-token/map transfer (`transfer/`), Redis Streams event relay (`events/`) |
| integration_test | `backend/integration_test/` | ✅ | E2E tests: gateway + C# gameserver interop (build tag `integration`) |
| nakama | `backend/nakama/` | Planned | Nakama Go plugins — auth, economy, leaderboard, social |
| deploy | `backend/deploy/` | Partial | Agones fleet manifests (`agones/fleet-map.yaml`, `fleet-dungeon.yaml`, autoscaler, allocation) |

Each module has its own `CLAUDE.md` with detailed role-specific instructions, plus `docs/` and `CHANGELOG.md`. Read the module CLAUDE.md before working in a module. `backend/TEAM.md` defines cross-module contracts and mandatory rules:

- **Changelog**: every change gets a `CHANGELOG.md` entry (Keep a Changelog format) in the touched module.
- **Docs**: update module `docs/` (README/API/DESIGN/RUNBOOK) before marking work complete.
- **Errors**: always wrap — `fmt.Errorf("context: %w", err)`.
- **Tests**: table-driven, `_test.go` alongside source.
- **Git**: branch `feat/<module>/<feature>`, conventional commits (`feat(gateway): ...`).

### Connection Flow (wire protocol in `shared/messages/`)

```
1. Client → Gateway:    MsgAuth { JWT }            (JWT verified locally, shared secret — no Nakama roundtrip)
2. Gateway → Client:    MsgAuthResp { OK, UserID }
3. Client → Gateway:    MsgEnterWorld { MapID }
4. Gateway → Client:    MsgEnterWorldResp { ServerAddr, JoinToken }
5. Client → GameServer: MsgJoinToken { Token }
6. GameServer → Client: MsgJoinTokenResp { OK }
7. Client → GameServer: MsgInput { MoveX, MoveY, AttackTargetID }  (per tick)
8. GameServer → Client: MsgSnapshot { Tick, AckTick, Full, Entities[], Removed[] }  (per tick)
9. Client → GameServer: MsgResync {}                               (optional: force a keyframe)
```

Snapshots are delta-encoded: a full keyframe on join / on `MsgResync` / every N
snapshots (default 30), deltas in between carrying only changed entities plus a
despawn list. `AckTick` is the newest client input tick the server accepted for that
player — the client's reconciliation anchor. Normative wire reference and client
merge algorithm: **`backend/gameserver-dotnet/docs/API.md`**.

### Extension Seams (MVP → Production)

| Layer | MVP (current) | Production |
|-------|---------------|------------|
| Transport | TCP (default) — KCP/UDP available opt-in via `shared/transport` + `--transport=kcp` | KCP everywhere, with per-session encryption |
| Encoding | **Protobuf + entity-type enum + entity-id interning** (`shared/proto/wire.proto`, one schema -> Go + C#), **81% smaller than the original JSON**; legacy JSON still accepted and distinguished by the first body byte - ADR-9 | Protobuf only, once no pre-Protobuf client remains |
| Player store | In-memory default; **PostgreSQL implemented** (C# `PostgresPlayerStore`, set `GAME_DB_URL`) | PostgreSQL everywhere |
| Session/Registry stores | In-memory default; **Redis implemented** (gateway `--backend=redis`) | Redis everywhere |
| Event stream | Go channels default; **Redis Streams implemented** (consumer group + ACK). C# side still publishes into a noop — ADR-5 | Redis Streams end to end |
| AOI | Brute-force | **Still brute-force.** A uniform spatial grid was built, proved correct against it, and measured **2.8x slower** at realistic density — the scan's cost is composing matches, not the distance tests. See `backend/docs/BENCHMARK.md` Part V before proposing it again |
| Orchestration | Manual | Agones on k3s (SDK already integrated in gameserver) |
| GameServer language | C# .NET 10 | C# .NET 10 with NativeAOT — shared game logic with Unity client via `Shared.GameLogic` |

## Target Architecture

### Two Communication Channels
- **Meta (HTTPS/WebSocket)**: Unity Client <-> Nakama — auth, economy, social, leaderboard, inventory
- **Realtime (TCP today, KCP/UDP opt-in)**: Unity Client <-> Gateway for auth + map assignment only, then Unity Client <-> Game Server **directly** for combat, movement and world state. The gateway is not in the gameplay data path (ADR-3)

### Server Stack
- **Nakama (Go)**: Meta services — authentication (device/email/social), economy + storage, leaderboard, party/chat/friends, notifications + presence, matchmaking queue
- **Gateway (Go, custom)**: Session manager, server registry, map assignment, event-stream relay. It is a **redirector, not a proxy** — it authenticates the client and hands back `{ServerAddr, JoinToken}`; gameplay traffic never flows through it (see `backend/docs/ARCHITECTURE-DECISIONS.md`, ADR-3)
- **Game Servers (C# .NET 10 NativeAOT, RAM/pod unbenchmarked — ADR-7)**: Map servers (combat/skill/movement at 10-15Hz tick, **one live server per `map_id`** — ADR-2) and Dungeon servers (instanced per party, 60s reconnect window). Shared game logic (`Shared.GameLogic`) used by both server and Unity client
- **Agones on k3s**: Game server lifecycle — allocation, health checks, scaling. k3s chosen over full K8s (~500MB vs 2GB+ control plane)
- **PostgreSQL**: 2 instances — meta (accounts, storage, leaderboard, owned by Nakama) and game state (`player_states`, written only by the game server)
- **Redis**: Sessions (TTL), server registry, and cross-server events via **Redis Streams with consumer-group ACK**. Not a cache and not evictable — `maxmemory-policy noeviction` (ADR-4). Raw pub/sub is not used anywhere (ADR-5)

### Netcode Model
- **Simulation tick**: Fixed 10-15Hz, render at 60fps independent
- **Server authoritative**: validate anti-cheat, cooldown, range, speed; client predicts + reconciles (rewind/replay)
- **Remote entities**: interpolation with 2-3 snapshot buffer; dead reckoning ~200ms max on bad mobile networks
- **Disconnect**: server holds entity 30s (60s in dungeon), client re-handshakes with session token

## Key Design Patterns

- **Economy transactions**: Atomic (BEGIN TX -> check balance -> deduct + add -> COMMIT), idempotency_key guard, rate limiting at Nakama RPC
- **Gameplay rewards**: Server-authoritative — Map Server -> Nakama internal RPC (signed, no external network)
- **Loot**: Server-side roll only
- **State persistence**: Async batch save every 30s (never blocks tick loop), plus a save when an entity leaves the world. Accepted crash-loss window is ≤30s of position/HP — anything of value (currency, items) must be written through Nakama transactionally at grant time, never left to the sweep (ADR-6)
- **Dungeon lifecycle** (⬜ target, not implemented): Allocate instance -> save checkpoint -> transfer party -> gameplay -> loot/fail -> final save -> transfer back to origin map -> shutdown pod. No checkpointing exists today; `--mode=dungeon` only changes the hold window
- **Leaderboard**: Nakama sorted set, season reset with archive + reward distribution
- **Social**: Nakama built-in — Party API (max 4), Friends, Chat channels, Guild via Groups API

## Deployment Tiers (VPS + k3s, all open-source $0 license)

> **⚠️ COST AND TIER CCU FIGURES ARE STILL ESTIMATES.** Costs below are planning
> figures and no VPS-hardware load test has been run. Do not size a launch on the
> tier rows. What *has* been measured is **bandwidth per client**, below; the
> per-server player ceiling is currently **unknown** and blocked on hardware.

**Measured per-game-server capacity** (`backend/docs/BENCHMARK.md`, 2026-08-07,
develop @ `cb31656`):

| Metric | Measured | Notes |
|--------|----------|-------|
| Downstream bandwidth | **45.9 KB/s per client at 200 players** | inside ADR-7's `< 50 KB/s` mobile threshold; ceiling is above 200 and not yet bracketed |
| Reproducibility of that | **0.3% across six runs** | bytes on the wire do not care what else the host is doing |
| Wire encoding | Protobuf + entity-type enum + id interning | **81% smaller than the original JSON** |
| RAM per game server | **~30 MiB idle → ~82 MiB at 200 players** | well inside the 128Mi pod limit |
| **Players per game server (tick)** | **UNKNOWN** | ⛔ see below — not measurable on the current hardware |

> ### ⛔ The per-server player ceiling is currently unknown
>
> An earlier revision of this table said **150**. That figure is **stale**: it was
> measured before Protobuf, the entity-type enum and id interning — three changes
> that removed 81% of the wire and with it the constraint that produced it. **Do
> not quote 150 as the current ceiling.**
>
> A replacement cannot be measured here. The load generator runs on the same
> machine as the server under test and uses *more CPU than it*, and tick p99 is
> highly sensitive to that: at a fixed level it read 67.4–70.8ms with the box
> quiet and 224.7–240.6ms with a deploy sharing it, a **3.3× swing**. So every
> tick figure from this host is a lower bound of unknown tightness.
>
> **The unblock is a separate machine for the load generator, and nothing else.**
> Tracked as a blocker in [ADR-7](backend/docs/ARCHITECTURE-DECISIONS.md).
>
> **What did change**: bandwidth was the binding constraint at roughly a third of
> the tick ceiling, and is not any more — tick binds now. That is why the missing
> number is a blocker rather than a curiosity.

| Tier | Cost/mo ⚠️ | Setup | CCU ⚠️ |
|------|---------|-------|-----|
| Dev/Alpha | $40-60 | 1 VPS all-in-one, pg_dump daily | <200 |
| Beta | $80-150 | 2 VPS (app+DB), CDN, Grafana | 200-500 |
| Soft Launch | $200-400 | 3 VPS (Nakama+GW, game servers, DB), Redis dedicated | 500-2000 |
| Growth | $400-1000+ | Multi-node k3s, managed DB optional, Redis Sentinel | 2000-5000+ |

The **"game servers implied" column has been removed.** It divided tier CCU by
150, so it propagated a stale ceiling into the one place someone sizing a fleet
would actually read. It can come back when there is a ceiling worth dividing by.

## Tech Stack Reference

| Component | Technology |
|-----------|-----------|
| Game Backend | Nakama (Go) |
| Game Servers | C# .NET 10 (NativeAOT) |
| Gateway | Custom Go binary |
| Orchestration | k3s + Agones |
| Database / Cache | PostgreSQL / Redis |
| Client | Unity 6 (6000.3.9f1) with DOTS |
| Realtime Transport | KCP/UDP (custom Gateway) |
| Serialization | Protobuf / FlatBuffers (target) |
| Monitoring | Grafana Cloud free + Prometheus |
| CI/CD | GitHub Actions |
