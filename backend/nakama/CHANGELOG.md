# Changelog — Nakama Module

All notable changes to the Nakama module will be documented in this file.
Format based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Security

- **`NAKAMA_HTTP_KEY` must now be set and non-default for any deployed environment** (ADR-24). It
  authenticates `reward_kill`, `reward_kills` and `submit_kill` — the server-only RPCs
  `economy/caller.go:requireServerCaller` guards — and it was running at Nakama's published default
  everywhere, because CD never wrote the variable. See `backend/deploy/CHANGELOG.md` for the
  measurement and the gate.

### Documentation

- **`backend/TEAM.md`'s "Nakama <-> GameServer: Internal RPC (signed) for reward granting" was
  backwards on both counts and is corrected.** The plugin makes **no outbound network calls at
  all**; the **C# game server calls Nakama** (`GameServer/Nakama/NakamaClient.cs`). And the call is
  **not signed** — it authenticates with `runtime.http_key` in the **query string**, which is a
  static bearer secret in a URL, so it lands in access logs even once the hop is TLS.

### Security
- **Economy mutation RPCs are server-only** (audit 2026-09-07 F01, P0). `reward_kill`,
  `reward_kills` and `submit_kill` take the beneficiary `user_id` from the payload, and
  Nakama exposes every registered RPC to authenticated clients as well as to
  `runtime.http_key` callers — so any logged-in client could grant itself (or anyone)
  gold and score. A shared guard, `economy.requireServerCaller`, now rejects any
  invocation whose context carries a client-session marker (`RUNTIME_CTX_USER_ID`,
  `RUNTIME_CTX_SESSION_ID` or `RUNTIME_CTX_USER_SESSION_EXP`) with gRPC code 7
  (`PERMISSION_DENIED`, HTTP 403, message `server-only rpc`) **before the payload is
  parsed** and before any `WalletUpdate`/`LeaderboardRecordWrite`. `runtime.http_key`
  calls carry none of those markers, so the game server's `NakamaClient`
  (`/v2/rpc/<id>?http_key=…`) is unaffected. `get_leaderboard` (read) and
  `gateway_token` (client RPC by design) are unchanged.
- **`kills_alltime` leaderboard is authoritative** (F02, P1). It was created with
  `authoritative=false`, letting clients write their own scores through Nakama's public
  `WriteLeaderboardRecord` regardless of the RPC guard. Because `LeaderboardCreate` is
  idempotent and never alters an existing board, `SetupLeaderboards` now looks the
  board up first (`LeaderboardsGetId`); an existing non-authoritative board makes
  **InitModule fail** with the fix in the error message rather than deleting data or
  silently leaving the hole open. Data-preserving migration is one SQL update plus a
  Nakama restart (`UPDATE leaderboard SET authoritative = true WHERE id =
  'kills_alltime'`, see `docs/RUNBOOK.md`); `LEADERBOARD_MIGRATE=recreate` in the
  runtime env opts into delete-and-recreate for disposable dev/staging boards. Chosen
  over auto-recreate because a start-up hook must not destroy player records on its
  own, and over log-and-continue because that keeps a P1 open unnoticed.

### Fixed
- **`reward_kills` is exactly-once per `batch_id`** (audit 2026-09-07 F06, P1). The
  batch id was only metadata; a retry under a new id after a post-commit connection
  failure granted twice, and the game server's answer to that — dropping any batch whose
  outcome was unknown — lost gold instead. A receipt (storage collection
  `reward_receipts`, key = `batch_id`, owner = user, read/write 0) is now written
  **create-only** in the same `nk.MultiUpdate` transaction as the wallet update, so gold
  and receipt commit or roll back together. A resent id is replayed from the receipt
  (`replayed: true`, no wallet change); a duplicate racing past the lookup loses the
  version check and its whole transaction rolls back. Chosen over a separately written
  dedupe marker because only the one-transaction form closes the crash window between
  marker and grant. `batch_id` is now **required** (code 3 when empty).
- **Leaderboard failure after the grant is no longer a silent success.** The response
  carries `status: "granted" | "partial"`; `partial` means gold committed, score not.
  A replay of the same batch id retries **only** the leaderboard until it lands
  (`leaderboard_done` in the receipt), so score converges without a second grant. The
  residual double fault (score written, receipt update failed, batch replayed) is a
  bounded score over-count, logged as `receipt update failed for batch …` (ADR-6).
- **Over-cap batches are rejected with a machine-readable code** (F07). `kills` outside
  1..1000 returns gRPC code 11 `OUT_OF_RANGE` (`economy.CodeKillsOutOfRange`) instead of
  the generic 3, so the game server can split rather than treat it as malformed. Nothing
  is granted, so the parts may take new ids.

### Added
- `economy.ErrServerOnly`, `economy.LeaderboardMigrateEnv`.
- `scripts/probe-economy.sh` — dependency-free (bash + curl + python3) live probe of
  F01/F02/F06/F07 against a running Nakama: session calls → 403, `http_key` grant,
  replay with unchanged wallet, code 11 over cap, missing `batch_id`, authoritative
  board refusing client writes, server-side score = 3. Documented in `docs/RUNBOOK.md`
  ("Live probe after deploy").
- `economy.ReceiptCollection`, `CodeKillsOutOfRange`, `StatusGranted`, `StatusPartial`;
  response fields `status`, `replayed`, `balance` (`gold` now means gold granted for the
  batch, on original and replay alike).
- Tests: same `batch_id` twice → one wallet update and one leaderboard write; racing
  duplicate loses the create-only version check and is answered as a replay;
  `MultiUpdate` failure leaves no receipt and the resend is granted fresh; receipt lookup
  failure grants nothing; `partial` then replay converges the score with exactly one
  grant; out-of-range kills → code 11 with zero calls; receipt write is create-only and
  server-only.
- Table-driven tests: guard accepts server (no-session) context and rejects user id /
  session id / session expiry; all three mutation RPCs reject a client session before
  parsing with zero granter calls; server caller still grants; `SetupLeaderboards`
  creates `authoritative=true`, leaves an authoritative board alone, fails on a legacy
  board by default, and deletes+recreates only on opt-in.

### Changed
- `docs/API.md`, `docs/RUNBOOK.md`, `docs/DESIGN.md`, `docs/README.md` describe the
  server-only contract, error code 7, the leaderboard migration, the exactly-once
  receipt mechanics, `status`/`replayed`, code 11, receipt retention and how to audit
  a disputed batch.

## [0.9.0] - 2026-09-05

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
