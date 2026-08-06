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
