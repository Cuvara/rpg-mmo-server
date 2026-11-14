# Nakama Module — API Reference

## RPCs

### `gateway_token`

Issues a realtime session token (HS256 JWT) that the Gateway verifies locally.

- **Auth**: required (Nakama session). Unauthenticated callers get code `16`.
- **Registered in**: `main.go` → `auth.GatewayTokenRPC`

Request payload (optional, may be an empty string):

```json
{ "server_id": "map_01-abc", "character_id": "5f0c…-uuid", "game_id": "foo" }
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `server_id` | string | no | Pins the token to a specific game server instance. Omitted → the `sid` claim is not emitted. |
| `character_id` | string | no | ADR-31. Roster character this realtime session plays. Must be in the caller's own roster (`character_list`) for the session game, checked by `character.Owns` in `Scoped(game, "characters")` — `characters` for `cuvara`, `<game>.characters` for any other game, with no fallback to `characters`; otherwise code `5` `character not found` (same answer for "not yours" and "does not exist"). Emitted as the `cid` claim, which the gateway copies into the join token. Omitted → no `cid` claim → the account's default character (protocol 2 behaviour). |
| `game_id` | string | no | ADR-33. Never selects the game: the game is the session variable `game` (absent = `cuvara`). When supplied it must equal that session game, otherwise code `7` `game_id does not match the session game`. Omitting it is the normal case. |

The game is resolved by `gamectx.GameFromContext` (see *Game identity* below)
before the roster check. For a non-default game the RPC also creates the
caller's `<game>.player`/`profile` record if it is missing (same record and
rules as the after-authenticate hooks), so an account that predates the game
gets its profile on its first token.

Response payload:

```json
{
  "token": "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.…",
  "user_id": "3f9c…",
  "expires_in": 3600,
  "character_id": "5f0c…-uuid",
  "game_id": "foo"
}
```

| Field | Type | Description |
|-------|------|-------------|
| `token` | string | HS256 JWT, signed with the Gateway's shared secret |
| `user_id` | string | Nakama user ID, equal to the token's `sub` claim |
| `expires_in` | int | Token lifetime in seconds (`constants.SessionTTL`, 3600) |
| `character_id` | string | Echo of the `cid` claim; omitted for the default character |
| `game_id` | string | ADR-33. Echo of the `gid` claim; omitted for the default game `cuvara` |

Token claims (produced by `shared/jwt`, consumed by `gateway/session`):

| Claim | Source | Description |
|-------|--------|-------------|
| `sub` | Nakama user ID | Read by `session.VerifyClientJWT` |
| `sid` | request `server_id` | Optional, omitted when empty |
| `cid` | request `character_id` (ownership-checked) | Optional (ADR-31), omitted when empty = default character. The gateway copies it into the join token; the game server echoes it in `JoinTokenResponse.character_id` |
| `gid` | session var `game` (allow-listed) | Optional (ADR-33), minted through `shared/jwt` `SignWithGame`; **omitted for `cuvara`**, so a default-game token is byte-for-byte the pre-ADR-33 shape. `Claims.Game()` reads absent as `cuvara` |
| `iat` | now | Issued-at, Unix seconds |
| `exp` | now + TTL | Expiry, Unix seconds; `jwt.Verify` rejects expired tokens |

Errors:

| Code | Message | Cause |
|------|---------|-------|
| 16 | `unauthenticated` | No user ID in the request context |
| 3 | `invalid payload` | Payload is not valid JSON |
| 3 | `game not allowed` | The session game is not in `GAMES` (or not a valid game id) |
| 7 | `game_id does not match the session game` | Request `game_id` differs from the session var `game` |
| 5 | `character not found` | `character_id` is not in the caller's roster (the game's own roster for a non-default game) |
| 13 | `internal error` | Signing, marshalling, roster-read or profile-write failure, or an invalid `GAMES` value (logged server-side) |

### `character_list`, `character_create`, `character_delete` (ADR-31)

The character roster. Nakama owns the roster (id, slot, name, created); the game server owns
each character's state, bag and equipment in the game-state DB, keyed by the id minted here
(ADR-1, ADR-31). The plugin makes no outbound call: deleting a character does **not** touch
its game-state rows.

- **Auth**: client session required for all three (code `16` otherwise; a
  `runtime.http_key` call has no subject).
- **Registered in**: `main.go` → `character.ListRPC`, `character.CreateRPC`, `character.DeleteRPC`.
- **Game**: the session var `game` (absent = `cuvara`), resolved after the rate limit and
  before the payload is read. The RPCs take no `game_id`. See *Game identity* below.
- **Storage**: collection `Scoped(game, "characters")` — `characters` for `cuvara`,
  `<game>.characters` for any other game — key `roster`, owner = the user; one object per
  user per game holding the whole roster, permission read `1` (owner), write `0` (only these
  RPCs). A character belongs to exactly one game's roster; nothing reads another game's
  collection. Every mutation is a version-checked write retried up to 5 times, so two
  concurrent creates cannot exceed the slot cap or share a slot.

**Placeholder limits (pending design, ADR-31 "Consequences")** — `character/character.go`:

| Constant | Placeholder value |
|----------|-------------------|
| `MaxCharacterSlots` | 4 |
| `NameMinLength` / `NameMaxLength` | 3 / 16 (bytes; ASCII alphabet) |
| `NamePatternPlaceholder` | `^[A-Za-z0-9_]+$` (no uniqueness, no localisation, no profanity filter) |

`character_list` — no payload. Response:

```json
{ "characters": [ { "id": "5f0c…", "slot": 0, "name": "Arthas", "created_at": 1700000000 } ], "max_slots": 4 }
```

Characters are ordered by slot; `characters` is `[]` (never `null`) for an empty roster.

`character_create` — payload `{ "name": "Arthas", "slot": 2 }`; `slot` optional (absent or
negative = lowest free slot). Response `{ "character": { "id", "slot", "name", "created_at" } }`.
`id` is a random UUID v4.

`character_delete` — payload `{ "character_id": "5f0c…" }`. Response
`{ "character_id": "5f0c…", "deleted": true }`.

Errors:

| Code | Message | Cause |
|------|---------|-------|
| 16 | `unauthenticated` | No client session |
| 3 | `invalid payload` | Payload missing (create) or not valid JSON |
| 3 | `invalid character name` | Name fails the placeholder rule |
| 3 | `invalid character slot` | `slot >= max_slots` |
| 3 | `character_id is required` | Delete without an id |
| 9 | `character roster is full` | Already `max_slots` characters |
| 9 | `character slot is taken` | Requested slot already used |
| 5 | `character not found` | Delete of an id not in the caller's roster |
| 10 | `roster is being modified concurrently, retry` | All 5 version-checked attempts lost (retryable) |
| 8 | `rate limited` | See rate limits below |
| 3 | `game not allowed` | The session game is not in `GAMES` |
| 13 | `internal error` | Storage failure, or an invalid `GAMES` value (detail logged, not returned) |

Rate limits (per user, in-process — same multi-instance caveat as `gateway_token`):

| Bucket | RPCs | Rate | Burst |
|--------|------|------|-------|
| roster write | `character_create` + `character_delete` (shared) | 0.2/s | 5 |
| roster read | `character_list` | 1/s | 10 |

Client end (follow-up, `client-integration`): a character-select screen calling these three,
then `gateway_token` with `character_id`, then `EnterWorldRequest.character_id` (optional;
if sent it must equal the token's `cid`, else the gateway answers `character_mismatch`).

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
{ "game_id": "foo", "user_id": "3f9c…", "kills": 3, "map_id": "map_01", "batch_id": "9c41…" }
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `game_id` | string | no | ADR-33. The game the kills belong to; absent = `cuvara`. Must be in `GAMES` (code `3`). Selects the wallet currency `Scoped(game, "gold")`, the receipt collection `Scoped(game, "reward_receipts")` and the board `Scoped(game, "kills_alltime")`. The game server fills it from `GAMESERVER_GAME_ID` |
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
| `balance` | int | Wallet balance of the game's currency key (`gold`, or `<game>.gold`) after the grant; present only on the call that performed it |
| `score`, `rank` | int | Leaderboard state; `0` with `status: partial` |
| `leaderboard_error` | string | Set with `status: partial` |

**Wallet ledger metadata**: `source: "enemy_kills"`, `kills`, `map_id`,
`batch_id`, plus `game_id` for a non-default game (omitted for `cuvara`, so its
ledger rows are unchanged).

**Exactly-once mechanics.** The wallet update and a receipt
(storage collection `Scoped(game, "reward_receipts")`, key = `batch_id`,
owner = `user_id`, permissions read 0 / write 0) are committed in one transaction via
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
| 3 | `game not allowed` | `game_id` not in `GAMES` (checked after the payload checks, before the receipt lookup); nothing granted, do not retry until `GAMES` is fixed |
| 13 | `receipt lookup failed: …` / `wallet update failed: …` | Storage or transaction failure; nothing granted, safe to resend the same id |
| 13 | `internal error` | Invalid `GAMES` value at request time (logged); nothing granted |

**Receipt retention.** One receipt per granted batch, i.e. one per killer per
game-server flush that carried kills (a 3s flush ≈ ≤1,200 rows per grinding
player per hour, a few hundred bytes each). Nothing prunes them yet; they are
the audit trail for a disputed grant (`SELECT` on storage collection
`reward_receipts`, or `<game>.reward_receipts` for a non-default game, or the Console's Storage page). A retention job is a
follow-up, not a blocker at current scale.

### `reward_kill`, `submit_kill` (legacy per-kill pair)

Still registered for compatibility; the game server no longer calls them. Each
grants one kill's gold (`reward_kill`) or one leaderboard point (`submit_kill`)
per HTTP call — the per-kill amplification `reward_kills` exists to remove. New
callers should use `reward_kills`.

Both are **server-only** under the same guard as `reward_kills`: a client
session gets code `7` `server-only rpc` before the payload is read.

Request payloads:

```json
{ "game_id": "foo", "user_id": "3f9c…", "victim_id": "mob_17", "map_id": "map_01" }
{ "game_id": "foo", "user_id": "3f9c…" }
```

`game_id` is optional on both (ADR-33), absent = `cuvara`, and must be in
`GAMES` (code `3`; an invalid `GAMES` answers code `13` `internal error`). It is
checked after `user_id` and before any write. `reward_kill` credits
`Scoped(game, "gold")` and adds `game_id` to the ledger metadata for a
non-default game; `submit_kill` writes `Scoped(game, "kills_alltime")`, so a
non-default game never touches `kills_alltime`.

### `get_leaderboard`

Returns the top 10 of the game's kills board as
`{ "leaderboard_id": …, "records": [{ "rank", "user_id", "username", "score" }] }`.
`leaderboard_id` is the board read: `kills_alltime` for `cuvara`,
`<game>.kills_alltime` for any other game. The game is the session var `game`;
the RPC takes no `game_id`, so an `http_key` call (no session) reads
`kills_alltime`. A session game not in `GAMES` gets code `3` before any read;
an invalid `GAMES` gets code `13`.
Read-only, so it carries no caller guard: callable with `http_key` or by a
client session. Every kills board is **authoritative** — records can be
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

- **Game** (ADR-33): `party_create` and `party_leave` use the session var
  `game`. `party_join` and `party_get` resolve it through
  `gamectx.GameFromContext` after the `party_id` check: with a client session
  the session var decides and a differing `game_id` is refused with code `7`;
  without a session (the gateway's `http_key` call to `party_get`) the payload
  `game_id` selects the game, absent = `cuvara`. The gateway fills it from the
  session's latched `gid`. Every party RPC reads and writes only that game's
  collections, so a party cannot span games.

Request payload — `party_join` and `party_get` only (`party_create` and
`party_leave` act on the caller and take no payload):

```json
{ "party_id": "8f14e45fceea167a5a36dedd4bea2543", "game_id": "foo" }
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `party_id` | string | yes | Party to join or read |
| `game_id` | string | no | Selects the game only without a session (absent = `cuvara`); with a session it must equal the session game. Must be in `GAMES` |

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
| 3 | `game not allowed` | The resolved game is not in `GAMES`; refused before any storage access |
| 7 | `game_id does not match the session game` | `party_join` / `party_get` from a client session with a `game_id` other than the session var |
| 13 | `internal error` | Storage failure, or an invalid `GAMES` value. Detail is logged, never returned. |

