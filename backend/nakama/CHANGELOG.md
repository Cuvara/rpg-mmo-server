# Changelog — Nakama Module

All notable changes to the Nakama module will be documented in this file.
Format based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

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
