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
| Auth — social (Google/Apple/Facebook) | Planned | — |
| Economy — atomic transactions, wallet, inventory | Planned | — |
| Leaderboard — rankings, season management | Planned | — |
| Social — party, friends, chat, guild, presence | Planned | — |
| Matchmaking, notifications | Planned | — |

## Layout

```
nakama/
  main.go            # InitModule — registers all hooks and RPCs
  auth/
    config.go        # env-driven config (JWT secret, TTL, password policy)
    errors.go        # client-facing runtime errors + gRPC codes
    token.go         # gateway_token RPC + token issuance
    profile.go       # after-auth hooks + EnsureProfile storage logic
    validate.go      # before-auth email hook + credential validation
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

The plugin reads env vars from the Nakama runtime environment
(`runtime.RUNTIME_CTX_ENV`) first and falls back to the process environment via
`shared/config`. Token TTL comes from `shared/constants.SessionTTL` (1h).

See `API.md` for the RPC/hook reference and `DESIGN.md` for the rationale.

## Dependencies

- `github.com/duycuong/rpg-mmo/shared`
- `github.com/heroiclabs/nakama-common` (Nakama Go runtime SDK)