Storage records:

| Property | Party | Membership index |
|----------|-------|------------------|
| Collection | `Scoped(game, "party")`: `party` (`cuvara`), `<game>.party` | `Scoped(game, "party_member")`: `party_member` (`cuvara`), `<game>.party_member` |
| Key | party id | `current` (one per user per game — the fixed key is what makes the create-only write enforce one party per user in each game) |
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

## Game identity (ADR-33)

A client selects its game once, at login, through Nakama session variables:

```json
POST /v2/account/authenticate/device?create=true
{ "id": "<device id>", "vars": { "game": "foo" } }
```

Absent `game` = `cuvara`. Nakama copies `vars` into the session token, and the
plugin reads them back from `runtime.RUNTIME_CTX_VARS` on every later call
(`gamectx.GameFromContext`). The game must be in the `GAMES` allow-list
(`README.md` → *Configuration*); otherwise the login, the refresh or the RPC is
refused with code `3` `game not allowed`.

| Error | Code | Raised by |
|-------|------|-----------|
| `game not allowed` (`gamectx.ErrGameNotAllowed`) | 3 | Every before-auth hook, `BeforeSessionRefresh`, and every RPC below |
| `game_id does not match the session game` (`gamectx.ErrGameMismatch`) | 7 | `gateway_token`, `party_join`, `party_get` called with a session and a differing `game_id` |
| `internal error` | 13 | Invalid `GAMES` reached at request time (normally `InitModule` already refused to start) |

