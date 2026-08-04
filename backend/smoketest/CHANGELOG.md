# Changelog — Smoketest Module

All notable changes to this module will be documented in this file.
Format based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Added
- `TRANSPORT` env / `--transport` flag selects the transport for the gateway
  hop (`tcp` or `kcp`, default `tcp` — CD smoke is unchanged). The game server
  hop always follows `EnterWorldResponse.Transport`, so mixed deployments work
  with no extra configuration. The `gateway_auth` step detail now reports both.

### Changed
- The clean `MsgDisconnect` is followed by a short pause before closing: KCP
  flushes on its 10ms update tick and `Close()` does not drain pending output,
  so without it the server would only notice the disconnect when the reconnect
  hold expired.


### Added
- New module `github.com/duycuong/rpg-mmo/smoketest` — post-deploy smoke test
  binary (`cmd/smoketest`) covering the full flow: Nakama healthcheck → device
  auth (random device id) → `gateway_token` RPC with local JWT verification
  (`shared/jwt` + `JWT_SECRET`, `sub` must match the Nakama user id) → gateway
  `MsgAuth` + `MsgEnterWorld` → game server `MsgJoinToken` → ~10 `MsgInput` at
  100 ms asserting ≥ 5 `MsgSnapshot` and the expected position delta → clean
  `MsgDisconnect`.
- Per-step PASS/FAIL + latency summary and machine-readable final line
  `SMOKE=PASS|FAIL`; non-zero exit on any failure; per-operation timeouts so CI
  can never hang.
- All endpoints configurable via env (`NAKAMA_URL`, `NAKAMA_SERVER_KEY`,
  `GATEWAY_ADDR`, `JWT_SECRET`, `SMOKE_MAP_ID`, `SMOKE_TIMEOUT`) with CLI flag
  overrides.
- Table-driven unit tests for the pure helpers (env/config parsing, dial-addr
  normalization, result formatting).
