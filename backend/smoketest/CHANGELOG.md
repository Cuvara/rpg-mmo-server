# Changelog — Smoketest Module

All notable changes to this module will be documented in this file.
Format based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Fixed
- **`gamestate_reload` could fail a deploy that did everything right.** It compared
  the reloaded spawn against the row snapshot `gamestate_player_row` took a step
  earlier. That earlier read only has to prove the write path works, so it accepts
  any `0 < x < maxX` — including a periodic 30s save caught mid-walk. The eviction
  save that follows the hold expiry then overwrites it with the final position, so
  the value the server correctly reloads is *newer* than the recorded one.
  - Seen on the 2026-08-11 dev deploy: the step recorded `x=3.0000`, the server
    reloaded `x=3.3333`, the check called it a mismatch, and PostgreSQL held
    `x=3.3333` with `updated_at` **33 s after** the recorded read. Nothing was
    broken except the comparison.
  - The reload check now re-reads `player_states` at comparison time and asserts
    against that, which is what it always claimed to verify — "the persisted row is
    actually READ BACK". It falls back to the recorded value if the re-read finds
    no row, so a genuinely missing row still fails.
  - This could also have passed for the wrong reason: a stale recording that happens
    to match an unrelated spawn proves nothing about the reload path.

### Added
- **Persistence checks.** The smoke flow proved the wire and touched no database;
  it now asserts that both stores actually hold what the run produced. Five new
  steps:
  - `nakama_account` — `GET /v2/account` asserts the device login created a durable
    account whose id matches the one `gateway_token` issued a JWT for, with our
    device id linked to it.
  - `nakama_profile` — `POST /v2/storage` asserts the plugin's `AfterAuthenticate`
    hook wrote `collection=player key=profile` owned by the player, with
    `level == StartingLevel`. Nakama answers **HTTP 200 with an empty object list**
    when the record is absent, so the emptiness check — not the status code — is
    what catches a Nakama running without the Go plugin loaded.
  - `gamestate_migrations` — asserts `schema_migrations` is non-empty, gap-free,
    checksummed and at the version the binary expects (`--expect-migration-version`,
    default 1). Bump that default in the same commit that adds a migration.
  - `gamestate_player_row` — polls `player_states` until the row for this run's user
    appears, then asserts map, position, HP and freshness. Polls rather than sleeps:
    the game server only writes on the `AsyncSaver` sweep (30s) or when the reconnect
    hold expires (another 30s), so the arrival time spans a ~60s window.
  - `gamestate_reload` — waits out the reconnect hold so the entity is evicted from
    memory, rejoins, and asserts the server respawns the player at the *persisted*
    position instead of the origin. This is the only check that proves the saved row
    is read back; inside the hold window a reconnect reattaches to the in-memory
    entity and would prove nothing.

  The two Nakama checks use the public HTTP API (works against a remote VPS, needs
  no credential the run did not already have) and always run — they add ~5ms. The
  three game-state checks need direct SQL, because nothing exposes `player_states`
  over HTTP; they are **skipped loudly** when `GAME_DB_URL` is unset and add ~35s
  when it is set. CD needs no change: `deploy/.env` already carries `GAME_DB_URL`
  and the post-deploy smoke step sources it.
- `SKIP` step status, rendered distinctly from `PASS` so an unconfigured run can
  never read as a verified one, and `--require-db` to turn skips into failures.
- Flags/env: `--device-id`/`SMOKE_DEVICE_ID`, `--game-db-url`/`GAME_DB_URL`,
  `--skip-db`, `--require-db`, `--expect-migration-version`, `--db-poll-timeout`,
  `--db-poll-interval`, `--hold-ttl`.

### Changed
- `gameserver_join` now merges the snapshot stream through `messages.SnapshotState`
  instead of reading entities out of one snapshot. Snapshots are delta-encoded — only
  the join keyframe and every 30th snapshot carry full state — so scanning a single
  snapshot for the player would usually find nothing. The step result now also
  reports `keyframes`, `deltas` and `ack_tick`; those are reported, not asserted, so
  the smoke test stays green against a server predating the delta protocol (which
  simply looks like an all-keyframe stream).
- `gameserver_join` position assertion follows the new server-authoritative
  movement model: `move_x`/`move_y` are a direction integrated as
  `direction * speed * dt` per tick, so N inputs no longer put the player at
  X≈N. The step now requires `0 < final_x < inputs` — the upper bound is a
  regression guard against `move_x` being treated as a raw displacement again.
  Snapshot draining stops once the buffered snapshots are consumed so `final_x`
  reports the newest authoritative position (expected ≈ `inputs * speed /
  tickRate`, i.e. ≈3.33 with the default 10 inputs, 5 u/s, 15Hz).
- Game server migrated from Go to C# .NET 10 (`backend/gameserver-dotnet/`).
  Smoke test unchanged — the `gameserver_join` step connects to whatever address
  the `EnterWorldResponse` returns, same wire protocol.

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