Which game each RPC uses:

| RPC | Game source |
|-----|-------------|
| `gateway_token`, `character_list`, `character_create`, `character_delete`, `party_create`, `party_leave`, `get_leaderboard` | Session var `game` (absent = `cuvara`). `gateway_token` also accepts a matching `game_id`; the others take none, so `get_leaderboard` over `http_key` reads `cuvara`'s board |
| `party_join`, `party_get` | Session var with a session (payload `game_id` must match); payload `game_id` without one (absent = `cuvara`) |
| `reward_kill`, `reward_kills`, `submit_kill` | Payload `game_id` (absent = `cuvara`). Server-only, so there is never a session |

Per-game data names come from `gamectx.Scoped(game, name)`: `name` for `cuvara`,
`<game>.<name>` otherwise (ADR-33 D4). No call ever reads another game's name,
and the default game's names, data and payloads are exactly as before:

| Data | Kind | `cuvara` | other game |
|------|------|----------|------------|
| Profile | collection | `player` | `<game>.player` |
| Roster | collection | `characters` | `<game>.characters` |
| Gold | wallet currency key | `gold` | `<game>.gold` |
| Reward receipts | collection | `reward_receipts` | `<game>.reward_receipts` |
| Party | collection | `party` | `<game>.party` |
| Party membership | collection | `party_member` | `<game>.party_member` |
| Kills board | leaderboard id | `kills_alltime` | `<game>.kills_alltime` |

