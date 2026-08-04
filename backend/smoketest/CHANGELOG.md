# Changelog — Smoketest Module

All notable changes to this module will be documented in this file.
Format based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

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
