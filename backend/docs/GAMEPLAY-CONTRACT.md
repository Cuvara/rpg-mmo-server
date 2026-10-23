# Core gameplay developer contract

**Audience:** a developer or agent about to implement a new core gameplay feature: an ability, an
enemy type, an item effect, a movement mode. This document states what must stay true and how to
prove it. It does not define gameplay numbers. Every number below is a code or config default,
quoted with its source. Changing one is a gameplay decision for the user, not an implementation
detail (see [#467](https://github.com/Cuvara/rpg-mmo-server/issues/467): every gameplay number is a
placeholder).

**Status:** refreshed 2026-10-23 against merged server develop `7fbdcf2` and Netcode v0.48.0
(`51b083e`). UnityDots #37 and client pin updates remain pending their respective PRs.

## How to read the citations

Each citation is `<commit>:<path>:<line>`. The commit is the one the statement describes. Check a
citation with `git show <sha>:<path> | sed -n '<line>p'` in the named repository.

| Label | Repository | Commit | What it is |
|---|---|---|---|
| `develop` | rpg-mmo-server | `7fbdcf2` | merged `origin/develop` |
| `#472` | rpg-mmo-server | `7fbdcf2` | merged into develop: enemy separation, spawn scatter, enemy budget, testlab support |
| `#474` | rpg-mmo-server | `7fbdcf2` | merged into develop: snapshot delivery trace, KCP counters, #471 fix, image revision label |
| `#475` | rpg-mmo-server | `7fbdcf2` | merged into develop: debug kill hook, per-reason combat rejections, map bounds on `/status` |
| `#482` | rpg-mmo-server | `7fbdcf2` | merged into develop: a graceful leave releases the player at once |
| `integ` | rpg-mmo-server | `0b7a51a` | Historical readiness test tree containing #472 + #474 + #475 + #482 |
| `nc-0.48.0` | Netcode | `51b083e` (tag `v0.48.0`) | Released Netcode 0.48.0 |
| `dots-pending` | UnityDots | `286b009` | PR #37 code snapshot; release and client pin pending PR #37 |
| `client#154` | IndieRPGMMOAdventure | `d8d227b` | Client PR #154 head (test-lab pin baseline) |
| `client#155` | IndieRPGMMOAdventure | `2eb4d93` | PR #155 code snapshot; client Netcode pin pending PR #155 |
| `lab` | rpg-factory | `a31d749` | rpg-factory PR #17 head (the test lab) |

Path abbreviations: `GS/` = `backend/gameserver-dotnet/GameServer/`,
`GST/` = `backend/gameserver-dotnet/GameServer.Tests/`, `SGL/` = `backend/gameserver-dotnet/Shared.GameLogic/`,
`ADR` = `backend/docs/ARCHITECTURE-DECISIONS.md`.

**Server chain.** PRs #472, #474, #475 and #482 are merged into `develop` at `7fbdcf2`.

**Client version caveat.** Netcode v0.48.0 is released at `51b083e`; the client pin update remains
pending PR #155. UnityDots #37 and its client pin update are also pending. References below to
`nc-0.48.0` describe the released Netcode version; references to `dots-pending` and client pins
remain conditional on those PRs. Historical readiness runs may still describe the embedded commit
used at the time.

---

## 1. Authority

### The server owns the world

| The server decides | Where | Source |
|---|---|---|
| Position. The client sends a direction; the server integrates `direction * speed * dt` itself | `InputHandler` | `develop:GS/Input/InputHandler.cs:662-664`, step at `:692-698` |
| HP, damage and combat resolution | `InputHandler` (attacks), `StepGameplay` (projectiles, statuses) | `develop:GS/Input/InputHandler.cs:17-21`; `develop:GS/Server/TickLoop.cs:491-492` |
| Spawn on join | `PlayerSpawn.Resolve` | `develop:GS/Server/GameServer.cs:1729`, `:1776` |
| Respawn: dead for at most one world tick, then full HP at the spawn point | `PlayerRespawnSystem` (World group) | `develop:GS/Scaffolding/EnemyCombatSystems.cs:291-298`, `:330-334`, `:363-373` |
| Respawn point scatter | `ScatterSpawn` | `develop:GS/Scaffolding/EnemyCombatSystems.cs:376` |
| Enemy AI: spawn, move, attack, reap | Enemy AI phase, before broadcast | `develop:GS/Server/TickLoop.cs:589-594` |
| Hits of projectiles, lag-compensated by at most 200 ms; the server never trusts a client origin | ADR-29 | `ADR` heading `develop:backend/docs/ARCHITECTURE-DECISIONS.md:4113`; `:4137-4141` |

A client result is a hint only (`develop:SGL/Systems/AbilityLogic.cs:20-24`).

### The client predicts only two things

1. **Its own movement.** `LocalMovePredictor` predicts the local player and reconciles against
   the server (section 3).
2. **Its own projectiles, visually.** The owner predicts its projectile and pairs it with the
   server entity by `spawn_seq` (`develop:ADR:4128-4130`). The hit is still the server's.

The client never predicts HP, death, spawns, respawns, enemy movement or another player.

### Shared.GameLogic holds the rules (ADR-10)

- Client and server run the same movement, combat and validation code
  (`develop:ADR:1009-1010`). The rules are what must agree between the two sides, not the ECS
  (`:1027-1029`). Shared.GameLogic stays ECS-free: static functions over plain structs (`:1038-1041`).
- **Determinism rules:** only IEEE-754-exact float operations (`+ - * /`, comparison,
  `MathF.Min/Max/Abs/Sqrt`). No transcendentals (`develop:ADR:1105-1111`). Round every intermediate
  to `float` explicitly (`:1113-1122`).
- **Where the code goes:** deterministic logic that the client must reproduce or predict goes into
  `SGL/`, and the server calls it. A copy that lives only in `GS/` makes the client diverge.
- **Golden vectors** pin the rules on both sides: `develop:SGL/GoldenVectors/` (aoi, combat,
  hitbox_rewind, motor3d, movement, projectile, snapshot_merger, validation, vec2). Regenerate them
  only for an intended behaviour change: `GOLDEN_REGEN=1 dotnet test --filter Regenerate`
  (`develop:GST/Golden/GoldenVectorGenerator.cs:12-13`). Explain every changed case in the PR.
- An SGL change is a two-repo contract. The client gets it only through an `sgl-v*` tag and a pin
  bump (`client#154:Packages/manifest.json:31` pins `sgl-v0.7.0`).

---

## 2. Input path

### From the client sample to the handler

1. **Client.** The client samples intent each frame and sends an `InputMessage` (move direction,
   attack target, ability, jump) stamped with its predicted base tick.
2. **Transport.** KCP over UDP, reliable and ordered (section 6).
3. **Decode and admit.** `GameServer.OnMessageReceived` decodes the `InputMessage` and calls
   `_world.PushInput` (`develop:GS/Server/GameServer.cs:2144-2172`). The outcome (coalesced,
   dropped by the connection budget, dropped because the queue is full) is counted (`:2105-2114`).
4. **Coalesce at ingest.** A movement-only input replaces the previous queued movement-only entry;
   the newest tick wins (`develop:GS/World/EcsWorld.cs:1734-1745`, `:1771-1773`, `:1791-1802`).
   An input with an attack, an ability or a jump is never coalesced away. Per-connection bound:
   `DefaultMaxInputsPerConnection = 32` (`:209`); knobs `GAMESERVER_MAX_INPUTS_PER_TICK` and
   `GAMESERVER_MAX_PENDING_INPUTS` (`develop:GS/ServerEnv.cs:90`, `:93`).
5. **Drain once per base tick.** `_world.DrainInputs(inputs)` (`develop:GS/Server/TickLoop.cs:538`).
6. **Coalesce at the tick.** Each input in the batch is processed, but **only the newest input per
   player moves the player** (highest client tick, arrival order as tiebreak)
   (`develop:GS/Server/TickLoop.cs:548-563`, `:475-493`). A tick with no input still applies the
   held movement (`:502-507`, `:579-587`). A stop clears held movement even when it is not the
   newest input (`develop:GS/Input/InputHandler.cs:666-688`).
7. **Handler.** `InputHandler` checks, in order: the entity exists, it is alive, the input tick is
   newer than the last one applied (`develop:GS/Input/InputHandler.cs:631-651`). Then it validates
   and applies.

Input, movement and combat run inline in the tick body, not as declared `IEcsSystem`s
(`develop:GS/Server/TickLoop.cs:525-531`). This is known structural debt. Do not copy it into a
new system, and do not move it in passing (that is an architecture change with a measurement leg).

### One input owner

One user owns one entity. A newer login of the same user kicks the older session; the newer login
owns the user (`develop:GS/Server/GameServer.cs:2108-2111`). A player entity's id is its user id
(`develop:GS/Persistence/CharacterSessions.cs:9`). On the client, one scene has one input source and
one predicted local entity. Once UnityDots PR #37 lands, the prediction system queries only the
`LocalNetworkEntity` tag and predicts exactly one entity
(`dots-pending:Runtime.Netcode.Prediction/LocalPredictionSystem.cs:94`).

### Rejections

Every refused input has a typed reason (`develop:GS/Input/InputRejection.cs:37`, 16 values at
`:43-175`):

`entity_gone`, `dead_entity`, `stale_tick`, `invalid_direction`, `attack_target_unresolved`,
`attack_target_dead`, `attack_out_of_range`, `attack_on_cooldown`, `attack_other`,
`ability_unknown`, `ability_on_cooldown`, `ability_out_of_range`, `ability_target_unresolved`,
`ability_target_dead`, `ability_caster_dead`, `ability_other`.

Four more rejection reasons now split `attack_other` and `ability_other`
(`develop:GS/Input/InputRejection.cs:187`, `:195`, `:203`, `:215`; labels `:314-317`):
`attack_target_untargetable`, `attack_cannot_act`, `ability_cannot_cast`, `ability_missing_aim`.

Each reason has a `RejectionWeight`: `Benign`, `LatencyExplicable` or `Forged`
(`develop:GS/Input/InputRejection.cs:237-260`; table at `:322-365`). Only `invalid_direction` is
`Forged` (`:310`).

**Observe the rejections:**

- Metric `gameserver_inputs_rejected_total{reason}` (`develop:GS/Observability/GameMetrics.cs:376-397`).
  It is not the same as `gameserver_inputs_dropped_total{reason}` (admission drops) or
  `gameserver_inputs_coalesced_total` (`:49-50`, `:370-373`, `:418-422`).
- `/status` fields `inputs_rejected` and `inputs_rejected_by_reason`
  (`develop:GS/Observability/ServerStatus.cs:763`, `:767`).

**Anti-cheat observes only.** `AttackRateAudit` and `InputAnomalyTracker` record and never act on
a player (`develop:GS/Input/AttackRateAudit.cs:10`; `develop:GS/Input/InputAnomalyTracker.cs:8`).
Turning an observation into a kick or a ban needs thresholds, which is a gameplay decision
([#457](https://github.com/Cuvara/rpg-mmo-server/issues/457)).

### Checklist: adding a new input or command

Source: rpg-factory `skills/server-realtime/references/gameplay-systems.md` ("Authority checklist").

1. The client sends **intent**, never an outcome: a direction, not a displacement; a target id, not
   a damage value; an item id, not an inventory state.
2. Decode with `TryRead`. Reject a malformed command with a typed error
   (`GameplayErrors.InvalidPayload`; `develop:GS/Commands/CommandRouter.cs:121`, `:189-225`). For
   input, add a value to `InputRejection` with its `RejectionWeight` and its metric label.
3. Validate on server state, inside the tick: the entity is alive, range is checked **after** this
   tick's movement, cooldowns count **ticks**, never wall-clock time.
4. Rate-limit at admission (`TryAdmit`, `develop:GS/Commands/CommandRouter.cs:134-136`). Keep every
   per-connection queue bounded.
5. Any randomness is a deterministic function of server state. No `System.Random` on the tick.
6. A new anti-cheat check records into the existing observers. It does not act.
7. Tests: one rejection test per new reason, the happy path through `TickLoop.TickOnce()`, and the
   allocation guards still at 0 B (section 7).
8. A new wire field or message is a `wire.proto` change: a cross-repo contract (Go gateway, C#
   server, Netcode, client), driven by the rpg-factory `wire-contract` skill. ADR-30: gameplay grows
   through content and opcodes, not through the realtime schema (`develop:ADR:4147`).

---

## 3. Ticks and rates

### Three groups on one integer tick line (ADR-13)

| Group | Default | Knob | Source |
|---|---|---|---|
| Critical (base tick: input, movement, combat) | 60 Hz | `SIM_CRITICAL_HZ` | `develop:GS/Server/SimulationRates.cs:59` |
| World (enemy AI, respawn, **replication**) | 15 Hz | `SIM_WORLD_HZ` | `:62` |
| Background | 5 Hz | `SIM_BACKGROUND_HZ` | `:65` |
| Upper limit for any group | 240 Hz | | `:72` |

- The knobs are read in `develop:GS/Program.cs:102-114` (also `--sim-*-hz` flags; the legacy
  `GAMESERVER_TICK_RATE` applies only when no `SIM_*` variable is set).
- **Divisibility rule.** The world and background rates must divide the critical rate, and no
  group may be faster than the one before it. A pair that breaks this is **rejected at startup, not
  rounded** (`develop:GS/Server/SimulationRates.cs:242-270`; `develop:ADR:1783-1787`, ADR-13 d2).
- A group runs on base tick `t` when `(t - 1) % every == 0`
  (`develop:GS/Server/SimulationRates.cs:183-187`). The doc comment at `:45` says `baseTick % every`;
  the code and ADR-13 d2 are authoritative.
- The order Critical, World, Background is fixed (`develop:GS/Server/SimulationSchedule.cs:18-22`;
  ADR-13 d6, `develop:ADR:1805-1810`).
- **A system declares its frequency only through `IEcsSystem.Group`.** Never count ticks, test
  `tick % n` or read Hz in a system.

### Snapshots run at the World rate

Replication is gated to the World rate, not the base rate
(`develop:GS/Server/TickLoop.cs:614-633`; ADR-13 d7, `develop:ADR:1812-1816`). At the defaults that
is one snapshot every 4 base ticks. Events raised on non-broadcast ticks are kept for the next
snapshot (`develop:GS/Server/TickLoop.cs:747-758`). Sending at the base rate would break the
bandwidth budget (section 6).

### The client adopts the advertised tick rate

- The server advertises `tick_rate` in `JoinTokenResp`. It is `MovementHz`, which is the critical
  rate (`develop:GS/Server/GameServer.cs:1987`; `develop:GS/Server/SimulationRates.cs:122`). The
  same value is on `/status` (`develop:GS/Observability/ServerStatus.cs:865`). A client that sees 0
  must refuse to predict (`develop:backend/shared/proto/wire.proto:325-331`).
- Netcode `LocalMovePredictor.UseServerTickRate(uint)` adopts it: a different rate re-derives the
  timestep and the catch-up budget and resets the predictor
  (`nc-0.48.0:Runtime/Prediction/LocalMovePredictor.cs:739`). **It was absent from the previously pinned Netcode version.** The
  client calls it only after client PR #155 (`client#155:Assets/Scripts/DI/Dots/DotsWorldBridge.cs:643`),
  which needs a Netcode `>= 0.48.0` pin.
- Until then the shipping client predicts at its configured rate. A server started with a
  non-default critical rate is therefore not supported by the shipping client.
- **The interpolation clock of the first joiner** seeds its seconds-per-tick from the observed tick
  gap and takes its first rate measurement over at least 0.25 s, so a bunched join pair cannot set
  a wrong rate (`FirstMeasurementSeconds`, `nc-0.48.0:Runtime/Interpolation/InterpolationClock.cs:123`,
  `:265`). Netcode 0.48.0 only.

### Prediction, reconciliation and replay

- History ring: 256 ticks (`nc-0.48.0:Runtime/Prediction/LocalMovePredictor.cs:316`; the same in
  `nc-0.48.0`). A replay longer than the ring keeps the newest 256 ticks exact (test
  `ReplayHistoryStagingTests.AReplayLongerThanTheHistoryRing_KeepsTheNewestTicksExact`).
- Corrections up to `SmoothingThreshold = 0.5` units are smoothed; a larger correction snaps and
  increments `Snaps` (`nc-0.48.0:Runtime/Prediction/LocalMovePredictor.cs:226`).
- The server reports which input it applied last (`AckTick`, `AckAppliedTick`;
  `develop:GS/Snapshot/SnapshotDeltaState.cs:1143-1145`). Normative reference:
  `backend/gameserver-dotnet/docs/API.md` "Reconciliation" and "Which history entry a snapshot
  describes" (`develop:backend/gameserver-dotnet/docs/API.md:1446`, `:1459`).

### Clock steering and stalls

All of this is Netcode v0.48.0 behavior (`nc-0.48.0`); the client pin update remains pending PR #155.
The contract: **a client main-thread stall costs at most one correction**, and in the gate run it
cost none (section 7).

- The predictor steers its clock onto the server's tick line. A clock error above
  `HardResyncTicks` (2 s of ticks) forces a hard resync (`nc-0.48.0:Runtime/Prediction/LocalMovePredictor.cs:2325`). The
  integral steering band is 8 ticks (`SteerIntegralBandTicks`, `:2623`).
- **Stall jump.** After a stall the predictor jumps its clock onto the line once (`JumpClock`,
  `nc-0.48.0:Runtime/Prediction/LocalMovePredictor.cs:1015`; counted in `StallResyncs`, `:965`). After a clamped frame any
  error beyond `StallJumpMinTicks = 2` is jumped, not steered (`:2401-2403`, `:2628`).
- **Jump credit across Unity's late `deltaTime`.** Unity reports a frame's stall in the next frame's
  `deltaTime`. The jump credit therefore lasts for two `Advance` calls (`_jumpCreditAdvances`,
  `:999`, set at `:2411`), so the long frame after the stall frame is not simulated twice.
- **Clamp-end input move.** The client records the stall-end input and then calls `Advance` with
  the stall as its delta in the same frame. A clamped `Advance` moves the inputs recorded since the
  previous `Advance` to the clamp's end tick (`MoveThisFramesInputsToTheClampEnd`, `:1088`, called at
  `:2161-2163`; counted in `InputsMovedToClampEnd`, `:1079`). A later jump restamps unacknowledged
  inputs of the jumped frame (`DeferRestamp` / `ApplyDeferredRestamp`, `:1124`, `:1143`).
- **Late and bunched snapshots.** After a stall the queued snapshots arrive oldest first.
  `PredictionClockSteering.OnSnapshot` classifies each one against the arrival cadence
  (`nc-0.48.0:Runtime/Prediction/PredictionClockSteering.cs:293`): **late** (gap above 2x the expected
  interval) steers to the snapshot tick plus its delivery delay at the advertised rate
  (`:355-364`); **bunched behind a late one** (below 0.5x) is not steered on and its reconcile is
  skipped (`:344-352`; `SkipReconcileForBunchedSnapshot`, `nc-0.48.0:Runtime/Prediction/LocalMovePredictor.cs:2308`).
- **Interpolation clock.** A rate sample is accepted only within `[0.5x, 2x]` of the current
  seconds-per-tick and only for an on-cadence arrival; after 5 s of continuous rejection the new
  rate is relearned (`nc-0.48.0:Runtime/Interpolation/InterpolationClock.cs:101-115`, `:311`). After an
  off-cadence episode, the first on-cadence arrival sets the clock forward once when it is more than
  0.5 s behind (`:137`, `:373-380`). A clock too far ahead of the newest snapshot holds (`:396-402`).
  Counters: `RejectedIntervalSamples`, `ClockResyncs`, `AheadHoldFrames` (`:143-154`).
- **Server stall.** One second without snapshots followed by a burst corrects nothing
  (`ServerStallRecoveryTests.NoSnapshotsForASecondThenABurst_CorrectsNothing`, Netcode headless suite).
- `PredictionClockSteering.Reset()` forgets the tick-rate, staleness and ack-latency estimators
  and the advertised rate (`nc-0.48.0:Runtime/Prediction/PredictionClockSteering.cs:745-757`).
- **What remains:** `AckLatencyEstimator` can flap between refusing and accepting its floor
  (`ConservativeFloorTicks`, `nc-0.48.0:Runtime/Prediction/AckLatencyEstimator.cs:964`; fit at `:625`),
  which moves the lead target by about 4 ticks and costs a ~2-step correction, with or without a
  stall (section 8).
- **Predictor call trace (opt-in).** `LocalMovePredictor.Trace` and `PredictionClockSteering.Trace`
  take a `PredictorTraceSink` that receives every public call (`nc-0.48.0:Runtime/Prediction/LocalMovePredictor.cs:977`;
  `nc-0.48.0:Runtime/Prediction/PredictionClockSteering.cs:160`; `nc-0.48.0:Runtime/Prediction/PredictorTrace.cs:66`).
  It is a null check when off. Use it in test players only (section 7, "How to debug a prediction
  problem").

### Lab caveat: the WSL VM clock

On the shared development host the WSL VM's monotonic clock runs about 8% slow against Windows
(direct measurement: ratio 0.927 over 60 s, `.testlab-runs/t06/clock-drift-check.txt` in the
workspace; trace fits over the 1 h soaks: 1.082-1.110). A server in WSL configured at 60/15 Hz then
runs at about 55/13.8 Hz of Windows time, and its rate wanders within an hour. This is a property of
that lab host, not of the game. Do not quote a rate or a latency measured across the Windows/WSL
boundary without this caveat, and do not "fix" the game for it.

---

## 4. Snapshots and replication

### Keyframes and deltas

- A full snapshot goes out on join, on `MsgResync`, and every `GAMESERVER_KEYFRAME_INTERVAL`
  snapshots (default 30; `develop:GS/Snapshot/SnapshotDeltaState.cs:23-25`,
  `develop:GS/ServerEnv.cs:101-102`, `develop:SGL/Components/GameConstants.cs:142`). Deltas carry
  changed entities and a `Removed` list (`develop:GS/Snapshot/SnapshotDeltaState.cs:1347`).
- `MsgResync` forces the next snapshot to be full (`develop:GS/Server/GameServer.cs:2184-2189`).
- The server treats "last sent" as "last received". That is only correct because KCP is reliable
  and ordered (`develop:GS/Snapshot/SnapshotDeltaState.cs:27-32`).
- Normative client merge algorithm: `develop:backend/gameserver-dotnet/docs/API.md:1281`; keyframe
  policy `:1396`.

### Area of interest

- AOI radius `GAMESERVER_AOI_RADIUS`, default 50 units (`develop:GS/Server/AoiSettings.cs:21`;
  `develop:SGL/Components/GameConstants.cs:132`). An entity that leaves the radius is sent as a
  removal and re-added when it comes back.
- The spatial grid answers a query only when the population occupies at least 96 cells; below that
  a brute-force scan is faster (`develop:GS/World/SpatialGrid.cs:152-156`). Read
  `backend/docs/BENCHMARK.md` Part XI before any AOI change.

### Importance and shedding

- **Importance** orders what is sent. It does not decide how much, and it is off by default
  (`GAMESERVER_IMPORTANCE`, default `legacy`; `develop:GS/Server/ImportanceSettings.cs:20`,
  `:64`, `:74-75`; ADR-27, `develop:ADR:3742`).
- **Shedding** is the per-connection downlink byte budget `GAMESERVER_MAX_SNAPSHOT_BYTES`, default
  8192 (`develop:GS/ServerEnv.cs:99`; `develop:GS/Snapshot/SnapshotDeltaState.cs:322`). It is a tail
  cap, not a traffic shaper (`:312-321`). A shed entity is deferred, never lost (`:285-291`). The
  local player's own entity is never shed (`:353-354`). There is no other shedding knob.
- **Gap gauges.** `/status` `snapshot_max_update_gap_ms` and `snapshot_max_shed_age`
  (`develop:GS/Observability/ServerStatus.cs:605`, `:662`); metrics
  `gameserver.snapshots.max_update_gap_ms` and `.max_shed_age`
  (`develop:GS/Observability/GameMetrics.cs:23`, `:232`, `:497`).
  On `develop`, an entity shed from the join keyframe charges the server's whole uptime to
  `snapshot_max_update_gap_ms` ([#471](https://github.com/Cuvara/rpg-mmo-server/issues/471)).
  On develop, the gap now counts from the join (`develop:GS/Snapshot/SnapshotDeltaState.cs:604-615`).
- **Coalescing per connection.** The server stages one snapshot per connection. A newer gather
  replaces an unclaimed one and is counted in `gameserver_snapshots_coalesced_total`
  (`develop:GS/Net/Connection.cs:381-392`, `:721`).

### Ordering and freshness on the client

- KCP delivers in order, but the client still rejects old data (Netcode 0.48.0):
  - `WorldState.Apply` drops a snapshot older than the current tick, and a second snapshot at the
    same tick once one has been merged, and counts it in `StaleSnapshotsDropped`
    (`nc-0.48.0:Runtime/World/WorldState.cs:204`, `:341`). The server sends at most one snapshot per
    world tick per connection, so this guard never drops a legitimate snapshot. the previously pinned Netcode version merged an older snapshot (defect D2).
  - The interpolation clock ignores a tick at or before its newest one
    (`nc-0.48.0:Runtime/Interpolation/InterpolationClock.cs:214`). The sample ring rejects a duplicate or
    reordered tick (`nc-0.48.0:Runtime/Interpolation/InterpolationRing.cs:23`).
- **Same-frame removal and re-add.** When a backlog merges several snapshots before one view tick,
  an id can be removed (it left the area of interest) and added again without the view seeing the
  gap. `WorldState.TakeRemovedIds` reports removals since the last view tick, and the binder
  respawns such an id with an empty sample ring (`nc-0.48.0:Runtime/World/WorldState.cs:374`;
  `nc-0.48.0:Runtime/View/WorldViewBinder.cs:536`, counter `RespawnsWithinAFrame` `:174`).
- A new gameplay field must survive this: never rely on applying a snapshot twice, and never carry
  view state across a removal.

### Interpolation and extrapolation (remote entities)

Defaults from `nc-0.48.0:Runtime/Interpolation/InterpolationConfig.cs:189-208`:

| Field | Default | In `nc-0.48.0` |
|---|---|---|
| `TargetDelay` | 0.100 s | yes |
| `MaxExtrapolation` | 0.050 s | yes |
| `ExtrapolationHold` | 0.150 s | no |
| `ExtrapolationSettle` | 0.100 s | no |
| `MaxExtrapolationSpan` | 0.250 s | no |
| `MaxExtrapolationSpeed` | 0 (off) | no |

Semantics of hold and settle: `nc-0.48.0:Documentation~/INTERPOLATION.md:47-61`. `default(InterpolationConfig)`
normalises hold, settle and span to these defaults, but `MaxExtrapolation` stays 0, which means
"never extrapolate" (`nc-0.48.0:Runtime/Interpolation/InterpolationConfig.cs:232-258`). Client PR #155 uses
`InterpolationConfig.Default` with `MaxExtrapolationSpeed = 15` units/s
(`client#155:Assets/Scripts/DI/Dots/DotsWorldBridge.cs:93`, `:346-347`).

### Teleports, including respawn

- **Local player: a respawn that moves the player is a snap, by design.** It moves the
  authoritative position further than `SmoothingThreshold`, so the predictor snaps once and does
  not re-apply it. In the 2026-10-22 test-lab soaks, client snaps equalled the server respawns that
  moved the player, one to one (for example 544 snaps against 543 such respawns plus the join). An
  idle player under attack dies about every 18 s at the current enemy defaults (section 5), so
  "snaps at idle" are respawns, not a prediction defect.
- **Remote entities.** A last segment faster than `MaxExtrapolationSpeed` or longer than
  `MaxExtrapolationSpan` is held at its destination, not carried past it
  (`nc-0.48.0:Runtime/Interpolation/InterpolationConfig.cs:86-109`; `nc-0.48.0:Documentation~/INTERPOLATION.md:152`).
  Both guards need Netcode `>= 0.48.0`.
- A new movement mode that moves an entity discontinuously (dash, blink, portal) is a teleport for
  both rules. Test it against both.

---

## 5. Entity lifecycle

### Server

- **Spawn.** A player spawns on join (`develop:GS/Server/GameServer.cs:1729`, `:1776`). Its entity
  id is its user id.
- **Despawn.** A despawn reaches clients in the snapshot's `Removed` list
  (`develop:GS/Snapshot/SnapshotDeltaState.cs:1347`). A deferred despawn is offered again (`:294`).
- **Drop: 30 s / 60 s hold.** A connection that drops (dead link, timeout, process kill) keeps its
  entity for 30 s on a map and 60 s in a dungeon, so a reconnect reattaches to it
  (`develop:GS/Program.cs:1112`; `develop:GS/Program.cs:1112`). A held entity is linkdead and out of
  reach. There is no knob for this window.
- **Graceful leave: immediate release (on develop).** A client `disconnect` frame marks the
  connection `Left` before it closes (`develop:GS/Server/GameServer.cs:2227-2235`;
  `develop:GS/Net/Connection.cs:123-132`). The teardown then runs the same hold path with a zero
  window: `holdTtl = graceful ? TimeSpan.Zero : _options.HoldTtl` (`develop:GS/Server/GameServer.cs:2711`).
  The player is saved before the entity is removed (`:2755`, `:2764`). Contract:
  `develop:backend/gameserver-dotnet/docs/API.md:82`, `:1155-1159`. On `develop` the `disconnect`
  frame only closes the connection (`develop:GS/Server/GameServer.cs:2227-2235`), so a polite leave
  is held like a drop.
- **Combat logout is the user's decision.** Immediate release also lets a player leave in the
  middle of combat. No combat-logout rule (for example, keeping the entity while it is in combat)
  exists. Adding one is a gameplay rule, not an implementation detail.
- **Respawn.** `GAMESERVER_PLAYER_RESPAWN` is on by default
  (`develop:GS/Scaffolding/EnemyAiSettings.cs:63`). Respawn lives in the enemy phase: with
  `GAMESERVER_ENEMIES` off, a killed player stays dead. There is no invulnerability window after a
  respawn.
- **Spawn scatter** (on develop): `GAMESERVER_SPAWN_SCATTER`, default 3 units
  (`develop:GS/ServerEnv.cs:183`; `develop:GS/Server/ServerDefaults.cs:28`). Spawns and respawns land on a
  scattered point instead of one shared spawn ([#469](https://github.com/Cuvara/rpg-mmo-server/issues/469)).
  Without scatter, idle players respawn on the same point; every enemy then picks the same nearest
  player (the nearest-player search keeps the first on a tie, `develop:GS/Scaffolding/PlayerTargetBuffer.cs:166`),
  so the other players are shielded by accident.

### Enemy population and damage

| Knob | Default | On develop? | Source |
|---|---|---|---|
| `GAMESERVER_ENEMY_MAX` | 30 | yes | `develop:GS/Scaffolding/EnemyAiTuning.cs:65` |
| `GAMESERVER_ENEMY_MAX_PER_PLAYER` | 45 | yes | `develop:GS/Scaffolding/EnemyAiTuning.cs:74` |
| `GAMESERVER_ENEMY_BUDGET` | 300 | on develop | `develop:GS/Scaffolding/EnemyAiTuning.cs:170` |
| `GAMESERVER_ENEMY_SEPARATION` | 0.8 units | on develop | `develop:GS/Scaffolding/EnemyAiTuning.cs:154` |
| `GAMESERVER_ENEMY_SEED` | (unset) | on develop | `develop:GS/Scaffolding/EnemyAiSettings.cs:71` |
| `GAMESERVER_ENEMY_ATTACK` | 5 | yes | `develop:GS/Scaffolding/EnemyAiTuning.cs:87` |
| `GAMESERVER_ENEMY_ATTACK_INTERVAL` | 0.5 s | yes | `develop:GS/Scaffolding/EnemyAiTuning.cs:124` |
| `GAMESERVER_ENEMY_ATTACKERS_PER_TARGET` | 3 | yes | `develop:GS/Scaffolding/EnemyAiTuning.cs:145` |

Knob names: `develop:GS/Scaffolding/EnemyAiSettings.cs:44`, `:50`, `:53`, `:56` (`GAMESERVER_ENEMY_ATTACKS`
turns attacks off).

- **Cap.** `cap = MAX + MAX_PER_PLAYER * live players`, clamped to `BUDGET` on develop
  (`develop:GS/Scaffolding/EnemyAiSettings.cs:292-303`). At 3 players the cap is 165, so the budget
  binds only above 6 players ([#468](https://github.com/Cuvara/rpg-mmo-server/issues/468)).
- **Damage to an idle player.** At most `ATTACKERS_PER_TARGET` attacks per player per window of
  `ceil(ATTACK_INTERVAL * world Hz)` world ticks (`develop:GS/Scaffolding/EnemyCombatSystems.cs:141-142`,
  `:191`). Each hit deals `max(attack - defense, MinDamage)` (`develop:SGL/Systems/CombatLogic.cs:31-35`);
  enemy attack 5 against player defense 5 gives 1. With 100 HP (`develop:GS/Server/ServerDefaults.cs:10`,
  defense `:20`) an idle player in range of at least 3 enemies dies about every 18 s. Separation and
  the budget do not change that rate.
- **Confirm the values in force on `/status`:** `enemy_ai` (on develop it includes `budget=` and
  `separation=`; `develop:GS/Scaffolding/EnemyAiSettings.cs:439-440`), `enemy_ai_max_now`,
  `enemies_alive`, `player_respawns` (`develop:GS/Observability/ServerStatus.cs:139`, `:477`, `:498`,
  `:521`).
- **Separation** keeps enemies chasing one player from stacking on one point. Every hash-chain
  entry a query steps over counts against `MaxVisited = 256`, so a crowd of colliding cells cannot
  make a query walk the whole chain (`develop:GS/Scaffolding/EnemySeparationSystem.cs:58`).

### Client: sessions, generations, reconnect

- **Generation.** Every connect increments the client's session generation
  (`nc-0.48.0:Runtime/Client/NetworkClient.cs:448`). Commands from an older generation are dropped by
  the DOTS view.
- **World reset.** `NetworkClient.RunConnectAsync` calls `World.Reset()` on every connect and
  reconnect (`nc-0.48.0:Runtime/Client/NetworkClient.cs:469`).
- **Reconnect resets.** On `Reconnected` the client resets the view generation, the binder, the
  predictor and the projectile predictor (`client#154:Assets/Scripts/DI/Dots/DotsWorldBridge.cs:499-529`;
  `client#155:Assets/Scripts/DI/Dots/DotsWorldBridge.cs:510-540`). The binder reset matters when the new server's ticks are lower than the
  old one's (reconnect to a restarted or different pod).
- **Fallbacks for a missed reset:**
  - The binder restarts its tick line when the world tick goes backwards and counts it in
    `TickLineRestarts` (`nc-0.48.0:Runtime/View/WorldViewBinder.cs:546`, `:961`).
  - The UnityDots timed path resets its timeline clock on the `Reset` command (pending UnityDots PR #37;
    `dots-pending:Runtime.Netcode/NetworkViewCommandSystem.cs:190-200`).
  - Once UnityDots PR #37 lands, `LocalPredictionSystem` resets its ack/anchor memory, the steering **and
    the predictor** on a new generation or a backwards world tick, before that update's reconcile
    (`dots-pending:Runtime.Netcode.Prediction/LocalPredictionSystem.cs:111-140`, `predictor.Reset()` at
    `:139`; `dots-pending:Documentation~/NETCODE-INTEGRATION.md:120-128`). A second reset by the host is
    harmless.
- **Dead session detection (Netcode 0.48.0).** A client main-thread freeze longer than
  about 3 s makes the server close the session (`dead_link`); that is the server's KCP design. The
  client then fails its session either by its own KCP dead link or, at the latest, after **5 s of
  server silence counted only while the client is running** (tick gaps above 250 ms are not
  counted), and reconnects (`nc-0.48.0:Runtime/Transport/KcpClientSession.cs:85`, `:91`, `:355`). Before
  this, a client whose RTO was inflated by an acknowledgement read after the freeze hung for 31 s
  until its 40 s heartbeat expired. In the gate run the reconnect after a 4.7 s freeze took 0.42 s.
- **Graceful leave on the client (Netcode 0.48.0).** `WireConnection.LeaveAsync` writes
  the `disconnect` frame, waits up to `LeaveWriteTimeoutMs = 500` for the write, then flushes KCP
  until the frame is acknowledged or the close-flush budget runs out, then closes
  (`nc-0.48.0:Runtime/Connection/WireConnection.cs:306`, `:349`, `:364`). Budget:
  `min(max(2 x RTO, 100 ms), 500 ms)` (`nc-0.48.0:Runtime/Transport/KcpClientSession.cs:203`, `:206`,
  `:232`). With `nc-0.48.0` the frame never reaches the wire and the server sees a dead link, so even
  on develop the leave is held like a drop. S08G passes only with both fixes; it passed live with
  both (for example `20261023T070125Z-S08G-wcwk`).
- **Reconnect policy.** Which close reasons reconnect: `nc-0.48.0:Runtime/Client/ReconnectPolicy.cs:60-113`
  (`PeerClosed`, `HeartbeatTimeout`, `TransportError` reconnect; `ServerDisconnect` reconnects after
  a delay only for `server_shutdown`; local close, protocol error and most kicks do not).
- **Handle reuse.** Snapshot entity ids are interned to handles. A despawned entity's view must not
  survive, and a reused handle must not show the old state. Netcode pins this end to end in
  `EntityLifecycleTests` and `EntityReentryTests` (headless suite).

---

## 6. Networking expectations

- **KCP/UDP only** for gameplay (ADR-32, `develop:ADR:4210`). Any `GAMESERVER_TRANSPORT` other than
  `kcp` is fatal at startup (`develop:GS/Program.cs:170-174`, `:240-244`). The gateway hop is TCP,
  optionally TLS, and carries no gameplay (ADR-3).
- **Crypto.** Optional datagram PSK `TRANSPORT_KEY` (AES-256; empty = plaintext;
  `develop:GS/Net/Transport/Transport.cs:18`; ADR-8). Sealed session `GAMESERVER_SEALED`, default
  `require` (`develop:GS/ServerEnv.cs:149`; ADR-22, ADR-25). Changing framing, handshake or crypto
  bytes is wire-contract work.
- **KCP limits (server).** Send window 128, writer soft limit 256 queued segments, idle timeout 60 s
  (`develop:GS/Net/Transport/KcpTuning.cs:20`, `:51`; `develop:GS/Net/Transport/KcpLimits.cs:92`).
  KCP is reliable and ordered: one lost datagram holds every later snapshot until it is
  retransmitted. A client that stops acknowledging for about 3 s loses its session (section 5).
- **Receive drain (client, Netcode 0.48.0).** After each awaited datagram the client
  reads every queued datagram synchronously, up to 4096
  (`nc-0.48.0:Runtime/Transport/UdpDatagramDrain.cs:39`, `:50`). Before this, the receive loop continued on
  the Unity frame context and read one datagram per frame, so a backlog after a stall took hundreds
  of frames to drain. Counters: `DatagramsRead`, `ReceiveWakeups`, `MaxDatagramsPerWake`
  (`nc-0.48.0:Runtime/Transport/KcpTransport.cs:106-112`).
- **Bandwidth budget.** `< 50 KB/s` downstream per client (ADR-7, `develop:ADR:771`). Measured:
  45.9 KB/s per client at 200 players (`develop:backend/docs/BENCHMARK.md:1360`, `:1383`). A feature
  that adds snapshot bytes per entity needs a measurement before it merges. The per-server player
  ceiling is unknown (ADR-7; [#205](https://github.com/Cuvara/rpg-mmo-server/issues/205)); do not
  quote one.
- **Delivery trace for diagnosis** (on develop). `GAMESERVER_SNAPSHOT_TRACE=<path>` writes one CSV
  row per sent snapshot. Unset = off. A path that cannot be opened exits 2
  (`develop:GS/ServerEnv.cs:175`; `develop:GS/Program.cs:355-376`). Columns:
  `conn_id,user_id,world_tick,gather_us,enqueue_us,write_start_us,write_end_us,bytes,queue_depth,dropped_oldest_total,kcp_wait_snd,kcp_snd_wnd,kcp_rmt_wnd,kcp_retrans_total,kcp_rto_ms`
  (`develop:GS/Observability/SnapshotTrace.cs:108-111`). Compose forwards it
  (`develop:backend/deploy/docker-compose.yml:516`); the fleets deliberately do not. Related `/status`
  fields: `snapshot_send_queue_depth_max`, `snapshot_send_dropped_oldest`, `kcp_wait_snd_max`,
  `kcp_rto_max_ms`, `kcp_rmt_wnd_min`, `kcp_segments_retransmitted`, `snapshot_trace_rows_written`,
  `snapshot_trace_rows_dropped` (`develop:GS/Observability/ServerStatus.cs:669-716`); metric
  `gameserver_kcp_segments_retransmitted_total` (`develop:GS/Observability/GameMetrics.cs:36`).
  Development and test only: the file grows without bound and holds every player's user id.
- **Client session close diagnostics (test players).** The TestLab writes a `session_closed` event
  with the cause, the KCP failure reason, RTO and silence counters on every gameplay session close
  (`client#155:Assets/TestLab/Runtime/TestLabRunner.cs:814`; `client#155:docs/TESTLAB.md:214`).

---

## 7. Required tests for a gameplay PR

Run what applies. Report counts: passed, failed, skipped, and the skip families. A run with zero
executed tests is not a pass.

### Server (always, for server or SGL changes)

- [ ] **Full suite:** `cd backend/gameserver-dotnet && dotnet test`. Skips are non-zero by design;
      the expected families are Bench (env-gated), Golden regen, and the Docker-gated Persistence,
      Registry, Events and World fixtures. Any other skip is a finding. One class while iterating:
      `dotnet test GameServer.Tests --filter "FullyQualifiedName~<Namespace.Class>"`; it never
      replaces the full run.
- [ ] **Unit tests for the feature:** one rejection test per new reason; the happy path through
      `TickLoop.TickOnce()`. For connection lifecycle, a socket-level test over real KCP (pattern:
      `develop:GST/Server/GracefulLeaveTests.cs:44-87`).
- [ ] **Allocation guards:** `GST/Snapshot/SnapshotAllocationTests.cs` (snapshot path) and
      `GST/CoreV3/GameplayAllocationTests.cs` (0 B over 600 ticks; `develop:GST/CoreV3/GameplayAllocationTests.cs:45`) run in every
      suite. For a change on the combat or input path, also run
      `BENCH_TICK=1 dotnet test GameServer.Tests -c Release --filter TickAllocationBench`
      (`develop:GST/Bench/TickAllocationBench.cs:44`, `:53`) and compare "Input apply (write scope)"
      against the base commit. A number written into a doc goes through the measurement process
      (`backend/docs/MEASUREMENT.md`).
- [ ] **Golden vectors** for an SGL rule: regenerate only for an intended behaviour change (section 1),
      commit the fixtures, explain every changed case. No behaviour change means an unchanged
      `SGL/GoldenVectors/`.
- [ ] **Randomness in tests:** a test that depends on enemy placement sets `GAMESERVER_ENEMY_SEED`
      and turns off what it does not test (separation made one characterisation test fail 51 times
      in 2000).
- [ ] **Docs:** a new knob is a `const string` in `ServerEnv.cs`, parsed strictly (exit 2), listed in
      `backend/gameserver-dotnet/docs/README.md`, and forwarded by compose and the fleets (or
      excluded with a reason). A new metric or `/status` field is in `docs/METRICS.md`.

### Netcode (when the change touches prediction, interpolation, transport or the snapshot merge)

- [ ] **Headless suite:** `dotnet test Tests~/Headless/Cuvara.Netcode.Tests.Headless.csproj -p:SglDir=<Shared.GameLogic path>`
      (`nc-0.48.0:Tests~/Headless/Cuvara.Netcode.Tests.Headless.csproj:91`; CI job "Headless tests (dotnet)",
      `nc-0.48.0:.github/workflows/ci.yml:79-159`). From an Orca worktree, the rpg-factory script
      `scripts/checks/netcode-headless.sh --server <rpg-mmo-server dir>` (rpg-factory PR #17) picks
      the SGL tag; it fails on a non-zero or timed-out `dotnet test` and on zero executed tests.
- [ ] **Stall and backlog shape.** A change to the clock, steering or transport must keep the stall
      tests green (`ClientHitchRecoveryTests`, `ClientStallInterpolationTests`,
      `TransportReceivePacingTests`) **and the live-trace replays** (`LiveTraceReplayTests`,
      `nc-0.48.0:Tests/Editor/LiveTraceReplayTests.cs:189`, `:212`). Synthetic harnesses missed the live
      frame order three times; the replays of recorded S13 sessions did not.
- [ ] **Unity EditMode** in the Netcode package CI ("Unity Tests").

### Client (when the change touches client code)

- [ ] **EditMode:** `Unity.exe -batchmode -nographics -projectPath <project> -runTests -testPlatform EditMode -testResults <xml> -logFile <log>`.
      Unity 6000.3.9f1. Name the Netcode version that was resolved or embedded.
- [ ] A Unity-MCP plugin error in the log can fail a test through `LogAssert`. Record it as a FAIL
      caused by the environment, never as a PASS.
- [ ] Client CI is currently blocked (section 8), so local EditMode evidence is required.

### Test lab (rpg-factory `testlab/`, PR #17)

The test lab runs real Windows players against the scratch stack (`rpgs-*` containers). It is
not merged yet; citations are at `lab`. Commands, from the rpg-factory checkout, in Windows Python:

```
python3 testlab/testlab.py build-client                                # testlab player (CUVARA_TESTLAB)
python3 testlab/testlab.py run S01 --build-server --server-repo <dir>   # first run: build and stamp the server image
python3 testlab/testlab.py suite readiness                             # S11 S12 S13 S08G S14 S15
python3 testlab/testlab.py sweep S07 --levels 0,10,25                   # load levels; reports the first failing level
python3 testlab/testlab.py run S09 --duration 3600 --snapshot-trace     # 1 h soak with the server delivery trace
python3 testlab/testlab.py analyze <run_dir>                           # re-grade stored evidence
python3 testlab/testlab.py analyze --delivery <run_dir>                # diagnostic only, never PASS
python3 testlab/testlab.py replay-trace <run_dir> --client 0           # validate a predictor call trace
python3 testlab/testlab.py thresholds --write                          # regenerate docs/THRESHOLDS.md
python3 testlab/testlab.py leak-check <player build>                   # no test code in a shipping player
```

Sources: `lab:testlab/README.md:12-23`, `:211-224`, `:226-270`; `lab:testlab/testlab.py:359-393`;
suites in `lab:testlab/suites.json`. `run`, `suite`, `sweep`, `stack` and the builds work only in
Windows Python; `analyze`, `replay-trace`, `leak-check` and the unit tests also run in WSL
(`lab:testlab/README.md:25-31`).

- `--build-server` builds the scratch images from `--server-repo` and stamps the server SHA into the
  image label; provenance then checks the label (`lab:testlab/README.md:200`). Without it, a server
  worktree with no built Nakama module fails preflight (`:158`).
- `--snapshot-trace` refuses to start unless the server's compose file forwards
  `GAMESERVER_SNAPSHOT_TRACE`, which is true only on develop (`lab:testlab/README.md:218`).
- A sweep writes `sweeps/<stamp>-<id>.json` with `first_failing_level`; that is the first failing
  level on this host, not a server ceiling (`lab:testlab/testlab.py:133-157`; `README.md:293`).
- S09 v4 repeats its 300 s cycle for the whole `--duration` (`repeat_cycle_s`). Soaks before v4 were
  5 minutes active and 55 minutes idle and are not comparable (`lab:testlab/README.md:133`).

**Which scenario covers what** (`lab:testlab/docs/COVERAGE.md:21-41`, which names the live runs
behind each status):

| Area | Status | Scenarios |
|---|---|---|
| Tick-rate handling | COVERED | S12 (+S11, S13, S01, S02, S04, S05, S07, S09) |
| Reconciliation / replay | PARTIAL | S02-S06, S08-S10, S12 |
| Snaps after stalls | COVERED | S13 (with the predictor call trace) |
| Interpolation at rest | COVERED | S02, S05 |
| Interpolation in motion | COVERED | S03, S06, S10 |
| Interpolation after respawn | COVERED | S14 (+S02, S05, S09) |
| Snapshot ordering / freshness | COVERED | all (`snapshot_order`); S11, S12 (`render_freshness`) |
| Entity spawn / despawn / id reuse | COVERED | S04, S09, S08G, S02 |
| Join | COVERED | S01, all |
| Disconnect / reconnect | COVERED | S08 (drop), S08G (graceful leave), S11 (server restart) |
| Resync after server restart | COVERED | S11 |
| Multi-client convergence | COVERED | S06, S09, S11, S12, S03, S10 |
| Player spawn distribution | COVERED | S01, S04, S05, S10 |
| Player respawn | COVERED | S14 |
| Enemy target acquisition | COVERED | S02, S05, S10 |
| Enemy separation | COVERED | S02, S05, S10 |
| Enemy bounded movement | PARTIAL | S11, S02, S05, S10 |
| Enemy budget / overload | PARTIAL | S02, S04, S05, S07, S09, S10 |
| Combat authority | COVERED | S15, S04 |

S14 and S15 now use server support merged into develop: S14 kills players through `POST /debug/kill`
(`GAMESERVER_DEBUG_ENTITIES=on`), and S15 grades the per-reason rejection series. S15 grades
**cooldown and reach**: casts faster than the cooldown must be refused as `ability_on_cooldown`, and
a cast aimed beyond range must not damage anything beyond reach
(`lab:testlab/scenarios/S15-combat-negative.json:6`). S13 v2 records the predictor call trace for
the stalled client (`lab:testlab/scenarios/S13-client-stall.json:4`, `:34`).

**Pick scenarios for your change:**

| You changed | Run at least |
|---|---|
| Movement or a movement mode | S03, S06, S10, S12 |
| Combat, an ability, a projectile | S04, S15 |
| Death, respawn, a teleport | S14, S02 |
| Enemy AI or an enemy type | S02, S05, S10; `sweep S07` for load |
| Spawn, despawn, lifecycle, reconnect, leave | S04, S08, S08G, S11 |
| Prediction, interpolation, clock, transport | S13 (x3), S12, S06, then a 1 h S09 |

**Reading a verdict.** Each assertion is PASS, FAIL, INCONCLUSIVE or INFO. INFO never decides a
run on its own. Missing evidence is INCONCLUSIVE, never PASS. A run is (`lab:testlab/lab/analyze.py:1600-1610`):

1. INCONCLUSIVE on a harness error;
2. otherwise FAIL if any assertion fails;
3. otherwise INCONCLUSIVE if any assertion is inconclusive or there are no results;
4. otherwise **PASS_WITH_WARNINGS** if an informational assertion would have failed (`:1608-1609`,
   `:1613-1616`);
5. otherwise PASS.

Exit codes: 0 PASS, 1 FAIL, 2 INCONCLUSIVE, 3 harness or usage error, **4 PASS_WITH_WARNINGS**
(`lab:testlab/README.md:19`). Exit 4 is never a clean PASS; a script that checks for 0 must treat it
as a warning.

**Qualifications.** Host-wide stalls in the run, or CPU load of 50% or more at start or end, are
listed under **Qualified by** next to the verdict (`lab:testlab/lab/analyze.py:1619-1638`;
`HOST_LOAD_WARN_PCT` `:25`; `README.md:21`). They do not change the outcome, because the checks a
stall explains are already INCONCLUSIVE. Read them before treating a PASS as representative.

**Thresholds.** Every limit a check uses is listed in `lab:testlab/docs/THRESHOLDS.md`, generated by
`testlab.py thresholds --write`, with its derivation or the mark **"policy, unvalidated"**
(`:1-6`). A test fails when the file is out of date. Change a limit there, with its reason; never
loosen one to make a run pass.

**Provenance is required.** Every run records the factory, server and client SHAs, the server image
id and revision label, the player hashes, the embedded Netcode commit, the scenario hash, host load,
and a hash manifest of the evidence (`lab:testlab/lab/provenance.py:170-186`). The manifest must
match an expected inventory for the scenario, including a traced client's `trace.ndjson`
(`:43-57`). A missing, changed, added or omitted item makes the `provenance` assertion INCONCLUSIVE
(`:198-256`), so the run cannot PASS.

**A contaminated run is INCONCLUSIVE, not PASS.** The lab enforces this for host stalls: a stall
shared by separate processes is the host, and checks it explains are INCONCLUSIVE until a rerun on a
quiet host (`lab:testlab/lab/analyze.py:1531`). Apply the same rule by hand to contamination the lab
cannot see: test instrumentation that costs frame time, a second workload on the host, a remote
desktop session, or a stale image. Record host load with every run, and label any number from this
host "shared-host".

**`analyze --delivery` and the clock.** It joins the C1 server trace with the client receive log
and classifies each receive gap over 250 ms as RECONNECT, SERVER_LATE, QUEUE, KCP, PATH, CLIENT or
UNKNOWN, scoped ALL-CLIENTS or SINGLE-CLIENT (`lab:testlab/lab/delivery.py:31`, `:66`). A gap across a
session change is RECONNECT, never SERVER_LATE (`:143`, `:314`). It fits `receive = a + rate x write`
per client; with fewer than 5 bins or a residual p90 above 50 ms, one-way delay is reported as
unavailable, never as a number (`:264`, `:294`; `lab:testlab/README.md:224`). On the WSL host every
1 h soak so far gave an unusable fit (skew +8% to +11%, residual p90 1-2 s), so read the gap classes
and the server trace, not a one-way delay. It also loads both traces fully: about 2.66 GB peak on a
1 h soak (`.testlab-runs/reanalysis/T45/measure45.log`, run `20261022T200348Z-S09-uc3e`).

### How to debug a prediction problem

Do not model the client's frame order by hand: three synthetic harnesses got it wrong before a
recorded trace showed it. Record the live calls, then replay them exactly.

1. **Record.** Run the scenario that shows the problem with the predictor call trace on for the
   affected client: S13 already has it on client 0 (`"predictor_trace": true`,
   `lab:testlab/scenarios/S13-client-stall.json:34`); any scenario can set it per client
   (`lab:testlab/README.md:226-240`). The player must be built from client #155 with Netcode
   `>= 8986908`; it writes `client-N/trace.ndjson`: a header (the predictor's construction
   `PredictionSettings`, map id, motor), a session line per join, a frame marker per frame, and one
   line per predictor or steering call (`client#155:docs/TESTLAB.md:185`). The header's `tick_rate`
   is the predictor's construction rate; the server's rate arrives later as a traced
   `UseServerTickRate` call.
   ```
   python3 testlab/testlab.py run S13
   ```
2. **Validate and locate.** `replay-trace` checks that the trace is complete and replayable and
   lists every correction and snap per scripted stall (`lab:testlab/README.md:242-255`). Exit 0 valid,
   1 invalid, 2 no trace.
   ```
   python3 testlab/testlab.py replay-trace <run_dir> --client 0 --out <dir>
   ```
3. **Replay exactly in Netcode.** Trim the trace (gzip), copy the map JSON
   (`backend/content/maps/<map>.json`), and add both as a fixture with a `PROVENANCE.md`, as
   `nc-0.48.0:Tests/Editor/Fixtures/S13Traces/` does. Follow `LiveTraceReplayTests`:
   `TheRecordedSessions_ReplayExactly` replays with the fix switched off and must reproduce every
   recorded reconcile bit for bit (`nc-0.48.0:Tests/Editor/LiveTraceReplayTests.cs:189`); the replayer is
   `PredictorTraceReplayer` (`nc-0.48.0:Runtime/Prediction/PredictorTrace.cs:211`; recipe in
   `nc-0.48.0:Documentation~/PREDICTION.md:718`). If the replay is not exact, fix the recording first.
4. **Fix and prove.** Change the predictor, then assert the intended behaviour on the same replay
   (pattern: `WithTheFix_EachStallCostsAtMostOneCorrection`, `:212`). Keep the existing synthetic
   tests green.
5. **Live re-check.** Build a verification player with the fixed Netcode embedded and rerun the
   scenario at least three times. The S13 fix was accepted only after 3 of 3 live runs showed 0
   corrections and 0 snaps with valid traces.

The trace allocates in its sink (string formatting per call); enable it only in test players.

### Last gate run (shared host)

The readiness gate on 2026-10-23 ran server `integ` `0b7a51a`, a player with Netcode commit 36a9275 (now released as v0.48.0 at `51b083e`)
embedded on client 2eb4d93, and harness `eaad3ba` (the RECONNECT classifier, before the merge of
main that made `a31d749`). Every run had provenance PASS.

| Item | Result | Runs |
|---|---|---|
| Readiness S01-S15 | 14/14 PASS after one rerun: S11 `jbes` failed from two external 4.8-4.9 s client freezes outside its restart window, rerun `c2t4` PASS | `20261023T065332Z-S01-a6dz` … `20261023T071251Z-S11-c2t4` |
| S13 (stall) | PASS 3/3: 0 local corrections and 0 snaps at both stalls, traces VALID | `l5la`, `vnna`, `a2sk` |
| S12 (30 Hz) | PASS 2/2 | `z7jv`, `7loh` |
| Sweeps at 25 bots | S07 PASS, S07P PASS, qualified by host load 55-71% | `sgmz`, `ikzq` |
| 1 h soak | FAIL from one external event only (section 8, item 1) | `20261023T071851Z-S09-i9fl` |

---

## 8. Known limitations at the time of writing

1. **Snapshot delivery gaps in the 1 h soak** ([#473](https://github.com/Cuvara/rpg-mmo-server/issues/473)).
   - **Not reproduced** in seven later 1 h soaks on the shared host: `20261022T163744Z-S09-sfj5`,
     `20261022T175111Z-S09-y6vg`, `20261022T200348Z-S09-uc3e`, `20261022T222551Z-S09-pf8e`,
     `20261023T015749Z-S09-4zym`, `20261023T040945Z-S09-e26z` and `20261023T071851Z-S09-i9fl`.
     This is not a claim that #473 is fixed.
   - **The original eeoa gaps were instrumentation stalls:** the test overlay's frame cost on the
     clients, since fixed in the test lab. Later single-process stalls came from the TestLab writing
     its files on the main thread, also fixed (`BackgroundFileWriter`,
     `client#154:Assets/TestLab/Runtime/BackgroundFileWriter.cs:32`). Neither is evidence of server
     lateness.
   - **The tools exist:** the server delivery trace (PR #474) and `analyze --delivery`.
   - **Hypotheses H1-H8**, read against the traces of every soak: blocking `SendTo` under the
     session lock (H2), the send-queue soft limit (H3), a closed remote window (H4), growing
     per-send work (H7) and larger snapshots (H8) are **refuted** (server write p99 at most 0.18 ms,
     queue depth at most 1, remote window at least 120 outside client freezes, flat bytes). A path
     outage with KCP head-of-line blocking (H1) is **not the cause**: KCP retransmissions rise only
     after a client stops acknowledging. An update-thread stall (H5) is **undetermined**, with no
     evidence for it. Delay above the client's KCP layer (H6) is **confirmed**: every long gap
     begins in a client frame stall or freeze.
   - **RC soak:** the final soak `20261023T071851Z-S09-i9fl` FAILs only because of one 4.7 s
     external freeze of client 1, which coincided with an AnyDesk session starting. The session
     closed (`dead_link` on the server, TransportError on the client), and the client reconnected
     in 0.42 s. Every failing assertion (kcp, snapshots, entity_lifecycle, cross_client_convergence)
     comes from that event; the rest of the hour is clean (0 other gaps over 250 ms, no other frame
     of 100 ms or more, writer backpressure 0).
2. **Shared host.** The lab host also runs the user's parallel sessions, and remote-desktop
   sessions: both sampled 4.5-5 s single-process freezes coincided with an AnyDesk session process
   starting. Host-wide freezes of 0.3-5.4 s also occurred, with no Windows event logged and no cause
   found. A gate-quality soak needs a quiet host and no remote desktop session.
3. **WSL clock about 8% slow** on the lab host (section 3). Server rates in Windows time are about
   55/13.8 Hz, the rate wanders, and one-way delay across the boundary is not measurable. Changing
   the WSL clock source is the user's decision.
4. **`AckLatencyEstimator` floor flapping.** The estimator can alternate between refusing and
   accepting its floor, which moves the lead target by about 4 ticks and costs a ~2-step correction,
   also with no stall (section 3). Open in Netcode.
5. **One-step corrections at a start or stop.** Some direction changes cost exactly one smoothed
   step (0.083 units at the defaults), never a snap. Cause: when an input arrives relative to the
   server's 60 Hz drain decides which tick applies it, and the predictor anchors on one stable
   offset (`nc-0.48.0:Runtime/Prediction/LocalMovePredictor.cs:3290`). Classified as inherent quantisation,
   not a defect. Related: [Netcode#183](https://github.com/Cuvara/Netcode/issues/183).
6. **`ability_out_of_range` cannot be produced from the client.** The client casts only a
   projectile ability, and a projectile's aim point has no range check by design
   (`develop:SGL/Systems/AbilityLogic.cs:137-142`; ADR-29 d5). S15 therefore grades cooldown and
   reach, not the out-of-range rejection. Testing it needs an entity- or ground-targeted cast path
   on the client, or a server-side bot.
7. **Delivery analysis memory.** `analyze --delivery` peaks at about 2.66 GB on a 1 h soak
   (section 7). The run grading (`analyze`) is streamed and stays near 1.1 GB.
8. **Combat logout is not decided.** A graceful leave releases the player at once, also in combat
   (section 5). Any combat-logout rule is the user's decision.
9. **Client CI is blocked.** The client repository variable `UNITY_VERSION` is 6000.0.26f1 while
   `ProjectSettings/ProjectVersion.txt` is 6000.3.9f1, so every client CI run fails at "Resolve
   Build Config" and executes 0 tests. Fixing the variable is the user's action.
10. **Client pins pending.** Netcode v0.48.0 is released at `51b083e`; its client pin update remains
    pending PR #155. UnityDots #37 and its client pin are also pending. Until those pin updates land,
    the shipping client lacks behavior that depends on the newer versions. The leave flush also
    depends on the player loop: at application quit the leave may not finish.
11. **UnityDots fallback path draws (0,0) while unseeded.** When the local entity is not in
    `WorldState`, `LocalPredictionSystem` still writes `predictor.Position`, which is (0,0) until the
    predictor is seeded (`dots-pending:Runtime.Netcode.Prediction/LocalPredictionSystem.cs:248-279`). Once UnityDots PR #37 lands, a reconnect can expose this for a frame or more on that path only.
12. **UnityDots default `MaxExtrapolation`.** `DotsNetcodeBootstrap.Install(world, view)` passes
    `default(InterpolationConfig)` through `Normalized()`, so the ECS timed path gets
    `MaxExtrapolation = 0` (never extrapolate), while the binder uses 0.050 s
    (`dots-pending:Runtime.Netcode/DotsNetcodeBootstrap.cs:36`, `:77`). The shipping client feeds the untimed
    path, so this does not change what it draws today. Pass a configured value for the timed path.
13. **Respawn requires the enemy phase**, and there is no respawn grace period (section 5).
14. **Anti-cheat only records** ([#457](https://github.com/Cuvara/rpg-mmo-server/issues/457)).
