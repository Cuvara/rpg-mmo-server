# Changelog — Integration Test Module

All notable changes to the E2E integration test suite will be documented in this file.
Format based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Added
- `redis_flow_test.go` — E2E coverage of the full core flow against a shared
  Redis (miniredis), plus reusable helpers (`startGameServer`, `authAndEnterWorld`,
  `joinGameServer`, `awaitEntity`, deadline-based `waitFor` / `waitAddr`).
- `TestRedisBackedFullFlow` — gateway + two game servers, each constructing its
  own `redisstore` clients against one Redis instance (no shared in-memory
  structs): auth → enter world → registry lookup of a self-registered server →
  join token → input → snapshot, and player-count propagation back through Redis.
- `TestSidEnforcement_CrossServer` — a join token minted for game server A is
  accepted by A and rejected by B (distinct server ids, same Redis).
- `TestReconnect_HoldWindow` — TCP drop puts the entity in the reconnect hold
  window (`ServerOpts.HoldTTL` overridden to 5s); reconnect inside the window
  reattaches to the same entity with position/HP preserved. The persisted record
  is deleted mid-hold so a store reload cannot mask the assertion.
- `TestCrossServerEvent_DeathRelay` — a boss kill on a game server publishes a
  `boss_killed` event that reaches the gateway's `events.Relay` sink through
  Redis Streams (consumer group + ACK), payload asserted end to end.
- `TestNakamaTokenCompat` / `TestNakamaToken_AcceptedByGameServer` — tokens
  issued by `nakama/auth.IssueGatewayToken` verify with the gateway's
  `session.VerifyClientJWT` and the join-token path, and are accepted by a live
  game server handshake; a wrong secret is rejected.

### Changed
- `go.mod`: added `github.com/alicebob/miniredis/v2`, `github.com/redis/go-redis/v9`
  and `github.com/duycuong/rpg-mmo/nakama` (with a `replace` to `../nakama`).

### Notes
- Hold-window *expiry* is not asserted here (it would mean waiting out the
  timer); it is covered by gameserver unit tests.
- The game server publishes to `gameserver/events.GameStream`
  (`events:game`) while the gateway binary's relay defaults to
  `gateway/events.DefaultStream` (`global`); the E2E test wires the relay to the
  publisher's stream name so delivery is exercised. The default wiring in the
  two `cmd/` binaries does not line up.
