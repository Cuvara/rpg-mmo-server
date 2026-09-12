# Nakama Module — Design Decisions

## 2026-08-04 — Auth: realtime token, profile bootstrap, credential validation

### Two token systems, on purpose

Nakama issues its own session token for the **meta** channel (HTTPS/WebSocket:
economy, social, leaderboard). The **realtime** channel (Gateway → game servers)
uses a separate, short-lived token issued by the `gateway_token` RPC.

Rationale:

- The Gateway is a custom Go service; making it validate Nakama session tokens
  would either couple it to Nakama internals or force a network roundtrip per
  connection. Neither is acceptable on the realtime path.
- A separate token keeps the blast radius small: leaking a realtime token grants
  only world entry until `exp`, never economy or account operations.

### Reuse `shared/jwt` rather than a JWT library

`auth/token.go` signs via `shared/jwt.SignWithServer` — the exact function whose
`Verify` counterpart the Gateway calls in `gateway/session.VerifyClientJWT`.

- One implementation ⇒ no claim-name or algorithm drift between producer and
  consumer. The claims are `sub` (user id), `sid` (optional server id), `iat`,
  `exp`; `jwt.Verify` enforces the HMAC and the expiry.
- No third-party JWT dependency inside the Nakama plugin. Go plugins must match
  the host binary's dependency graph exactly, so every extra dependency is a
  version-skew hazard at load time.
- Trade-off: HS256 with a shared symmetric secret means any holder of the secret
  can mint tokens. Acceptable because both Nakama and the Gateway are
  first-party services on a private network. Migrating to RS256/ES256 (Nakama
  signs, Gateway verifies with a public key) is a `shared/jwt` change only —
  neither service's business logic moves.

### Token TTL from `shared/constants.SessionTTL` (1h)

The realtime token's lifetime is the session TTL the Gateway already uses for
its Redis session entries, so a token cannot outlive the session record it maps
to. Clients call `gateway_token` again when re-entering the world; no refresh
flow is needed at MVP scale.

### Config: runtime env first, process env fallback

Nakama injects configured env vars into the request context under
`runtime.RUNTIME_CTX_ENV`, which is the idiomatic source inside a plugin. But
plain `os.Getenv` still works in the container, and the shared loader gives the
same defaults every other module uses. `LoadConfig` therefore layers
`RUNTIME_CTX_ENV` over `shared/config.Load()` — a single knob (`JWT_SECRET`)
with identical semantics across Nakama, Gateway, and GameServer.

### Profile storage schema

| Property | Value | Why |
|----------|-------|-----|
| Collection / key | `player` / `profile` | One record per user; collection reserved for further per-player records (`settings`, `stats`) without a schema change |
| Owner | the user | Nakama's ownership model handles per-user isolation |
| Permission read | `2` (public) | `display_name` and `level` must be visible to other players (party UI, leaderboards) |
| Permission write | `0` (no client write) | Server-authoritative: only plugin/RPC code may mutate progression |
| Fields | `level`, `created_at`, `display_name` | Minimum viable profile; additive JSON fields stay backwards compatible |

`EnsureProfile` is read-then-write and reports whether it created a record, so
the after-auth hooks are idempotent — repeat logins perform one read and no
write. This is deliberately not a blind upsert: an upsert on every login would
reset progression if a field were ever omitted.

The hook never fails a login for a missing user ID (logged at WARN instead); a
storage error *is* propagated, because silently having no profile would break
downstream gameplay code in harder-to-debug ways.

### Validation in `BeforeAuthenticateEmail`

Rejecting malformed emails and short passwords before Nakama touches the
database saves a DB roundtrip and returns a precise error code. Errors are
`runtime.NewError` with gRPC codes (3 `InvalidArgument`, 16 `Unauthenticated`,
13 `Internal`) so clients get proper HTTP/gRPC statuses instead of a generic
500. `mail.ParseAddress` is tightened by requiring the parsed address to equal
the input (rejects `Name <a@b.c>` display-name form) and requiring a dotted
domain (rejects `user@localhost`). Minimum password length is 8, matching
Nakama's own default.

### Testability

