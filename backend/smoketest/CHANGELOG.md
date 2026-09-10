# Changelog — Smoketest Module

All notable changes to this module will be documented in this file.
Format based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Fixed
- **A fifth `NewEnvelope` call site that `-encoding` did not reach.** `smoke/db.go`'s
  disconnect at the end of the reload check was still hardcoded to JSON, so a
  `-encoding proto` run against a `require` server would have had that one connection
  refused. It survived because the change that introduced `-encoding` asserted "no
  `NewEnvelope` call survived" against **`runner.go` only** — the file that happened to be
  open — and the live run that proved the feature used `-skip-db`, which is the one mode
  that never executes this path.

  Found by `send-budget`, who did the same conversion independently and searched the whole
  module. The scan is now module-wide and returns zero.

### Added
- **`-sealed` as a flag, and the impossible combination refused in the binary.**
  `-sealed -encoding json` is rejected at startup rather than left to fail at the join,
  where the symptom is a join that is *accepted* and a connection that then closes — so a
  log reading "join accepted" is not evidence the client works.

  It does not silently upgrade the encoding: someone who wrote that combination believes
  one of the two things about their run, and choosing the other for them hides which. The
  guard is in the binary and not only in `stack.sh`, because a wrapper can be bypassed and
  the binary is what CD runs.

  Both this and the documentation below are `send-budget`'s design, taken from #300.

### Added
- **`-encoding` / `SMOKE_ENCODING`: the smoke test can send protobuf, so it can check a
  sealed stack.** Defaults to `json`, so every existing run is byte-identical and CD proves
  exactly what it proved before.

  **Why this was thought to be hard, and was not.** A `require` game server refused this
  client at the join with `encoding_cannot_seal`, which was diagnosed — in this changelog,
  in `ROADMAP-SECURITY.md`, and in a `stack.sh` comment — as "it hand-rolls `encoding/json`
  with no encoding switch, and that independence is much of its value; protobuf is a
  decision, not a chore". That was wrong. It calls `messages.NewEnvelope`, and `codec.go`
  defines that as `NewEnvelopeAs(EncodingJSON, ...)` in the **shared** codec the gateway and
  the load generator already use. There was no hand-rolled encoder and no independence to
  lose. Four call sites in `runner.go`.

  Verified live against a `require` server rather than in unit tests: JSON refused
  (`encoding_cannot_seal`), protobuf + sealing **`SMOKE=PASS`** with
  `sealed=true binding_verified=false` over 16 snapshots, protobuf without a hello refused
  (`sealed handshake failed (NoHello)`) — three populations, three distinct reasons in the
  server's own log.

  `binding_verified=false` is correct and expected: this client receives its join token from
  the real gateway and so holds no `JOIN_TOKEN_SECRET`. It behaves exactly like a shipped
  client, which is the point of it.

### Changed
- **An unrecognised encoding is refused at startup, not defaulted.** Empty is *unset* and
  maps to JSON — a `Config` built in code leaves it zero — but a non-empty value that is
  neither `json` nor `proto` is a typo, and those are different things. Silently falling
  back would be invisible against an `off` server and, against a `require` one, would be
  refused at the join in a way that reads as a broken stack rather than a misspelt flag.

### Added

- **`SMOKE_SEALED`: the smoke test can speak a sealed session — but the path is UNREACHABLE
  today, and this entry overstated it when it was written.**

  **Correction (2026-09-10).** Measured live against a `require` server: the sealed arm
  fails at `gameserver_join` with `sealed handshake: read server hello: read length: EOF`,
  and the server log gives the cause — `encryption is required and this client's encoding
  cannot seal (encoding_cannot_seal)`. **The smoke test speaks JSON, and a JSON client can
  never carry a sealed frame.** It hand-rolls `encoding/json` and has no encoding switch at
  all, so it is refused at the join before any of the code below runs.

  The sealing code is not wrong — `RunClientHandshake` is shared with the load generator,
  which does drive it live over protobuf. It is unreachable. The original claim that this
  was "required before the game server's default can flip" was **the wrong way round**:
  the default flipping is what exposes that the smoke test cannot connect at all, and the
  real prerequisite is a smoke test that speaks protobuf.

  That is a design decision rather than a chore: the smoke test's JSON-ness is much of its
  value, because it makes it an *independent* second implementation of the wire rather than
  a consumer of the same generated types the server uses. Vendoring the generated protobuf
  types would cost that independence; hand-rolling protobuf is real work.

  **Consequence, and it is the binding one:** `post-deploy-smoke` and `verify.sh`'s
  `flow.smoke` both run this binary against the stack they just deployed, so **no
  environment can be set to `GAMESERVER_SEALED=require` until this is resolved** — requiring
  encryption currently deprecates the deploy verifier. The original entry follows, unchanged
  except for this note, because what it describes is what the code does once it can connect.
  - **It behaves like a shipped client, because it is the closest thing to one here.** It
    goes through the real gateway and therefore *receives* its join token rather than
    minting one, so it holds no `JOIN_TOKEN_SECRET` and **cannot verify the server's
    binding**. It reads the `jti` with `jwt.ParseUnverified` and completes the handshake
    unverified — confidentiality against a passive eavesdropper, nothing against an active
    one.
  - The step reports **both** facts: `sealed=true binding_verified=false`. Reporting only
    the first would let a reader take confidentiality for authenticity, which is precisely
    the conflation ADR-21 was written about.
  - Only the game-server socket is sealed; sealing the gateway hop with these keys would be
    meaningless, since they derive from a join token the gateway itself issues.

### Added
- **Strict address mode (`SMOKE_STRICT_ADDR` / `--strict-addr`, default off).** The
  runner normalizes listen-style addresses (`:9000`, `0.0.0.0:9000`, `[::]:9200`)
  to loopback before dialing. That is right for host-mode deploys, but it silently
  hides the Agones failure mode it is about to be used to prove: with
  `portPolicy: Dynamic` the game server must learn its scheduler-assigned address
  from the sidecar and register that; if it does not it advertises the hostless
  `:9000`, the gateway forwards it verbatim, and no real client can dial it — while
  the smoke test rewrites it, connects to whatever else listens on port 9000 of the
  local host, and reports PASS.
  - With the flag on, a listen-style `ServerAddr` from `MsgEnterWorldResp` fails the
    `gateway_auth` step with a message naming the address and the likely cause (the
    Agones sidecar status read, or `GAMESERVER_PUBLIC_ADDR`).
  - Applies to the **game-server hop only**. `GATEWAY_ADDR` is operator-supplied
    local config (`:8000` by default), not an advertised address, and keeps the
    rewrite in both modes.
  - Rejects only *listen-style* addresses: a loopback address the server
    deliberately advertised (`127.0.0.1:9000`, plausible under k3d) passes through.
  - Default off, and strict-off is asserted byte-for-byte identical to
    `NormalizeDialAddr` — CD's post-deploy smoke step and host-mode local dev are
    unaffected.

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
