# Nakama Module — API Reference

## RPCs

### `gateway_token`

Issues a realtime session token (HS256 JWT) that the Gateway verifies locally.

- **Auth**: required (Nakama session). Unauthenticated callers get code `16`.
- **Registered in**: `main.go` → `auth.GatewayTokenRPC`

Request payload (optional, may be an empty string):

```json
{ "server_id": "map_01-abc" }
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `server_id` | string | no | Pins the token to a specific game server instance. Omitted → the `sid` claim is not emitted. |

Response payload:

```json
{
  "token": "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.…",
  "user_id": "3f9c…",
  "expires_in": 3600
}
```

| Field | Type | Description |
|-------|------|-------------|
| `token` | string | HS256 JWT, signed with the Gateway's shared secret |
| `user_id` | string | Nakama user ID, equal to the token's `sub` claim |
| `expires_in` | int | Token lifetime in seconds (`constants.SessionTTL`, 3600) |

Token claims (produced by `shared/jwt`, consumed by `gateway/session`):

| Claim | Source | Description |
|-------|--------|-------------|
| `sub` | Nakama user ID | Read by `session.VerifyClientJWT` |
| `sid` | request `server_id` | Optional, omitted when empty |
| `iat` | now | Issued-at, Unix seconds |
| `exp` | now + TTL | Expiry, Unix seconds; `jwt.Verify` rejects expired tokens |

Errors:

| Code | Message | Cause |
|------|---------|-------|
| 16 | `unauthenticated` | No user ID in the request context |
| 3 | `invalid payload` | Payload is not valid JSON |
| 13 | `internal error` | Signing or marshalling failure |

### `reward_kills`

Grants gold **and** leaderboard score for a batch of kills in one call,
**exactly once per `batch_id`**. This is the RPC the game server uses; it
replaced the per-kill `reward_kill` + `submit_kill` pair, which cost 2 HTTP
requests and 2 separate meta-DB transactions per mob kill (rpg-mmo-server#233).

- **Auth**: **server-only** — `runtime.http_key` (server-to-server). A call
  carrying a Nakama client session (user id / session id / session expiry in
  the request context) is rejected with code `7` (`PERMISSION_DENIED`, HTTP
  403) **before the payload is parsed** and before any wallet or leaderboard
  write. Enforced by `economy.requireServerCaller`, shared by all three
  mutation RPCs.
- **Registered in**: `main.go` → `economy.RewardKillsRPC`

Request payload:

```json
{ "user_id": "3f9c…", "kills": 3, "map_id": "map_01", "batch_id": "9c41…" }
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `user_id` | string | yes | Nakama user id of the killer |
| `kills` | int | yes | 1..1000 (`MaxKillsPerBatch`). Out of range → code `11`, nothing granted; the caller splits |
| `map_id` | string | no | Recorded in the wallet metadata and the receipt |
| `batch_id` | string | **yes** | **Idempotency key.** Must be stable across every retry of the same batch. A `batch_id` already granted is replayed from its receipt with no second wallet change |

Response payload:

```json
{ "success": true, "status": "granted", "replayed": false, "gold": 30, "balance": 480, "score": 41, "rank": 2 }
```

| Field | Type | Description |
|-------|------|-------------|
| `success` | bool | Always `true` on 2xx |
| `status` | string | `granted` — gold and score committed. `partial` — gold committed, leaderboard write failed; **resend the same `batch_id`**, the replay retries only the leaderboard |
| `replayed` | bool | `true` when this `batch_id` had already been granted by an earlier call; the wallet was not touched again |
| `gold` | int | Gold granted for this batch (`GoldPerKill × kills`) — on the original call and on replays |
| `balance` | int | Wallet gold after the grant; present only on the call that performed it |
| `score`, `rank` | int | Leaderboard state; `0` with `status: partial` |
| `leaderboard_error` | string | Set with `status: partial` |

