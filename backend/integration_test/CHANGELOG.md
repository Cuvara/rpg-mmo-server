# Changelog — Integration Test Module

All notable changes to the E2E integration test suite will be documented in this file.
Format based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Changed
- Replaced Go gameserver integration tests with dotnet interop tests following
  the gameserver migration from Go to C# .NET 10.

### Added
- `dotnet_interop_test.go` — Go Gateway ↔ C# GameServer cross-language E2E tests
  (build tag `integration`). Seven tests covering: full client flow through gateway
  to C# server (auth → enter world → join → input → snapshot), invalid JWT
  rejection, wrong server ID rejection, multiple concurrent clients, client
  disconnect handling, gateway auth validation, and wire protocol JSON compatibility.

### Added
- `transport_flow_test.go` — `TestFullFlow_TransportMatrix` runs the full
  client -> gateway -> gameserver flow over all four per-hop transport
  combinations (kcp/kcp, tcp/kcp, kcp/tcp, tcp/tcp), asserting the registry
  carries the game server's transport, that `EnterWorldResponse.Transport`
  announces it, and that the client can complete auth, join, input and
  snapshots after dialing what was announced.
- `TestEnterWorld_LegacyRegistryEntryIsTCP` — a registry entry with no
  transport field makes the gateway omit the response field, which every
  client reads as TCP.

### Changed
- `MockClient` can now dial any transport: `NewMockClientTransport(kind, addr)`.
  `NewMockClient(addr)` is unchanged and still means TCP.


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
