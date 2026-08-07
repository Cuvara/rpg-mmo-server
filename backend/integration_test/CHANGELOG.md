# Changelog — Integration Test Module

All notable changes to the E2E integration test suite will be documented in this file.
Format based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Added
- `TestDotnetInterop_MixedEncodingsOnOneServer` — a JSON client and a Protobuf
  client joined to the **same running server**, which is the state a fleet is
  actually in mid-rollout. It asserts the server answers each client in the
  encoding it was addressed in, and measures the keyframe payload saving on the
  real wire so the bandwidth claim is checked rather than trusted. This test is
  what caught the game server dropping the handshake's encoding and falling back
  to JSON for every reply.

### Changed
- `TestDotnetInterop_FullFlow` now runs the whole handshake **twice, once per
  encoding**. It is the only test that can prove the two independently generated
  implementations of `wire.proto` agree — `protoc-gen-go` on the Go side,
  `protoc`'s C# generator on the server. Unit tests on either side can only prove
  each is self-consistent.

### Fixed
- **This suite had never actually run.** Every test sits behind `//go:build integration`,
  but `ci.yml` and `cd.yml` invoked it without `-tags integration`, so the package
  compiled to zero tests and both pipelines printed a green
  `?   github.com/duycuong/rpg-mmo/integration_test  [no test files]`. Both workflows
  now pass `-tags integration` (and `vet_flags: -tags integration`, plus a
  `needs_dotnet` setup-dotnet step, since the suite builds and runs the C# server).
- `go.mod` / `go.sum` were stale: `go vet -tags integration ./...` failed with
  `go: updates to go.mod needed; to update it: go mod tidy`. The tagged sources pull in
  the gateway's Prometheus dependencies, which were never recorded because nothing ever
  built them. Tidied. `go vet` in the reusable workflow previously ran without the tag,
  which is precisely why this stayed invisible.
- **The suite failed as a package even with every test passing.**
  `startDotnetGameServer` launched the server with `dotnet run`, which spawns it as a
  *grandchild* holding the inherited stdout/stderr. `cmd.Process.Kill()` killed only
  the `dotnet run` wrapper, so the real server survived with the pipe open and `go test`
  ended in `*** Test I/O incomplete 30s after exiting` / `FAIL`. The server is now
  launched as `dotnet GameServer.dll` — a direct child that Kill actually reaps. (The
  native apphost is deliberately not used: it needs `DOTNET_ROOT` and dies with
  "You must install .NET to run this application" wherever the SDK is non-default.)

### Changed
- The C# server is built **once** per test binary (`sync.Once`) instead of once per
  test, and the log-scanner goroutine is now joined during cleanup so nothing can
  `t.Logf` after its test returned. Suite wall-clock: 69s (FAIL) → 7.6s (ok).
- Test servers start with `--metrics-addr ""`. The default `:9101` is a fixed global
  port: it collided with any locally running server and between consecutive tests.
- Startup failures fail fast with the server's own log instead of stalling for the
  full timeout, and the startup deadline is 60s (cold runners build slowly).
- Snapshot assertions now merge the delta stream via the new `mergeSnapshots` helper
  (`messages.SnapshotState`) instead of inspecting a single snapshot: with delta
  encoding, one snapshot is not the world. The full-flow and wire-compat tests
  additionally assert `ack_tick` matches the input tick that was sent, and every
  merge asserts the connection opened with a keyframe.
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