Nakama's `runtime.NakamaModule` is a very large interface. Instead of mocking it
wholesale, `profile.go` declares a two-method `profileStore` interface
(`StorageRead`/`StorageWrite`) that `runtime.NakamaModule` satisfies
structurally. Unit tests drive `EnsureProfile` against a small in-memory fake,
and the hooks are tested with a struct embedding `runtime.NakamaModule` (nil)
plus that fake — any unexpected call panics, which is the desired signal.


---

## 2026-08-06 — `gateway_token` rate limiting

Nakama's Go runtime ships no rate limiter, so `auth/ratelimit.go` wires
`shared/ratelimit` to the `gateway_token` RPC.

**Keyed by user id, checked first.** The caller is already authenticated when
the handler runs, so the user id is available and is the right key — an IP key
would collapse everyone behind one carrier NAT into a single bucket. The check
runs before the payload is even parsed, so a rejected call does no work.

**Why limit a cheap RPC at all.** One HMAC costs nothing; CPU is not the
concern. The token *is*. An unbounded `gateway_token` loop is a free oracle for
minting valid realtime credentials, which is exactly the raw material for a
connection flood against the gateway. Limiting issuance is what makes the
gateway's own per-IP limit meaningful.

**0.2/s, burst 5.** A legitimate client calls this once per realtime
connection: at login, and again after a disconnect. Burst 5 absorbs a flapping
mobile link reconnecting a few times in a row; one call per 5s sustained is far
above any real client and far below a scripted loop.

**Package-level singleton.** Nakama registers RPCs as plain functions with
nowhere to hang per-plugin state, and `InitModule` runs once per process — so a
package var has exactly the lifetime we want. `TokenIdleTTL` (10m) must exceed
the bucket's own full-refill time (`TokenBurst/TokenRatePerSec` = 25s), or
eviction would hand an attacker a free reset; `TestTokenIdleTTLExceedsRefill`
pins that invariant.

### ⚠️ Multi-instance caveat

The buckets live in **this process's memory**. With N Nakama instances behind a
load balancer, one user gets N x the limit, because nothing synchronises the
instances. Accepted for the MVP — the deployment tiers in the root `CLAUDE.md`
run a single Nakama instance up to Soft Launch. The production upgrade is a
Redis-backed counter (`INCR` + `EXPIRE` on
`ratelimit:gateway_token:{user_id}`), against the Redis the gateway already
depends on. Tracked in ADR-8.

## 2026-09-07 — Economy RPCs are server-only; kills leaderboard is authoritative

### The boundary a comment did not enforce

`reward_kill`, `reward_kills` and `submit_kill` take the *beneficiary* user id
from the payload, because the game server grants on a player's behalf. Nakama
registers every RPC for both authenticated client sessions and
`runtime.http_key` callers, so until now any logged-in client could call
`reward_kills` with `{"user_id": "<anyone>", "kills": 1000}` — the "internal
RPC" note in `CLAUDE.md` was documentation, not enforcement (audit F01, P0).