**Exactly-once mechanics.** The wallet update and a receipt
(`storage collection "reward_receipts"`, key = `batch_id`, owner = `user_id`,
permissions read 0 / write 0) are committed in one transaction via
`nk.MultiUpdate`, with the receipt written create-only (`version: "*"`). A
resent `batch_id` finds the receipt and is answered as a replay; two duplicates
racing past the lookup both reach `MultiUpdate`, the loser fails the version
check, its whole transaction (wallet included) rolls back, and it is answered
as a replay too. The leaderboard increment is **not** in that transaction
(Nakama's `MultiUpdate` does not cover leaderboards): its outcome is recorded
in the receipt afterwards (`leaderboard_done`), which is what lets a replay
retry only the leaderboard. If that receipt update itself fails after the
score landed, a later replay increments the score once more — bounded score
drift on a double fault is the accepted cost (ADR-6); it is logged as
`receipt update failed for batch …`.

**Error contract — load-bearing for the caller's retry policy.** An error
(non-2xx) is returned **only when nothing was granted by this call**, so any
error — and any timeout — may be answered by resending the **same**
`batch_id`. Never mint a new id for a retry: that is the one way to grant
twice.

| Code | Message | Cause |
|------|---------|-------|
| 7 | `server-only rpc` | Caller is a client session, not `runtime.http_key`; nothing granted, do not retry |
| 3 | `invalid payload` / `user_id is required` / `batch_id is required` | Malformed request; nothing granted |
| 11 | `kills must be in 1..1000` | `OUT_OF_RANGE` (`CodeKillsOutOfRange`); nothing granted. Split the batch — each part under a **new** id, since this one never reached the wallet |
| 13 | `receipt lookup failed: …` / `wallet update failed: …` | Storage or transaction failure; nothing granted, safe to resend the same id |

**Receipt retention.** One receipt per granted batch, i.e. one per killer per
game-server flush that carried kills (a 3s flush ≈ ≤1,200 rows per grinding
player per hour, a few hundred bytes each). Nothing prunes them yet; they are
the audit trail for a disputed grant (`SELECT` on storage collection
`reward_receipts`, or the Console's Storage page). A retention job is a
follow-up, not a blocker at current scale.

### `reward_kill`, `submit_kill` (legacy per-kill pair)

Still registered for compatibility; the game server no longer calls them. Each
grants one kill's gold (`reward_kill`) or one leaderboard point (`submit_kill`)
per HTTP call — the per-kill amplification `reward_kills` exists to remove. New
callers should use `reward_kills`.

Both are **server-only** under the same guard as `reward_kills`: a client
session gets code `7` `server-only rpc` before the payload is read.

### `get_leaderboard`

Returns the top 10 of `kills_alltime` as
`{ "leaderboard_id": …, "records": [{ "rank", "user_id", "username", "score" }] }`.
Read-only, so it carries no caller guard: callable with `http_key` or by a
client session. The `kills_alltime` board is **authoritative** — records can be
written only by the runtime (the server-only RPCs above); a client's own
`WriteLeaderboardRecord` against it is refused by Nakama. Clients may still
*read* it through Nakama's own REST API.

## Hooks

### `BeforeAuthenticateEmail`

Validates the email/password pair before Nakama processes the request, and
normalises the email to lower case (trimmed).

Validation rules (`auth.ValidateEmailCredentials`):

| Rule | Error | Code |
|------|-------|------|
| Non-empty, RFC-5322 parsable, bare address (no display-name form) | `invalid email address` | 3 |
| Domain part contains a `.` | `invalid email address` | 3 |
| Password length ≥ 8 (`DefaultMinPasswordLength`) | `password too short` | 3 |

### `AfterAuthenticateDevice` / `AfterAuthenticateEmail`

Bootstraps the player profile on first login. Reads the profile record first;
writes only when it does not exist, so repeat logins are a no-op. A missing user
ID in the context is logged at WARN and treated as a no-op (never blocks login).

Storage record:

| Property | Value |
|----------|-------|
| Collection | `player` |
| Key | `profile` |
| Owner | the authenticating user |
| Permission read | `2` (public) |
| Permission write | `0` (server-authoritative only) |

```json
{
  "level": 1,
  "created_at": 1785801600,
  "display_name": "Arthas"
}
```

`display_name` defaults to the Nakama username; when that is empty it falls back
to `Player-<first 8 chars of user id>`.

## Exported Go API

| Symbol | Description |
|--------|-------------|
| `InitModule` | Nakama plugin entry point (package `main`) |
| `auth.RPCGatewayToken` | RPC id constant `"gateway_token"` |
| `auth.GatewayTokenRPC` | RPC handler |
| `auth.IssueGatewayToken(userID, serverID, cfg)` | Signs a Gateway-compatible JWT |
| `auth.GatewayTokenRequest` / `GatewayTokenResponse` | RPC payload types |
| `auth.EnsureProfile(ctx, nk, userID, displayName)` | Idempotent profile creation; reports whether it created one |
| `auth.Profile` | Stored profile record |
| `auth.ValidateEmailCredentials(email, password, minLen)` | Credential validation |
| `auth.AfterAuthenticateDevice` / `AfterAuthenticateEmail` / `BeforeAuthenticateEmail` | Hook handlers |
| `auth.LoadConfig(ctx)` / `auth.Config` | Env-driven configuration |
| `auth.ErrUnauthenticated`, `ErrInvalidPayload`, `ErrInternal`, `ErrInvalidEmail`, `ErrWeakPassword`, `ErrRateLimited` | Client-facing runtime errors |
| `auth.ProfileCollection`, `ProfileKey`, `StartingLevel`, `DefaultMinPasswordLength` | Constants |
| `auth.TokenRatePerSec`, `TokenBurst`, `TokenIdleTTL` | `gateway_token` rate-limit constants |
| `economy.RPCRewardKill`, `RPCRewardKills`, `RPCSubmitKill`, `RPCGetLeaderboard` | RPC id constants |
| `economy.RewardKillRPC`, `RewardKillsRPC`, `SubmitKillRPC`, `GetLeaderboardRPC` | RPC handlers |
| `economy.ErrServerOnly` | Code `7` error returned to client sessions by the mutation RPCs |
| `economy.SetupLeaderboards(ctx, logger, nk)` | Creates `kills_alltime` (authoritative) or fails init if an existing board is not |
| `economy.LeaderboardKillsAllTime`, `LeaderboardMigrateEnv`, `GoldPerKill`, `MaxKillsPerBatch`, `CodeKillsOutOfRange`, `ReceiptCollection`, `StatusGranted`, `StatusPartial` | Constants |

### `gateway_token` rate limit

| Limit | Value | Key |
|-------|-------|-----|
| Sustained | `TokenRatePerSec` = 0.2/s (one per 5s) | authenticated user id |
| Burst | `TokenBurst` = 5 | authenticated user id |

Exceeding it returns `ErrRateLimited` — message `"rate limited"`, gRPC code `8`
(`RESOURCE_EXHAUSTED`) — before the payload is parsed. Clients should back off,
not retry immediately.

⚠️ The limiter is **per Nakama process**: N instances admit N x the limit for a
given user. See `docs/DESIGN.md` and ADR-8.

`JWT_SECRET` may be a comma-separated rotation list; Nakama, as the issuer,
always signs with the **first** entry.