The wallet is one Nakama object per user, so a player of two games holds both
`gold` and `<game>.gold` in it; no currency is shared between games.
`InitModule` creates `<game>.kills_alltime` for each non-default game in
`GAMES` (*Leaderboards* in `RUNBOOK.md`).

## Hooks

| Hook | Game check | Other behaviour |
|------|------------|-----------------|
| `BeforeAuthenticateApple`, `Custom`, `Device`, `Facebook`, `FacebookInstantGame`, `GameCenter`, `Google`, `Steam` | `account.vars.game` must be in `GAMES` (code 3) | Request passed through unchanged; a request with no `account` passes through |
| `BeforeAuthenticateEmail` | Same, checked first | Credential validation and email normalisation (below) |
| `BeforeSessionRefresh` | A non-empty `vars` object is validated as a replacement: `vars.game` must be in `GAMES` (code 3), absent `game` = `cuvara` | An omitted or empty `vars` keeps the refresh token's vars and the game (real Nakama 3.40.0, 2026-11-14: over JSON/protobuf an empty map cannot be told apart from an absent one) |
| `AfterAuthenticateDevice`, `AfterAuthenticateEmail` | Re-validates `vars.game` | Creates the profile in `Scoped(game, "player")` (below) |

### `BeforeAuthenticateEmail`