The two caller kinds are distinguishable only through the request context: a
client session carries `RUNTIME_CTX_USER_ID`, `RUNTIME_CTX_SESSION_ID` and
`RUNTIME_CTX_USER_SESSION_EXP`; an `http_key` call carries none of them
([Nakama runtime introduction](https://heroiclabs.com/docs/nakama/server-framework/introduction/)).
`economy.requireServerCaller(ctx)` rejects if **any** of the three is set —
not just the user id — so a future Nakama that populates them differently
still fails closed. It runs before `json.Unmarshal`, so a client learns nothing
about the schema and no `WalletUpdate`/`LeaderboardRecordWrite` can be reached;
the tests assert zero mock calls after rejection. The error is gRPC code 7
(`PERMISSION_DENIED` → HTTP 403), distinct from the 16 the auth RPCs use for
"you need a session": here having a session is the problem.

`get_leaderboard` is a read and stays unguarded. `gateway_token` is the
opposite case (client-only, requires a session) and is untouched. The auth
hooks (`AfterAuthenticate*`) write storage but are hooks, not RPCs — Nakama
invokes them, clients cannot — so they need no guard.

Why not check `http_key` itself: the runtime never exposes it to the handler,
and Nakama already verified it before dispatch. Absence of a session *is* the
server credential path.

### Authoritative leaderboard, and why the migration is explicit

`kills_alltime` was created with `authoritative=false`, a second score path
that bypasses the RPCs entirely via Nakama's public `WriteLeaderboardRecord`
(audit F02, P1). It is now `true`. Nakama's `LeaderboardCreate` is idempotent
and leaves an existing board's flags untouched, so flipping the argument
migrates nothing on a running deployment. `SetupLeaderboards` therefore looks
the board up first (`LeaderboardsGetId`) and handles the legacy case
deliberately:

- default: **fail InitModule** with the exact fix in the error. Deleting a
  board destroys its records, and a start-up hook must not decide that on its
  own; silently continuing would leave a P1 hole open with a log line nobody
  reads. The data-preserving fix is a one-row SQL update plus restart
  (`docs/RUNBOOK.md`), because Nakama offers no in-place flag change through
  the runtime or Console.
- `LEADERBOARD_MIGRATE=recreate`: delete + recreate, opt-in, for environments
  whose scores are disposable.

Rollback to an older plugin build is safe against an authoritative board.

## 2026-09-07 — `reward_kills` is exactly-once per batch id

### The problem

`batch_id` was recorded in the wallet metadata and nothing else. The game
server minted a fresh GUID per send and dropped a batch whose answer never
arrived, because with no deduplication the only alternative to *maybe lost*
was *maybe doubled* (audit F06). Both were wrong: a timeout after Nakama
committed lost nothing but the sender believed it had; a timeout before commit
lost the gold for real; and a retry under a new id after a post-commit
connection reset doubled it. Separately, a backlog that grew past
`MaxKillsPerBatch` during an outage was rejected forever (F07).

### Receipt in the same transaction as the grant

The idempotency record is a Nakama storage object — collection
`reward_receipts`, key `batch_id`, owner the rewarded user — written
**create-only** (`Version: "*"`) in the same `nk.MultiUpdate` as the
`WalletUpdate`. Nakama runs a `MultiUpdate` inside one SQL transaction, so
either the gold and the receipt both exist or neither does. That single fact
carries the whole contract:

- a resent id finds its receipt and is replayed — no wallet write;
- two duplicates racing past the lookup both enter `MultiUpdate`; the second
  fails the version check and *its whole transaction rolls back*, wallet
  included, then it is answered as a replay;
- a failed `MultiUpdate` leaves no receipt, so the error really does mean
  "nothing granted" and the resend is granted fresh.

Why storage-plus-`MultiUpdate` and not a dedupe marker written separately: a
marker committed before the wallet loses the gold if the process dies between
the two; a marker committed after it doubles the gold on the same crash. Only
the one-transaction form closes that window, and `MultiUpdate` is the only
supported API that spans storage and wallet in one commit. Why not the wallet
ledger: it is append-only and not queryable by metadata through the runtime.

`batch_id` is therefore **required** now (code 3 if empty). The only caller is
our game server, which always sent one.

### The leaderboard stays outside the transaction

`MultiUpdate` does not cover leaderboards, so the score increment runs after
the commit. Its failure is reported as `status: partial`, never as an error —
an error would invite a wallet retry — and the receipt records
`leaderboard_done: false`. A replay of the same id then retries **only** the
leaderboard and flips the flag on success. Score converges without a second
gold grant. The one residual double-fault (score landed, receipt update
failed, batch replayed) increments the score once more; it is logged and is
the bounded score drift ADR-6 accepts.

### Splitting is the caller's job, but the refusal is machine-readable

`MaxKillsPerBatch` stays at 1000 as the abuse bound. Rejection now uses gRPC
code 11 (`OUT_OF_RANGE`, `CodeKillsOutOfRange`) instead of the generic 3, so
the game server can tell "split this" from "malformed" without parsing the
message. A refused batch never reached the wallet, so the halves may take new
ids.

### What is deliberately not here

- No receipt pruning. One row per granted batch; see the retention note in
  `docs/API.md`. A sweep keyed on `granted_at` is the natural follow-up.
- No sender-side durability. Kills the game server recorded but had not yet
  been acknowledged when it died are gone; that is the game server's window
  to document (`gameserver-dotnet/docs/DESIGN.md`), not Nakama's to close.
