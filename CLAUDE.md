# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Indie RPG MMO game — mobile/PC client with server-authoritative multiplayer. Open-world maps + instanced dungeons. All notes, comments, and code in English.

## Architecture

### Two Communication Channels
- **Meta (HTTPS/WebSocket)**: Unity Client <-> Nakama — auth, economy, social, leaderboard, inventory
- **Realtime (UDP/KCP)**: Unity Client <-> Gateway <-> Game Servers — combat, movement, world state

### Server Stack
- **Nakama (Go)**: Meta services — authentication (device/email/social), economy + storage, leaderboard, party/chat/friends, notifications + presence, matchmaking queue
- **Gateway (Go, custom)**: UDP/KCP router, session manager, server registry, pub/sub events. JWT verified locally (shared secret, no Nakama roundtrip)
- **Game Servers (Go binaries, ~50MB RAM/pod)**: Map servers (combat/skill/movement at 10-15Hz tick) and Dungeon servers (instanced per party, 60s reconnect window)
- **Agones on k3s**: Game server lifecycle — allocation, health checks, scaling. k3s chosen over full K8s (~500MB vs 2GB+ control plane)
- **PostgreSQL**: 2 instances — meta (accounts, storage, leaderboard) and game state (persistent world state)
- **Redis**: Sessions (TTL), server registry, pub/sub, cache. Redis Streams (persistent with ACK) for cross-server events (boss_killed, rare_drop, inventory_changed, season_ended)

### Client Stack (Unity 2022 LTS+ with DOTS)
Four layers, top to bottom:
1. **GameObject World (Presentation)**: UI screens (uGUI/UI Toolkit — HUD, inventory, shop, leaderboard), VFX/audio/camera (GO pooling, Cinemachine), view models (reactive binding)
2. **Bridge Layer**: Event Bus (UI Command <-> ECS events), Presentation Sync (ECS transform/anim -> GO), View Pool Binder (entity <-> GO)
3. **DOTS World (Simulation ECS)**: Input systems, client prediction + reconciliation (rewind + replay), movement/combat/skill (mirror server logic), remote entity interpolation (2-3 snapshot buffer), spawn/despawn + SubScene baking + AOI culling
4. **Netcode Layer**: KCP/UDP transport (realtime gameplay), snapshot decoder (delta + jitter buffer), input sender (per tick), Nakama client (HTTPS/WS for auth/economy/social)

### Client Services (Standalone)
Auth/session, inventory/wallet cache, Addressables + asset streaming, IAP + receipt validation, telemetry/analytics, settings/local save, crash reporter

### Netcode Model
- **Simulation tick**: Fixed 10-15Hz, render at 60fps independent
- **Client prediction**: Apply input immediately, send to server
- **Server authoritative**: Validate anti-cheat, cooldown, range, speed
- **Reconciliation**: On server snapshot, rewind + replay if divergent
- **Remote entities**: Interpolation with 2-3 snapshot buffer
- **Bad network (mobile)**: Extrapolate (dead reckoning, ~200ms max), request full snapshot on lag spike
- **Disconnect**: Server holds entity 30s (60s in dungeon), client re-handshake with session token
- **Serialization**: Protobuf / FlatBuffers

## Key Design Patterns

- **Economy transactions**: Atomic (BEGIN TX -> check balance -> deduct + add -> COMMIT), idempotency_key guard, rate limiting at Nakama RPC
- **Gameplay rewards**: Server-authoritative — Map Server -> Nakama internal RPC (signed, no external network)
- **Cross-server events**: Redis Streams with consumer group ACK (not plain pub/sub)
- **Loot**: Server-side roll only
- **State persistence**: Async batch save every 30-60s (does not block tick loop)
- **Dungeon lifecycle**: Allocate instance -> save checkpoint -> transfer party -> gameplay -> loot/fail -> final save -> transfer back to origin map -> shutdown pod (5min idle reclaim)
- **Leaderboard**: Nakama sorted set (O(log N)), season reset with archive + reward distribution
- **Social**: Nakama built-in — Party API (max 4, open/invite), Friends, Chat channels (room/group/DM), Guild via Groups API, Presence via StatusFollow/Update

## Deployment Tiers (VPS + k3s, all open-source $0 license)

| Tier | Cost/mo | Setup | CCU |
|------|---------|-------|-----|
| Dev/Alpha | $40-60 | 1 VPS all-in-one, pg_dump daily | <200 |
| Beta | $80-150 | 2 VPS (app+DB), CDN, Grafana | 200-500 |
| Soft Launch | $200-400 | 3 VPS (Nakama+GW, game servers, DB), Redis dedicated | 500-2000 |
| Growth | $400-1000+ | Multi-node k3s, managed DB optional, Redis Sentinel | 2000-5000+ |

## Tech Stack Reference

| Component | Technology | License |
|-----------|-----------|---------|
| Game Backend | Nakama (Go) | Apache 2.0 |
| Game Servers | Custom Go binary | Self-owned |
| Orchestration | k3s + Agones | Apache 2.0 |
| Database | PostgreSQL | Free |
| Cache/PubSub | Redis | BSD |
| Client Engine | Unity 2022 LTS+ | - |
| Client ECS | Unity DOTS | - |
| Realtime Transport | KCP/UDP (custom Gateway) | - |
| Serialization | Protobuf / FlatBuffers | - |
| CDN | CloudFlare R2 / BunnyCDN | - |
| Monitoring | Grafana Cloud free + Prometheus | - |
| CI/CD | GitHub Actions free tier | - |
| Crash Reporting | Sentry / Firebase free tier | - |
| Auth | Nakama built-in (device -> email -> social) | - |

## Monitoring & Alerting
- Nakama Console (built-in admin UI), Grafana Cloud (CCU, match count, RPC latency)
- Prometheus export from Nakama + game servers, pg_stat_statements for query perf
- Redis INFO + MONITOR for cache hit rate
- Uptime Robot for external health, k3s kubectl top for pod resources
- Alerts: error rate -> Slack/Discord, high latency -> auto-scale, disk >80%, game server crash -> auto-restart (k3s)
