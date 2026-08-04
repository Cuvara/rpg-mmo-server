# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Indie RPG MMO — mobile/PC Unity client with server-authoritative multiplayer. Open-world maps + instanced dungeons. This repo contains the Go backend. All notes, comments, and code in English.

## Commands

Each backend module is a separate Go module (own `go.mod`, linked via `replace` directives to `../shared`). Always `cd` into the module directory first — there is no root go.work.

```bash
# Tests (per module; CI runs Go 1.26)
cd backend/shared      && go test ./... -race
cd backend/gameserver  && go test ./... -race
cd backend/gateway     && go test ./... -race
cd backend/integration_test && go test -v -race -timeout 60s   # E2E: real gateway + gameserver in-process

# Single test
cd backend/gameserver && go test ./server/ -run TestTickLoop -race -v

# Vet (CI enforces)
go vet ./...

# Build binaries
cd backend/gameserver && go build ./cmd/gameserver/
cd backend/gateway    && go build ./cmd/gateway/

# Run locally (two terminals)
cd backend/gameserver && go run ./cmd/gameserver/ --addr=:9000 --map-id=map_01
cd backend/gateway    && go run ./cmd/gateway/ --addr=:8000
```

CI (`.github/workflows/ci.yml`): test shared → gameserver + gateway (parallel) → integration → build. Triggered on `backend/**` paths only.

## Repo Structure & Current State

The MVP is intentionally interface-driven: current implementations are TCP + JSON + in-memory stores; production swaps (KCP, Protobuf, PostgreSQL/Redis, Agones) plug in behind the same interfaces with zero business-logic changes.

| Module | Path | Status | Contents |
|--------|------|--------|----------|
| shared | `backend/shared/` | ✅ | Foundation, no deps: config, constants (tick rates, TTLs, Redis keys), error codes, JWT (HS256), logger (slog), wire protocol (`messages/` — Envelope + codec), storage interfaces + in-memory impls |
| gameserver | `backend/gameserver/` | ✅ | Tick loop (`server/tick.go`), world/entities (`game/`), input validation + anti-cheat (`input/`), combat (`combat/`), AOI snapshots (`snapshot/`), async batch persistence (`persistence/`), event publisher (`events/`), Agones SDK integration (`agones/`) |
| gateway | `backend/gateway/` | ✅ | TCP router, JWT auth + session manager (`session/`), server registry + allocator (`registry/`), join-token/map transfer (`transfer/`), event relay stub (`events/`) |
| integration_test | `backend/integration_test/` | ✅ | E2E tests spinning up gateway + gameserver together |
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
8. GameServer → Client: MsgSnapshot { Tick, Entities[] }           (per tick)
```

### Extension Seams (MVP → Production)

| Layer | MVP (current) | Production |
|-------|---------------|------------|
| Transport | TCP | KCP (`xtaci/kcp-go`) |
| Encoding | JSON structs | Protobuf |
| Player/Session/Registry stores | In-memory | PostgreSQL (`pgx`) / Redis |
| Event stream | Go channels | Redis Streams (consumer group ACK) |
| AOI | Brute-force | Spatial grid / quadtree |
| Orchestration | Manual | Agones on k3s (SDK already integrated in gameserver) |

## Target Architecture

### Two Communication Channels
- **Meta (HTTPS/WebSocket)**: Unity Client <-> Nakama — auth, economy, social, leaderboard, inventory
- **Realtime (UDP/KCP)**: Unity Client <-> Gateway <-> Game Servers — combat, movement, world state

### Server Stack
- **Nakama (Go)**: Meta services — authentication (device/email/social), economy + storage, leaderboard, party/chat/friends, notifications + presence, matchmaking queue
- **Gateway (Go, custom)**: UDP/KCP router, session manager, server registry, pub/sub events
- **Game Servers (Go binaries, ~50MB RAM/pod)**: Map servers (combat/skill/movement at 10-15Hz tick) and Dungeon servers (instanced per party, 60s reconnect window)
- **Agones on k3s**: Game server lifecycle — allocation, health checks, scaling. k3s chosen over full K8s (~500MB vs 2GB+ control plane)
- **PostgreSQL**: 2 instances — meta (accounts, storage, leaderboard) and game state
- **Redis**: Sessions (TTL), server registry, pub/sub, cache. Redis Streams (persistent with ACK) for cross-server events

### Netcode Model
- **Simulation tick**: Fixed 10-15Hz, render at 60fps independent
- **Server authoritative**: validate anti-cheat, cooldown, range, speed; client predicts + reconciles (rewind/replay)
- **Remote entities**: interpolation with 2-3 snapshot buffer; dead reckoning ~200ms max on bad mobile networks
- **Disconnect**: server holds entity 30s (60s in dungeon), client re-handshakes with session token

## Key Design Patterns

- **Economy transactions**: Atomic (BEGIN TX -> check balance -> deduct + add -> COMMIT), idempotency_key guard, rate limiting at Nakama RPC
- **Gameplay rewards**: Server-authoritative — Map Server -> Nakama internal RPC (signed, no external network)
- **Loot**: Server-side roll only
- **State persistence**: Async batch save every 30-60s (never blocks tick loop)
- **Dungeon lifecycle**: Allocate instance -> save checkpoint -> transfer party -> gameplay -> loot/fail -> final save -> transfer back to origin map -> shutdown pod (5min idle reclaim)
- **Leaderboard**: Nakama sorted set, season reset with archive + reward distribution
- **Social**: Nakama built-in — Party API (max 4), Friends, Chat channels, Guild via Groups API

## Deployment Tiers (VPS + k3s, all open-source $0 license)

| Tier | Cost/mo | Setup | CCU |
|------|---------|-------|-----|
| Dev/Alpha | $40-60 | 1 VPS all-in-one, pg_dump daily | <200 |
| Beta | $80-150 | 2 VPS (app+DB), CDN, Grafana | 200-500 |
| Soft Launch | $200-400 | 3 VPS (Nakama+GW, game servers, DB), Redis dedicated | 500-2000 |
| Growth | $400-1000+ | Multi-node k3s, managed DB optional, Redis Sentinel | 2000-5000+ |

## Tech Stack Reference

| Component | Technology |
|-----------|-----------|
| Game Backend | Nakama (Go) |
| Game Servers / Gateway | Custom Go binaries |
| Orchestration | k3s + Agones |
| Database / Cache | PostgreSQL / Redis |
| Client | Unity 2022 LTS+ with DOTS |
| Realtime Transport | KCP/UDP (custom Gateway) |
| Serialization | Protobuf / FlatBuffers (target) |
| Monitoring | Grafana Cloud free + Prometheus |
| CI/CD | GitHub Actions |
