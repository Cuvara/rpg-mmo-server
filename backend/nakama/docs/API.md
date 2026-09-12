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

> ⚠️ **`runtime.http_key` is a static, never-expiring bearer secret, and it travels
> in the URL QUERY STRING** — `POST /v2/rpc/reward_kills?http_key=…`, both as Nakama
> requires it and as `GameServer/Nakama/NakamaClient.cs` sends it.
>
> Two consequences worth stating rather than discovering (ADR-24):
>
> 1. **Left at Nakama's published default it authenticates.** Measured on the live
>    stack: no key → `401 "Auth token or HTTP key required"`; a wrong key → `401
>    "HTTP key invalid"`; `defaulthttpkey` → `400 "user_id is required"`, i.e. past
>    auth and into the handler. Anyone who can reach `:7350` could grant rewards.
>    CD now refuses to deploy an environment whose `NAKAMA_HTTP_KEY` is unset or
>    default — it previously never wrote the variable at all, so every deployed
>    environment ran the default.
> 2. **A query-string credential survives TLS into logs.** Making the hop
>    confidential (ADR-24) stops it being read off the wire; it does not stop it
>    appearing in Nakama's own access logs or any future proxy's. That is an
>    upstream API shape, not something this repo can change.

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

### `party_create`, `party_join`, `party_leave`, `party_get`

Storage-backed party, capped at **4 members** (`social.MaxPartyMembers`). Not
Nakama's realtime/socket Party API — see `DESIGN.md` for why.

- **Registered in**: `main.go` → `social.PartyCreateRPC` / `PartyJoinRPC` /
  `PartyLeaveRPC` / `PartyGetRPC`
- **Auth**: `party_create`, `party_join` and `party_leave` require a Nakama
  client session; a `http_key` caller has no subject and gets code `16`.
  **`party_get` accepts both** — the gateway calls it server-to-server to
  verify party membership before allocating a dungeon instance.

Request payload — `party_join` and `party_get` only (`party_create` and
`party_leave` act on the caller and take no payload):

```json
{ "party_id": "8f14e45fceea167a5a36dedd4bea2543" }
```

Response payload (all four RPCs share it):

```json
{
  "party_id": "8f14e45fceea167a5a36dedd4bea2543",
  "leader_id": "3f9c…",
  "members": ["3f9c…", "a71b…"],
  "member_count": 2,
  "max_members": 4,
  "created_at": 1785801600,
  "updated_at": 1785801742
}
```

| Field | Type | Description |
|-------|------|-------------|
| `party_id` | string | 128-bit hex id. **Empty** when the call left the caller in no party — i.e. the last member leaving, which deletes the party. |
| `leader_id` | string | Always one of `members` |
| `members` | string[] | Join order, leader first at creation. Order is load-bearing: leadership transfers to `members[0]` after the leader is removed. |
| `member_count` | int | `len(members)` |
| `max_members` | int | Always `MaxPartyMembers` (4), so a client can render "3/4" without hardcoding the cap |

Behaviour:

| RPC | Effect |
|-----|--------|
| `party_create` | Caller becomes leader of a new party of one. Fails if the caller is already in a party. |
| `party_join` | Adds the caller. The 5th member is refused with code `9`. Re-joining the party you are already in is **idempotent success**, so a client retrying after a timeout is not told "already in a party" about itself. |
| `party_leave` | Removes the caller. Leader leaving transfers leadership to `members[0]`; last member leaving **deletes** the party. |
| `party_get` | Reads leader + members. The gateway's check is `GetParty(...).IsMember(userID)`. |

Errors:

| Code | Message | Cause |
|------|---------|-------|
| 16 | `unauthenticated` | No user id in the context. Mutations only; `party_get` never returns it. |
| 3 | `invalid payload` | Payload is not valid JSON |
| 3 | `party_id is required` | `party_id` missing or empty on `party_join` / `party_get` |
| 5 | `party not found` | No party under that id. Normal for a stale id: a party is deleted when its last member leaves. |
| 9 | `party is full` | Join would exceed `MaxPartyMembers`. `FAILED_PRECONDITION`, not `RESOURCE_EXHAUSTED`: it is a game rule and retrying cannot help. |
| 9 | `already in a party` | Caller is in a **different** party. Call `party_leave` first — the join does **not** move you silently. |
| 9 | `not in a party` | `party_leave` with no membership |
| 10 | `party is being modified concurrently, retry` | 5 optimistic-concurrency attempts all lost the version check. The **one** party error worth retrying. |
| 8 | `rate limited` | Mutation rate limit (below). `party_get` is not limited. |
| 13 | `internal error` | Storage failure. Detail is logged, never returned. |

Storage records:

| Property | Party | Membership index |
|----------|-------|------------------|
| Collection | `party` | `party_member` |
| Key | party id | `current` (one per user — the fixed key is what makes the create-only write enforce one party per user) |
| Owner | **system** (empty user id) | the member |
| Permission read | `0` | `1` (owner may read "which party am I in" without an RPC) |
| Permission write | `0` | `0` |

Both records are written in **one `nk.MultiUpdate`** per mutation, so the party
and its reverse index can never disagree.

### `party` mutation rate limit

| Limit | Value | Key |
|-------|-------|-----|
| Sustained | `PartyWriteRatePerSec` = 0.5/s (one per 2s) | authenticated user id |
| Burst | `PartyWriteBurst` = 10 | authenticated user id |

One bucket shared by `party_create`, `party_join` and `party_leave`, because
the abuse shape is a create/leave or join/leave loop and per-RPC buckets would
let it run at the sum of the limits. Same per-process caveat as `gateway_token`.

`party_get` is deliberately **not** limited: the gateway's calls carry no user
id to key a bucket on, and keying them all to the empty string would let one
player's joins throttle the whole cluster's dungeon allocations.

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
| `social.RPCPartyCreate`, `RPCPartyJoin`, `RPCPartyLeave`, `RPCPartyGet` | RPC id constants |
| `social.PartyCreateRPC`, `PartyJoinRPC`, `PartyLeaveRPC`, `PartyGetRPC` | RPC handlers |
| `social.CreateParty` / `JoinParty` / `LeaveParty` / `GetParty` | Party logic against a narrow storage interface (unit-testable without Nakama) |
| `social.Party` / `Party.IsMember(userID)` | Stored party record; `IsMember` is the gateway's dungeon-entry check |
| `social.PartyRequest` / `PartyResponse` | RPC payload types |
| `social.ErrPartyFull`, `ErrAlreadyInParty`, `ErrNotInParty`, `ErrPartyNotFound`, `ErrPartyBusy`, `ErrPartyIDRequired`, `ErrUnauthenticated`, `ErrInvalidPayload`, `ErrRateLimited`, `ErrInternal` | Client-facing runtime errors |
| `social.MaxPartyMembers`, `PartyCollection`, `MembershipCollection`, `MembershipKey` | Constants |
| `social.PartyWriteRatePerSec`, `PartyWriteBurst`, `PartyWriteIdleTTL` | Party mutation rate-limit constants |

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
