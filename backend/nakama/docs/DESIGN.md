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
