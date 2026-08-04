# Changelog — Gateway Module

All notable changes to the Gateway module will be documented in this file.
Format based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Added
- Opt-in KCP/UDP listener for the realtime path: `--transport=tcp|kcp`
  (`GATEWAY_TRANSPORT`, default `tcp`) and the `server.WithTransport` option.
  `Gateway.Run` listens through `shared/transport`; handlers are unchanged.
- `EnterWorldResponse.Transport` is now filled from the target game server's
  registry entry, so the client knows which transport to dial for hop 2.
  `transfer.AssignResult` gained the matching `Transport` field. Empty means
  `tcp` — servers registered before the field existed keep working.
- `--allocator-transport` / `ALLOCATOR_TRANSPORT` (defaults to `--transport`):
  the transport stamped onto a ServerInfo synthesized by the Agones allocator,
  since the allocation API reports no transport and the pod has not registered
  itself yet. Must match the fleet manifest's `--transport` argument.
- Real Agones allocator (`registry.AgonesAllocator`): `POST` to the aggregated
  `allocation.agones.dev/v1` `GameServerAllocation` endpoint using `client-go`
  credential resolution (in-cluster ServiceAccount, else `--allocator-kubeconfig`
  / `$KUBECONFIG` / `~/.kube/config`). Fleet selection by
  `agones.dev/fleet` label, `KindMap`/`KindDungeon` mapped from config.
  Wire types are modelled locally instead of importing the Agones clientset —
  see `docs/DESIGN.md` for the rationale.
- `registry.ErrNoCapacity` sentinel for `state: UnAllocated`, plus
  `registry.KindAllocator` / `AllocationRequest` for kind-aware allocation.
- `cmd/gateway` flags `--allocator` (`none`|`agones`), `--allocator-namespace`,
  `--allocator-fleet-map`, `--allocator-fleet-dungeon`, `--allocator-kubeconfig`
  with matching `ALLOCATOR*` env vars. Default stays `none`, so nothing changes
  unless allocation is explicitly enabled.
- Tests: table-driven allocator client tests against an `httptest` fake API server
  (success, `UnAllocated`, API error with `Status.message`, malformed body, missing
  port, timeout, request shape/URL per kind) and a gateway-level enter-world test
  asserting allocation only fires for unserved maps and that the join token's
  `sid` is the allocated GameServer name.

### Changed
- `cmd/gateway` wires the registry with the Agones allocator when
  `--allocator=agones`; a bad Kubernetes config is fatal at boot.

### Fixed
- Cross-server event stream name mismatch: gameserver published to
  "events:game" (double-prefixed to "events:events:game" by the store) while
  the gateway relay subscribed to "global" — events never arrived. Both sides
  now share constants.GameEventStream ("game", store adds the prefix once).

### Added
- Selectable store backends in `cmd/gateway`: `memory` (default) or `redis`
  (`redisstore.SessionStore` / `ServerRegistry` / `EventStream` sharing one client).
  Resolved from `--backend`, `GATEWAY_BACKEND`, or an exported `REDIS_ADDR`
- `--addr` and `--instance-id` flags for `cmd/gateway` (`--instance-id` names the
  event-stream consumer within the `gateway` consumer group)
- Session validation + sliding TTL refresh on every non-auth frame (`checkSession`)
- `MsgDisconnect` handling and session cleanup on socket close
- `session.SessionKey` and `SessionManager.RefreshSession`
- Real `events.Relay` over any `storage.EventStream`, plus `events.Sink`/`SinkFunc`;
  wired into the gateway via `server.WithEventRelay` (started in `Run`, stopped in
  `Shutdown`). Sink logs/counts events — no client fan-out until `shared/messages`
  gains a client-facing event type
- `Gateway.OnEvent`, `Gateway.EventCount`, `Gateway.ConnCount`
- `registry.NewRegistryServiceWithAllocator` and `RegistryService.GetServer`
- Tests: session lifecycle, relay, and registry lookup covered against both the memory
  and Redis (miniredis) backends
- `docs/API.md` and `docs/DESIGN.md`

### Changed
- `RegistryService.FindServer` picks the least-loaded server with spare capacity instead
  of the first match; falls back to the allocator when one is configured
- `server.New` takes variadic `Option`s (existing 4-arg calls unchanged)
- `StubEventRelay.Start` is now a no-op instead of returning `ErrNotImplemented`, so a
  gateway configured without a stream still starts
- `docs/README.md`: run modes, flags, backend selection and env table
- Bump Go version to 1.26 (align with CI and gameserver)

### Fixed
- Sessions were write-only: never read back, never refreshed, never destroyed — they
  leaked until TTL and left ghost-online players in a shared store

### Added (initial)
- Initial module setup with go.mod (`github.com/duycuong/rpg-mmo/gateway`)
- CLAUDE.md agent instructions for Gateway Engineer role
