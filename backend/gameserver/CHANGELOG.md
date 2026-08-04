# Changelog — GameServer Module

All notable changes to the GameServer module will be documented in this file.
Format based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Fixed
- Data race between the tick loop, the persistence saver, and reconnect
  reattach (caught by `-race` in CI): entity field access is now disciplined
  through `World.Update` (write lock) / `World.View` (read lock); AOI queries
  and `PlayerStates` return value copies taken under the lock.

### Added
- Reconnect hold window: a disconnect now holds the player entity for
  `constants.EntityHoldTTL` (30s, map) or `constants.DungeonHoldTTL` (60s, `--mode=dungeon`)
  instead of removing it. A rejoin inside the window reattaches to the live entity with
  position/HP/cooldowns preserved; expiry triggers the final save then removal.
  `ServerOpts.HoldTTL` overrides the window; `Server.HeldCount()` exposes pending holds.
- Registry heartbeat goroutine at `constants.ServerHeartbeatTTL / 3`, with automatic
  re-registration if the entry has expired. `ServerOpts.HeartbeatInterval` overrides it.
- `--redis` / `--redis-addr` flags on `cmd/gameserver`: selects
  `redisstore.ServerRegistry` + `redisstore.EventStream` over the in-memory defaults
  (`REDIS_ADDR` / `REDIS_PASSWORD` from `shared/config`).
- Death events: `input.Handler.SetDeathHandler` hook wired into `Server.onEntityDeath`,
  publishing `player_death` / `boss_killed` (JSON `events.DeathPayload`) to the
  `events:game` stream via the previously unused `events.Publisher`.
- Input tick acknowledgement (server-side): `InputMessage.Tick` is recorded monotonically
  on `game.Entity.LastInputTick`, readable via `World.LastInputTick(userID)`.
- `ServerOpts.Mode` so the binary's `--mode` actually changes behavior (hold window).
- Tests: `server/server_test.go` (sid enforcement, registry lifecycle, reconnect within
  window, hold expiry + final save, death event publishing) and
  `input/tick_ack_test.go` (tick tracking, death hook).
- Initial module setup with go.mod (`github.com/duycuong/rpg-mmo/gameserver`)
- CLAUDE.md agent instructions for GameServer Engineer role

### Changed
- `Server.Shutdown()` is now idempotent (`sync.Once`), cancels pending reconnect holds,
  stops the heartbeat loop, and logs deregistration errors.
- Registry registration moved into `Server.register()` and reports the real listener
  address and live connection count.

### Fixed
- Join tokens are now bound to a server: a token whose `sid` claim names a different
  `--server-id` is rejected with `join token not valid for this server`. Tokens with an
  empty `sid` are still accepted and logged at WARN (see `docs/DESIGN.md`).

### Security
- Closes the join-token replay hole where one token was valid on every game server that
  shared the JWT secret.