Refuses a `vars.game` outside `GAMES` (code 3), then validates the
email/password pair before Nakama processes the request, and normalises the
email to lower case (trimmed).

Validation rules (`auth.ValidateEmailCredentials`):

| Rule | Error | Code |
|------|-------|------|
| Non-empty, RFC-5322 parsable, bare address (no display-name form) | `invalid email address` | 3 |
| Domain part contains a `.` | `invalid email address` | 3 |
| Password length ≥ 8 (`DefaultMinPasswordLength`) | `password too short` | 3 |

### `AfterAuthenticateDevice` / `AfterAuthenticateEmail`

Bootstraps the player profile on first login for the login's game
(`vars.game`, absent = `cuvara`). Reads the profile record first;
writes only when it does not exist, so repeat logins are a no-op. The
collection is `gamectx.Scoped(game, "player")`: `player` for `cuvara` (unchanged),
`<game>.player` for any other game. A missing user
ID in the context is logged at WARN and treated as a no-op (never blocks login).

Storage record:

| Property | Value |
|----------|-------|
| Collection | `player` (`cuvara`), `<game>.player` (other games) |
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
| `auth.IssueGatewayTokenForCharacter(userID, serverID, characterID, cfg)` | Same, with the `cid` claim; does not check ownership (the RPC does) |
| `auth.IssueGatewayTokenForGame(userID, serverID, characterID, gameID, cfg)` | Same, with the `gid` claim (ADR-33; omitted for empty/`cuvara`); does not check the allow-list or ownership (the RPC does) |
| `auth.GatewayTokenRequest` / `GatewayTokenResponse` | RPC payload types |
| `auth.EnsureProfile(ctx, nk, userID, displayName)` | Idempotent profile creation; reports whether it created one |
| `auth.EnsureGameProfile(ctx, nk, game, userID, displayName)` | Same, in `Scoped(game, "player")`; the caller must have validated `game` |
| `auth.Profile` | Stored profile record |
| `auth.ValidateEmailCredentials(email, password, minLen)` | Credential validation |
| `auth.AfterAuthenticateDevice` / `AfterAuthenticateEmail` / `BeforeAuthenticateEmail` | Hook handlers |
| `auth.BeforeAuthenticateApple` / `Custom` / `Device` / `Facebook` / `FacebookInstantGame` / `GameCenter` / `Google` / `Steam`, `auth.BeforeSessionRefresh` | ADR-33 game allow-list hooks |
| `gamectx.VarGame`, `EnvGames` | Session var name `"game"`, allow-list env name `"GAMES"` |
| `gamectx.ParseAllowList(raw)` / `AllowList(ctx)` / `Allowed(ctx, game)` | `GAMES` parsing (runtime env first, process env fallback) |
| `gamectx.GameFromVars(ctx, vars)` | Validated game from a `vars` map (auth and refresh hooks) |
| `gamectx.GameFromContext(ctx, payloadGameID)` | Validated game of a call: session var with a session (payload must match), payload without one (`http_key`) |
| `gamectx.Scoped(game, name)` | `name` for `cuvara`, `<game>.<name>` otherwise |
| `gamectx.ErrGameNotAllowed` (3), `ErrGameMismatch` (7), `ErrGamesConfig` (13) | Runtime errors |
| `auth.LoadConfig(ctx)` / `auth.Config` | Env-driven configuration |
| `auth.ErrUnauthenticated`, `ErrInvalidPayload`, `ErrInternal`, `ErrInvalidEmail`, `ErrWeakPassword`, `ErrRateLimited` | Client-facing runtime errors |
| `auth.ProfileCollection`, `ProfileKey`, `StartingLevel`, `DefaultMinPasswordLength` | Constants |
| `auth.TokenRatePerSec`, `TokenBurst`, `TokenIdleTTL` | `gateway_token` rate-limit constants |
| `economy.RPCRewardKill`, `RPCRewardKills`, `RPCSubmitKill`, `RPCGetLeaderboard` | RPC id constants |
| `economy.RewardKillRPC`, `RewardKillsRPC`, `SubmitKillRPC`, `GetLeaderboardRPC` | RPC handlers |
| `economy.ErrServerOnly` | Code `7` error returned to client sessions by the mutation RPCs |
| `economy.SetupLeaderboards(ctx, logger, nk, games)` | Creates `kills_alltime` and `<game>.kills_alltime` for each non-default game in `games` (authoritative), or fails init if an existing board is not |
| `economy.CurrencyGold` | `"gold"`, the unscoped wallet currency key (scoped before every wallet access) |
| `economy.LeaderboardKillsAllTime`, `LeaderboardMigrateEnv`, `GoldPerKill`, `MaxKillsPerBatch`, `CodeKillsOutOfRange`, `ReceiptCollection`, `StatusGranted`, `StatusPartial` | Constants |
| `social.RPCPartyCreate`, `RPCPartyJoin`, `RPCPartyLeave`, `RPCPartyGet` | RPC id constants |
| `social.PartyCreateRPC`, `PartyJoinRPC`, `PartyLeaveRPC`, `PartyGetRPC` | RPC handlers |
| `social.CreateParty` / `JoinParty` / `LeaveParty` / `GetParty` | Party logic against a narrow storage interface (unit-testable without Nakama); each takes the resolved `game` after `nk` |
| `social.Party` / `Party.IsMember(userID)` | Stored party record; `IsMember` is the gateway's dungeon-entry check |
| `social.PartyRequest` / `PartyResponse` | RPC payload types |
| `social.ErrPartyFull`, `ErrAlreadyInParty`, `ErrNotInParty`, `ErrPartyNotFound`, `ErrPartyBusy`, `ErrPartyIDRequired`, `ErrUnauthenticated`, `ErrInvalidPayload`, `ErrRateLimited`, `ErrInternal` | Client-facing runtime errors |
| `social.MaxPartyMembers`, `PartyCollection`, `MembershipCollection`, `MembershipKey` | Constants |
| `social.PartyWriteRatePerSec`, `PartyWriteBurst`, `PartyWriteIdleTTL` | Party mutation rate-limit constants |
| `character.RPCCharacterList`, `RPCCharacterCreate`, `RPCCharacterDelete` | RPC id constants (ADR-31) |
| `character.ListRPC`, `CreateRPC`, `DeleteRPC` | RPC handlers |
| `character.List` / `Create` / `Delete` / `Owns` / `ReadRoster` / `ValidateName` | Roster logic against `character.Store` (StorageRead + StorageWrite); all but `ValidateName` take the resolved `game` after `nk` |
| `character.Character`, `Roster`, `CreateRequest`, `DeleteRequest`, `ListResponse`, `CreateResponse`, `DeleteResponse` | Stored record and payload types |
| `character.MaxCharacterSlots`, `NameMinLength`, `NameMaxLength`, `NamePatternPlaceholder` | **Placeholder** creation limits |
| `character.Collection`, `RosterKey` | Storage location (`Collection` unscoped; resolved with `gamectx.Scoped`) |
| `character.ErrCharacterNotFound`, `ErrRosterFull`, `ErrSlotTaken`, `ErrInvalidName`, `ErrInvalidSlot`, `ErrCharacterIDRequired`, `ErrRosterBusy`, `ErrUnauthenticated`, `ErrInvalidPayload`, `ErrRateLimited`, `ErrInternal` | Client-facing runtime errors |
| `character.RosterWriteRatePerSec`, `RosterWriteBurst`, `RosterReadRatePerSec`, `RosterReadBurst`, `RosterIdleTTL` | Roster rate-limit constants |

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
