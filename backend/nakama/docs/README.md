# Nakama Module — Overview

Nakama Go runtime plugin for RPG MMO meta services. The code is compiled as a Go
plugin (`.so`) and loaded by the Nakama server at start-up, which calls
`InitModule` (see `main.go`).

Module path: `github.com/duycuong/rpg-mmo/nakama`
Depends on: `github.com/duycuong/rpg-mmo/shared` (via `replace ../shared`) and
`github.com/heroiclabs/nakama-common`.

## Services

| Service | Status | Where |
|---------|--------|-------|
| Auth — realtime (gateway) token RPC | ✅ | `auth/token.go` |
| Auth — player profile bootstrap on first login | ✅ | `auth/profile.go` |
| Auth — email/password pre-auth validation | ✅ | `auth/validate.go` |
| Auth — multi-game: `GAMES` allow-list hooks on every auth method and refresh, `gid` in `gateway_token` (ADR-33 leg 4) | ✅ | `gamectx/`, `auth/hooks.go`, `auth/token.go` |
| Multi-game data scoping of `character/`, `economy/`, `social/` (ADR-33 leg 4 data) | Planned | task l4-data |
| Auth — social (Google/Apple/Facebook) | Planned | — |
| Economy — atomic transactions, wallet, inventory | Planned | — |
| Leaderboard — `kills_alltime` (authoritative), server-only score writes | Done | season management planned |
| Social — **party** (create/join/leave/get, max 4, storage-backed) | ✅ | `social/party.go` |
| Social — friends, chat, guild, presence | Planned | — |
| Matchmaking, notifications | Planned | — |

## Layout

```
nakama/
  main.go            # InitModule — registers all hooks and RPCs
  auth/
    config.go        # env-driven config (JWT secret, TTL, password policy)
    errors.go        # client-facing runtime errors + gRPC codes
    token.go         # gateway_token RPC + token issuance (cid, gid)
    profile.go       # after-auth hooks + EnsureProfile storage logic
    profile_game.go  # EnsureGameProfile — per-game profile (ADR-33)
    roster_game.go   # per-game roster ownership check (ADR-33)
    hooks.go         # before-auth hooks (every method) + before refresh: GAMES check
    validate.go      # before-auth email hook + credential validation
  gamectx/
    gamectx.go       # GAMES allow-list, GameFromContext/GameFromVars, Scoped (ADR-33)
  economy/
    reward.go        # reward_kill (legacy per-kill)
    reward_batch.go  # reward_kills — batched, exactly-once per batch_id
    caller.go        # requireServerCaller — server-only guard
    leaderboard.go   # kills_alltime setup, submit_kill, get_leaderboard
  social/
    party.go         # party_create / party_join / party_leave / party_get
    errors.go        # client-facing runtime errors + gRPC codes
    ratelimit.go     # per-user bucket for the party mutations
  docs/              # README / API / DESIGN / RUNBOOK
```

## Development

Go 1.26. There is no root `go.work`; `cd` into this module first.

```bash
cd backend/nakama
go build ./...     # plain build (package main produces no artifact here)
go vet ./...
go test ./...      # unit tests, no Nakama server required
```

`auth/refresh_localstack_test.go` carries the `localstack` build tag and needs a
real Nakama; `go test ./...` skips it. How to run it: `RUNBOOK.md` →
*Session refresh keeps the game (local stack)*.

`main()` in `main.go` is an empty stub so that `go build ./...` / `go vet ./...`
succeed for a `package main` that is only ever loaded as a plugin.

> Do **not** build with `-buildmode=plugin` on a normal dev machine: Go plugins
> require cgo and the plugin **must** be built with the exact same Go toolchain,
> build flags, and dependency versions as the Nakama server binary. Use the
> official plugin builder image instead (below).

## Building the plugin `.so`

Use `heroiclabs/nakama-pluginbuilder` matching your Nakama server version:

```bash
# from repo root — shared/ must be inside the build context
docker run --rm -w "/builder" \
  -v "$PWD/backend:/builder" \
  heroiclabs/nakama-pluginbuilder:3.22.0 \
  build -C nakama -buildmode=plugin -trimpath -o ./build/rpgmmo.so ./...
```

The resulting `backend/nakama/build/rpgmmo.so` is mounted into the Nakama
container's module path (default `/nakama/data/modules`).

## Running

```yaml
# docker-compose fragment
nakama:
  image: heroiclabs/nakama:3.22.0
  volumes:
    - ./backend/nakama/build:/nakama/data/modules
  environment:
    - JWT_SECRET=<same secret the Gateway uses>
```

## Configuration

| Env var | Default | Purpose |
|---------|---------|---------|
| `JWT_SECRET` | `dev-secret-change-me` (from `shared/config`) | HS256 secret for realtime tokens. MUST match the Gateway and gameserver-dotnet, otherwise every realtime token is rejected. |
| `GAMES` | absent = `cuvara` only | ADR-33 allow-list of games this Nakama serves, comma-separated (`cuvara,foo`). When set it is **exactly** the listed games — `cuvara` is not implied, so list it to keep serving the existing game. Each entry must match `^[a-z][a-z0-9]{1,23}$`; an invalid entry makes `InitModule` fail at start-up (`configure allowed games: …`). The start-up log prints `allowed games: [...]`. |

The plugin reads env vars from the Nakama runtime environment
(`runtime.RUNTIME_CTX_ENV`) first and falls back to the process environment via
`shared/config`. Token TTL comes from `shared/constants.SessionTTL` (1h).
`GAMES` follows the same rule: the process env is read only when the runtime env
has no `GAMES` key. Set it as a runtime env var:

```bash
nakama --runtime.env "GAMES=cuvara,foo" …
```

or in the Nakama YAML config:

```yaml
runtime:
  env:
    - "GAMES=cuvara,foo"
```

### Client contract: the game is a session variable

A client of a game other than `cuvara` sends its game id in the `vars` of
**every** authenticate call (device, email, custom, social):

```json
{ "id": "<device id>", "vars": { "game": "foo" } }
```

No `vars.game` means `cuvara`, so existing clients need no change. Nakama keeps
the vars in the session and across a refresh that sends no `vars`; the plugin
reads the game from the session on every call, and `gateway_token` mints it as
the `gid` claim. A client never changes game mid-session: a payload `game_id`
that differs from the session game is refused (code 7). See `API.md` →
*Game identity*.

See `API.md` for the RPC/hook reference and `DESIGN.md` for the rationale.

## Dependencies

- `github.com/duycuong/rpg-mmo/shared`
- `github.com/heroiclabs/nakama-common` (Nakama Go runtime SDK)
