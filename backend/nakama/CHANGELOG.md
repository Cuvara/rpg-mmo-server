# Changelog — Nakama Module

All notable changes to the Nakama module will be documented in this file.
Format based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Added
- **`reward_kills` RPC — gold and leaderboard score for a batch of kills in one call**
  (rpg-mmo-server#233). The per-kill `reward_kill` + `submit_kill` pair cost 2 HTTP
  requests and 2 separate meta-DB transactions per mob kill — ~133 commits/s at 200
  players on the grindy end, the first thing to saturate a shared small-VPS Postgres.
  Both operations are increments, so one call per killer per game-server flush is
  semantically identical. The error contract is explicit and load-bearing: an error
  means NOTHING was granted (safe to re-queue); once the wallet update succeeds the
  call always reports success, surfacing a leaderboard failure in the response body
  instead — an error there would invite a retry that grants the gold twice (ADR-6:
  bounded score loss acceptable, double gold not). `kills` capped at 1000 per batch so
  a corrupted payload cannot mint unbounded gold; `batch_id` recorded in wallet
  metadata as the audit trail and future idempotency slot. Unit tests drive the core
  through a two-method mock: single-call grant, all reject-before-grant shapes, and
  both halves of the error contract.

- **RUNBOOK: two silent leaderboard failure modes**, both found in one live
  investigation where "the leaderboard is broken" was two deploy gaps and zero code
  bugs: a stale `nakama.so` (module mtime predated the commit registering
  `get_leaderboard`/`submit_kill`, so Nakama answered `RPC function not found` /
  `Leaderboard not found` while every older RPC worked), and a game server launched
  without `NAKAMA_URL` (it logs one `Nakama: disabled` line at startup and then skips
  every kill submit with no further trace). The new troubleshooting rows carry the
  exact symptoms, the mtime-vs-`git log` check, and the rebuild-with-container-stopped
  sequence the bind-mount file lock forces on Windows/WSL hosts.

### Changed
- **`reward_kill`'s per-kill Info log demoted to Debug** — 20+ lines/s at 200 players,
  part of the same per-kill amplification `reward_kills` removes. The RPC pair stays
  registered for compatibility; `docs/API.md` documents all four economy RPCs and
  marks the pair legacy.

- **The repo-level `CLAUDE.md` listed this module as `Planned`** while `auth/` and
  `economy/` were both implemented and under test. That row is the first thing anyone
  reads when deciding where a piece of work belongs, so it was routing auth and economy
  questions away from code that already answers them. Now `Partial`, naming what exists
  (`auth/`, `economy/`) and what does not (social, matchmaking).

### Added
- Rate limiting on the `gateway_token` RPC: 0.2 calls/s sustained, burst 5, per
  authenticated user id (not per IP — callers are authenticated here, and carrier
  NAT would otherwise collapse thousands of players into one bucket). Over-limit
  calls return `ErrRateLimited` (gRPC `RESOURCE_EXHAUSTED`, code 8) before any
  work is done. Built on `shared/ratelimit` with TTL eviction
- ⚠️ **Multi-instance caveat**: the limiter is in-process. N Nakama instances
  admit N x the limit for a given user. Accepted for the MVP (single-instance
  deployment tiers); a Redis-backed counter is the production upgrade (ADR-8)

### Changed
- `IssueGatewayToken` signs with `jwt.Keyring`, so a rotating
  `JWT_SECRET="new,old"` signs with `new` only. Previously the whole
  comma-separated string would have been used as a literal secret

### Changed
- Bump Go version to 1.26 (align with CI and gameserver)

### Added
- `main.go` — `InitModule` plugin entry point registering all auth hooks and RPCs
- `auth` package:
  - `gateway_token` RPC — issues a realtime session JWT (HS256) for the authenticated
    user, signed with `shared/jwt` so the Gateway verifies it locally with the shared
    secret (claims: `sub`, optional `sid`, `iat`, `exp`; TTL = `constants.SessionTTL`)
  - `AfterAuthenticateDevice` / `AfterAuthenticateEmail` hooks — idempotent player
    profile bootstrap in Nakama storage (`player` / `profile`: level, created_at,
    display_name; public read, server-only write)
  - `BeforeAuthenticateEmail` hook — email format and password length validation with
    proper gRPC error codes (`invalid email address`, `password too short`)
  - `LoadConfig` — reads `JWT_SECRET` from `runtime.RUNTIME_CTX_ENV` with fallback to
    `shared/config`
- Unit tests (table-driven, 11 tests / 34 subtests) with a minimal `runtime.NakamaModule`
  mock: token issuance verified via the `shared/jwt` verifier, first-login vs existing-login
  profile creation, credential validation cases
- Dependencies: `github.com/heroiclabs/nakama-common`, `shared` via `replace ../shared`
- Docs: `docs/README.md` (build `.so` via `heroiclabs/nakama-pluginbuilder`, run, config),
  `docs/API.md` (RPC + hook reference), `docs/DESIGN.md` (dated decisions),
  `docs/RUNBOOK.md` (deploy/rollback/troubleshooting)
- Initial module setup with go.mod (`github.com/duycuong/rpg-mmo/nakama`)
- CLAUDE.md agent instructions for Nakama Engineer role
