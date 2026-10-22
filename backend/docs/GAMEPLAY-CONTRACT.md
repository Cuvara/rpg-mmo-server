# Core gameplay developer contract

**Audience:** a developer or agent about to implement a new core gameplay feature: an ability, an
enemy type, an item effect, a movement mode. This document states what must stay true and how to
prove it. It does not define gameplay numbers. Every number below is a code or config default,
quoted with its source. Changing one is a gameplay decision for the user, not an implementation
detail (see [#467](https://github.com/Cuvara/rpg-mmo-server/issues/467): every gameplay number is a
placeholder).

**Status:** written 2026-10-23. Part of this contract lands only with open pull requests. Each such
statement says **"after PR #N"**. A statement without that note is true on `develop`.

## How to read the citations

Each citation is `<commit>:<path>:<line>`. The commit is the one the statement describes. Check a
citation with `git show <sha>:<path> | sed -n '<line>p'` in the named repository.

| Label | Repository | Commit | What it is |
|---|---|---|---|
| `develop` | rpg-mmo-server | `e982a6d` | `origin/develop` when this document was written |
| `#472` | rpg-mmo-server | `11f0140` | PR #472: enemy separation, spawn scatter, enemy budget, testlab support |
| `#474` | rpg-mmo-server | `1dc89ed` | PR #474 (stacked on #472): snapshot delivery trace, KCP counters, #471 fix |
| `#475` | rpg-mmo-server | `c7ad277` | PR #475 (stacked on #472): debug kill hook, per-reason combat rejections, map bounds on `/status` |
| `integ` | rpg-mmo-server | `4b2c1c2` | `integ/testlab/core-readiness`: #472 + #474 + #475 + review fixes. Integration branch, not for merge |
| `nc-0.47.0` | Netcode | tag `v0.47.0` | The Netcode version the Unity client pins today |
| `nc` | Netcode | `a5d09ce` | Netcode PR #185 head, stacked on PR #184 (`5fa8f37`). `package.json` says 0.48.0, but **no `v0.48.0` tag exists** |
| `dots` | UnityDots | `bfdfefb` | UnityDots PR #37 head. The client pins `v0.31.0` |
| `client#154` | IndieRPGMMOAdventure | `2aa4965` | Client PR #154 head (test lab) |
| `client#155` | IndieRPGMMOAdventure | `22264dc` | Client PR #155 head (draft, stacked on #154; needs Netcode >= 0.48.0) |
| `lab` | rpg-factory | `26429ce` | rpg-factory PR #17 head (the test lab) |

Path abbreviations: `GS/` = `backend/gameserver-dotnet/GameServer/`,
`GST/` = `backend/gameserver-dotnet/GameServer.Tests/`, `SGL/` = `backend/gameserver-dotnet/Shared.GameLogic/`,
`ADR` = `backend/docs/ARCHITECTURE-DECISIONS.md`.

**Client version caveat.** The shipping client resolves Netcode `v0.47.0`
(`client#154:Packages/manifest.json:24`). Everything below that cites `nc` reaches the client only
after Netcode PR #184/#185 merge, `v0.48.0` is tagged, and the client pin moves (client PR #155).
Test-lab verification players embed an untagged Netcode commit (for example `ba67b94`); that is
"embedded ba67b94", not a released 0.48.0.

---

## 1. Authority

### The server owns the world

| The server decides | Where | Source |
|---|---|---|
| Position. The client sends a direction; the server integrates `direction * speed * dt` itself | `InputHandler` | `develop:GS/Input/InputHandler.cs:662-664`, step at `:692-698` |
| HP, damage and combat resolution | `InputHandler` (attacks), `StepGameplay` (projectiles, statuses) | `develop:GS/Input/InputHandler.cs:17-21`; `develop:GS/Server/TickLoop.cs:491-492` |
| Spawn on join | `PlayerSpawn.Resolve` | `develop:GS/Server/GameServer.cs:1673`, `:1713` |
| Respawn: dead for at most one world tick, then full HP at the spawn point | `PlayerRespawnSystem` (World group) | `develop:GS/Scaffolding/EnemyCombatSystems.cs:291-298`, `:330-334`, `:363-373` |
| Respawn point scatter | `ScatterSpawn` | after PR #472: `#472:GS/Scaffolding/EnemyCombatSystems.cs:376` |
| Enemy AI: spawn, move, reap | Enemy AI phase, before broadcast | `develop:GS/Server/TickLoop.cs:589-594` |
| Hits of projectiles, lag-compensated by at most 200 ms; the server never trusts a client origin | ADR-29 | `ADR` heading `develop:…:4113`; `:4137-4141` |

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
   `_world.PushInput` (`develop:GS/Server/GameServer.cs:2076-2104`). The outcome (coalesced,
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
owns the user (`develop:GS/Server/GameServer.cs:2040-2043`). A player entity's id is its user id
(`develop:GS/Persistence/CharacterSessions.cs:9`). On the client, one scene has one input source and
one predicted local entity (UnityDots selects exactly one: after PR UnityDots#37,
`dots:Runtime.Netcode.Prediction/LocalPredictionSystem.cs:94` queries the `LocalNetworkEntity` tag).

### Rejections

Every refused input has a typed reason (`develop:GS/Input/InputRejection.cs:37`, 16 values at
`:43-175`):

`entity_gone`, `dead_entity`, `stale_tick`, `invalid_direction`, `attack_target_unresolved`,
`attack_target_dead`, `attack_out_of_range`, `attack_on_cooldown`, `attack_other`,
`ability_unknown`, `ability_on_cooldown`, `ability_out_of_range`, `ability_target_unresolved`,
`ability_target_dead`, `ability_caster_dead`, `ability_other`.

After PR #475 four more reasons split `attack_other` and `ability_other`
(`#475:GS/Input/InputRejection.cs:187`, `:195`, `:203`, `:215`): `attack_target_untargetable`,
`attack_cannot_act`, `ability_cannot_cast`, `ability_missing_aim`.

Each reason has a `RejectionWeight`: `Benign`, `LatencyExplicable` or `Forged`
(`develop:GS/Input/InputRejection.cs:197-220`; table at `:274-312`). Only `invalid_direction` is
`Forged` (`:310`).

**Observe the rejections:**

- Metric `gameserver_inputs_rejected_total{reason}` (`develop:GS/Observability/GameMetrics.cs:375-396`).
  It is not the same as `gameserver_inputs_dropped_total{reason}` (admission drops) or
  `gameserver_inputs_coalesced_total` (`:49-50`, `:370-373`, `:418-422`).
- `/status` fields `inputs_rejected` and `inputs_rejected_by_reason`
  (`develop:GS/Observability/ServerStatus.cs:691`, `:695`).

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

- The knobs are read in `develop:GS/Program.cs:98-110` (also `--sim-*-hz` flags; the legacy
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
  rate (`develop:GS/Server/GameServer.cs:1919`; `develop:GS/Server/SimulationRates.cs:122`). The
  same value is on `/status` (`develop:GS/Observability/ServerStatus.cs:793`). A client that sees 0
  must refuse to predict (`develop:backend/shared/proto/wire.proto:325-331`).
- Netcode `LocalMovePredictor.UseServerTickRate(uint)` adopts it: a different rate re-derives the
  timestep and the catch-up budget and resets the predictor (`nc:Runtime/Prediction/LocalMovePredictor.cs:731`,
  remarks `:710-716`). **It does not exist in `nc-0.47.0`.** The client calls it only after client
  PR #155 (`client#155:Assets/Scripts/DI/Dots/DotsWorldBridge.cs:643`), which needs a Netcode
  `>= 0.48.0` pin.
- Until then the shipping client predicts at its configured rate. A server started with a
  non-default critical rate is therefore not supported by the shipping client.

### Prediction, reconciliation and replay

- History ring: 256 ticks (`nc:Runtime/Prediction/LocalMovePredictor.cs:309`; the same in
  `nc-0.47.0`). A replay longer than the ring keeps the newest 256 ticks exact
  (`StageReplayedHistory`, `nc:…/LocalMovePredictor.cs:365`; test
  `ReplayHistoryStagingTests.AReplayLongerThanTheHistoryRing_KeepsTheNewestTicksExact`).
- Corrections up to `SmoothingThreshold = 0.5` units are smoothed; a larger correction snaps and
  increments `Snaps` (`nc:…/LocalMovePredictor.cs:226`, `:1758`, `:1770`).
- The server reports which input it applied last (`AckTick`, `AckAppliedTick`;
  `develop:GS/Snapshot/SnapshotDeltaState.cs:1130-1132`). Normative reference:
  `backend/gameserver-dotnet/docs/API.md` "Reconciliation" and "Which history entry a snapshot
  describes" (`develop:…/API.md:1446`, `:1459`).

### Clock steering and stalls

- The predictor steers its clock onto the server's tick line. A clock error above
  `HardResyncTicks` (2 s of ticks) forces a hard resync (`nc:…/LocalMovePredictor.cs:2031`).
- **Client stall (D1, after Netcode PR #185).** After a frame clamp, an error outside the 8-tick
  steering band (`SteerIntegralBandTicks`, `:2318`), or a clock that is behind by more than the
  catch-up budget, jumps the clock onto the line (`JumpClock`, counted in `StallResyncs`;
  `nc:…/LocalMovePredictor.cs:2096-2112`, `:952`, `:976`). Before this fix a 2 s stall cost up to 80
  whole-step corrections. The defect dates from Netcode v0.20.0; it is not a 0.48.0 regression.
- **Server stall.** One second without snapshots followed by a burst corrects nothing
  (`ServerStallRecoveryTests.NoSnapshotsForASecondThenABurst_CorrectsNothing`, Netcode headless suite).
- `PredictionClockSteering.Reset()` forgets the tick-rate, staleness and ack-latency estimators
  (`nc:Runtime/Prediction/PredictionClockSteering.cs:560-567`). Before `a5d09ce` it kept
  `AckLatency` across sessions.

### Lab caveat: the WSL VM clock

On the shared development host the WSL VM's monotonic clock ran about 7% slow against Windows
(ratio 0.927 over 60 s; evidence `.testlab-runs/t06/clock-drift-check.txt` in the workspace). A
server in WSL configured at 60/15 Hz then runs at about 55/13.8 Hz of Windows time. This is a
property of that lab host, not of the game. Do not quote a rate or a latency measured across the
Windows/WSL boundary without this caveat, and do not "fix" the game for it.

---

## 4. Snapshots and replication

### Keyframes and deltas

- A full snapshot goes out on join, on `MsgResync`, and every `GAMESERVER_KEYFRAME_INTERVAL`
  snapshots (default 30; `develop:GS/Snapshot/SnapshotDeltaState.cs:23-25`,
  `develop:GS/ServerEnv.cs:101-102`, `develop:SGL/Components/GameConstants.cs:142`). Deltas carry
  changed entities and a `Removed` list (`develop:GS/Snapshot/SnapshotDeltaState.cs:1334`).
- `MsgResync` forces the next snapshot to be full (`develop:GS/Server/GameServer.cs:2116-2121`).
- The server treats "last sent" as "last received". That is only correct because KCP is reliable
  and ordered (`develop:GS/Snapshot/SnapshotDeltaState.cs:27-32`).
- Normative client merge algorithm: `develop:backend/gameserver-dotnet/docs/API.md:1274`; keyframe
  policy `:1396`.

### Area of interest

- AOI radius `GAMESERVER_AOI_RADIUS`, default 50 units (`develop:GS/Server/AoiSettings.cs:21`;
  `develop:SGL/Components/GameConstants.cs:132`).
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
  (`develop:GS/Observability/ServerStatus.cs:587`, `:644`); metrics
  `gameserver.snapshots.max_update_gap_ms` and `.max_shed_age`
  (`develop:GS/Observability/GameMetrics.cs:23`, `:231`, `:496`).
  On `develop`, an entity shed from the join keyframe charges the server's whole uptime to
  `snapshot_max_update_gap_ms` ([#471](https://github.com/Cuvara/rpg-mmo-server/issues/471)).
  After PR #474 the gap counts from the join (`#474:GS/Snapshot/SnapshotDeltaState.cs:605-615`).
  Until then, do not read that gauge under shedding.
- **Coalescing per connection.** The server stages one snapshot per connection. A newer gather
  replaces an unclaimed one and is counted in `gameserver_snapshots_coalesced_total`
  (`develop:GS/Net/Connection.cs:360-371`, `:684`).

### Ordering and freshness on the client

- KCP delivers in order, but the client still rejects old data:
  - `WorldState.Apply` drops a snapshot older than the current tick, and a second snapshot at the
    same tick, and counts it in `StaleSnapshotsDropped` (after Netcode PR #185;
    `nc:Runtime/World/WorldState.cs:204-206`, `:334`). Netcode `ba67b94` dropped only older ticks;
    `nc-0.47.0` and `5fa8f37` merged an older snapshot (defect D2).
  - The interpolation clock ignores a tick at or before its newest one
    (`nc:Runtime/Interpolation/InterpolationClock.cs:108`). The sample ring rejects a duplicate or
    reordered tick (`nc:Runtime/Interpolation/InterpolationRing.cs:21-23`).
- A new gameplay field must survive this: never rely on applying a snapshot twice.

### Interpolation and extrapolation (remote entities)

Defaults from `nc:Runtime/Interpolation/InterpolationConfig.cs:189-203`:

| Field | Default | In `nc-0.47.0` |
|---|---|---|
| `TargetDelay` | 0.100 s | yes |
| `MaxExtrapolation` | 0.050 s | yes |
| `ExtrapolationHold` | 0.150 s | no |
| `ExtrapolationSettle` | 0.100 s | no |
| `MaxExtrapolationSpan` | 0.250 s | no |
| `MaxExtrapolationSpeed` | 0 (off) | no |

Semantics of hold and settle: `nc:Documentation~/INTERPOLATION.md:49-68`. `default(InterpolationConfig)`
normalises hold, settle and span to these defaults, but `MaxExtrapolation` stays 0, which means
"never extrapolate" (`nc:Runtime/Interpolation/InterpolationConfig.cs:232-257`). Client PR #155 uses
`InterpolationConfig.Default` with `MaxExtrapolationSpeed = 15` units/s
(`client#155:Assets/Scripts/DI/Dots/DotsWorldBridge.cs:93`, `:346-348`).

### Teleports, including respawn

- **Local player: a respawn is a snap, by design.** A respawn moves the authoritative position
  further than `SmoothingThreshold`, so the predictor snaps once and does not re-apply it. Test-lab
  runs on 2026-10-22 matched client snaps to server respawns one to one (543 snaps = 543 respawns
  over a 1 h soak). An idle player near enemies is killed and respawned about every 18.5 s at the
  current enemy defaults, so "snaps at idle" are respawns, not a prediction defect.
- **Remote entities.** A last segment faster than `MaxExtrapolationSpeed` or longer than
  `MaxExtrapolationSpan` is held at its destination, not carried past it
  (`nc:Runtime/Interpolation/InterpolationConfig.cs:86-109`; `nc:Documentation~/INTERPOLATION.md:116`).
  Both guards need Netcode `>= 0.48.0`.
- A new movement mode that moves an entity discontinuously (dash, blink, portal) is a teleport for
  both rules. Test it against both.

---

## 5. Entity lifecycle

### Server

- **Spawn.** A player spawns on join (`develop:GS/Server/GameServer.cs:1673`, `:1713`). Its entity
  id is its user id.
- **Despawn.** A despawn reaches clients in the snapshot's `Removed` list
  (`develop:GS/Snapshot/SnapshotDeltaState.cs:1334`). A deferred despawn is offered again (`:294`).
- **Disconnect hold.** The server holds a disconnected player for 30 s on a map and 60 s in a
  dungeon (`develop:GS/Program.cs:1042`). There is no knob for this window.
- **Respawn.** `GAMESERVER_PLAYER_RESPAWN` is on by default
  (`develop:GS/Scaffolding/EnemyAiSettings.cs:63`). Respawn lives in the enemy phase: with
  `GAMESERVER_ENEMIES` off, a killed player stays dead.
- **Spawn scatter** (after PR #472): `GAMESERVER_SPAWN_SCATTER`, default 3 units
  (`#472:GS/ServerEnv.cs:171`; `#472:GS/Server/ServerDefaults.cs:28`). Spawns and respawns land on a
  scattered point instead of one shared spawn ([#469](https://github.com/Cuvara/rpg-mmo-server/issues/469)).

### Enemy population

| Knob | Default | On develop? | Source |
|---|---|---|---|
| `GAMESERVER_ENEMY_MAX` | 30 | yes | `develop:GS/Scaffolding/EnemyAiTuning.cs:65` |
| `GAMESERVER_ENEMY_MAX_PER_PLAYER` | 45 | yes | `develop:GS/Scaffolding/EnemyAiTuning.cs:74` |
| `GAMESERVER_ENEMY_BUDGET` | 300 | after PR #472 | `#472:GS/Scaffolding/EnemyAiTuning.cs:170` |
| `GAMESERVER_ENEMY_SEPARATION` | 0.8 units | after PR #472 | `#472:GS/Scaffolding/EnemyAiTuning.cs:154` |
| `GAMESERVER_ENEMY_SEED` | (unset) | after PR #472 | `#472:GS/Scaffolding/EnemyAiSettings.cs:71` |

- **Cap.** `cap = MAX + MAX_PER_PLAYER * live players`, clamped to `BUDGET` after PR #472
  (`#472:GS/Scaffolding/EnemyAiSettings.cs:292-303`). At 3 players the cap is 165, so the budget
  binds only above 6 players ([#468](https://github.com/Cuvara/rpg-mmo-server/issues/468)).
- **Confirm the values in force on `/status`:** `enemy_ai` (after PR #472 it includes `budget=` and
  `separation=`; `#472:GS/Scaffolding/EnemyAiSettings.cs:438-440`), `enemy_ai_max_now`,
  `enemies_alive`, `player_respawns` (`develop:GS/Observability/ServerStatus.cs:139`, `:459`, `:480`,
  `:521`).
- **Separation** keeps enemies chasing one player from stacking on one point. The review finding
  that its neighbour bound could be exceeded through hash-chain collisions is fixed on `integ`
  (`22d6359`) and not yet on PR #472.

### Client: sessions, generations, reconnect

- **Generation.** Every connect increments the client's session generation
  (`nc:Runtime/Client/NetworkClient.cs:305`). Commands from an older generation are dropped by
  the DOTS view.
- **World reset.** `NetworkClient.RunConnectAsync` calls `World.Reset()` on every connect and
  reconnect (`nc:Runtime/Client/NetworkClient.cs:469`).
- **Reconnect resets.** On `Reconnected` the client must reset, in this order: the view generation,
  the binder, the predictor and the projectile predictor
  (`client#155:Assets/Scripts/DI/Dots/DotsWorldBridge.cs:510-536`; on `client#154` at `:499-524`).
  The binder reset (defect F2) matters when the new server's ticks are lower than the old one's
  (reconnect to a restarted or different pod).
- **Fallbacks for a missed reset:**
  - The binder restarts its tick line when the world tick goes backwards and counts it in
    `TickLineRestarts` (after Netcode PR #185; `nc:Runtime/View/WorldViewBinder.cs:512-516`, `:928-942`).
  - The UnityDots timed path resets its timeline clock on the `Reset` command (after PR UnityDots#37;
    `dots:Runtime.Netcode/NetworkViewCommandSystem.cs:190-200`).
  - `LocalPredictionSystem` resets its ack/anchor memory and the steering on a new generation or a
    backwards tick, **but not the predictor**. Resetting the predictor stays the host's job
    (`dots:Runtime.Netcode.Prediction/LocalPredictionSystem.cs:121-129`;
    `dots:Documentation~/NETCODE-INTEGRATION.md:120-124`).
- **Reconnect policy.** Which close reasons reconnect: `nc:Runtime/Client/ReconnectPolicy.cs:60-112`
  (`PeerClosed`, `HeartbeatTimeout`, `TransportError` reconnect; `ServerDisconnect` reconnects after
  a delay only for `server_shutdown`; local close, protocol error and most kicks do not).
- **Handle reuse.** Snapshot entity ids are interned to handles. A despawned entity's view must not
  survive, and a reused handle must not show the old state. Netcode pins this end to end in
  `EntityLifecycleTests` (headless suite).

---

## 6. Networking expectations

- **KCP/UDP only** for gameplay (ADR-32, `develop:ADR:4210`). Any `GAMESERVER_TRANSPORT` other than
  `kcp` is fatal at startup (`develop:GS/Program.cs:166-170`, `:236-240`). The gateway hop is TCP,
  optionally TLS, and carries no gameplay (ADR-3).
- **Crypto.** Optional datagram PSK `TRANSPORT_KEY` (AES-256; empty = plaintext;
  `develop:GS/Net/Transport/Transport.cs:18`; ADR-8). Sealed session `GAMESERVER_SEALED`, default
  `require` (`develop:GS/ServerEnv.cs:149`; ADR-22, ADR-25). Changing framing, handshake or crypto
  bytes is wire-contract work.
- **KCP limits.** Send window 128, writer soft limit 256 queued segments, idle timeout 60 s
  (`develop:GS/Net/Transport/KcpTuning.cs:20`, `:51`; `develop:GS/Net/Transport/KcpLimits.cs:92`).
  KCP is reliable and ordered: one lost datagram holds every later snapshot until it is
  retransmitted.
- **Bandwidth budget.** `< 50 KB/s` downstream per client (ADR-7, `develop:ADR:771`). Measured:
  45.9 KB/s per client at 200 players (`develop:backend/docs/BENCHMARK.md:1360`, `:1383`). A feature
  that adds snapshot bytes per entity needs a measurement before it merges. The per-server player
  ceiling is unknown (ADR-7; [#205](https://github.com/Cuvara/rpg-mmo-server/issues/205)); do not
  quote one.
- **Delivery trace for diagnosis** (after PR #474). `GAMESERVER_SNAPSHOT_TRACE=<path>` writes one CSV
  row per sent snapshot. Unset = off. A path that cannot be opened exits 2
  (`#474:GS/ServerEnv.cs:165-172`; `#474:GS/Program.cs:355-376`). Columns:
  `conn_id,user_id,world_tick,gather_us,enqueue_us,write_start_us,write_end_us,bytes,queue_depth,dropped_oldest_total,kcp_wait_snd,kcp_snd_wnd,kcp_rmt_wnd,kcp_retrans_total,kcp_rto_ms`
  (`#474:GS/Observability/SnapshotTrace.cs:109-111`). Compose forwards it
  (`#474:backend/deploy/docker-compose.yml:516`); the fleets deliberately do not. Related `/status`
  fields: `snapshot_send_queue_depth_max`, `snapshot_send_dropped_oldest`, `kcp_wait_snd_max`,
  `kcp_rto_max_ms`, `kcp_rmt_wnd_min`, `kcp_segments_retransmitted`, `snapshot_trace_rows_written`,
  `snapshot_trace_rows_dropped` (`#474:GS/Observability/ServerStatus.cs:651-698`); metric
  `gameserver_kcp_segments_retransmitted_total` (`#474:GS/Observability/GameMetrics.cs:36`).
  Development and test only: the file grows without bound and holds every player's user id.

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
      `TickLoop.TickOnce()`.
- [ ] **Allocation guards:** `GST/Snapshot/SnapshotAllocationTests.cs` (snapshot path) and
      `GST/CoreV3/GameplayAllocationTests.cs` (0 B over 600 ticks; `develop:…:45`) run in every
      suite. For a change on the combat or input path, also run
      `BENCH_TICK=1 dotnet test GameServer.Tests -c Release --filter TickAllocationBench`
      (`develop:GST/Bench/TickAllocationBench.cs:44`, `:53`) and compare "Input apply (write scope)"
      against the base commit. A number written into a doc goes through the measurement process
      (`backend/docs/MEASUREMENT.md`).
- [ ] **Golden vectors** for an SGL rule: regenerate only for an intended behaviour change (section 1),
      commit the fixtures, explain every changed case. No behaviour change means an unchanged
      `SGL/GoldenVectors/`.
- [ ] **Docs:** a new knob is a `const string` in `ServerEnv.cs`, parsed strictly (exit 2), listed in
      `backend/gameserver-dotnet/docs/README.md`, and forwarded by compose and the fleets (or
      excluded with a reason). A new metric or `/status` field is in `docs/METRICS.md`.

### Netcode (when the change touches prediction, interpolation or the snapshot merge)

- [ ] **Headless suite:** `dotnet test Tests~/Headless/Cuvara.Netcode.Tests.Headless.csproj -p:SglDir=<Shared.GameLogic path>`
      (`nc:Tests~/Headless/Cuvara.Netcode.Tests.Headless.csproj:87`; CI job "Headless tests (dotnet)",
      `nc:.github/workflows/ci.yml:80-111`). From an Orca worktree, the rpg-factory script
      `scripts/checks/netcode-headless.sh --server <rpg-mmo-server dir>` picks the SGL tag. Check
      the `dotnet test` exit code yourself (section 8).
- [ ] **Unity EditMode** in the Netcode package CI ("Unity Tests").

### Client (when the change touches client code)

- [ ] **EditMode:** `Unity.exe -batchmode -nographics -projectPath <project> -runTests -testPlatform EditMode -testResults <xml> -logFile <log>`.
      Unity 6000.3.9f1. Name the Netcode version that was resolved or embedded.
- [ ] A Unity-MCP plugin error in the log can fail a test through `LogAssert`. Record it as a FAIL
      caused by the environment, never as a PASS.

### Test lab (rpg-factory `testlab/`, PR #17)

The test lab runs real Windows players against the scratch stack (`rpgs-*` containers). It is
not merged yet; citations are at `lab`. Commands, from the rpg-factory checkout, in Windows Python:

```
python3 testlab/testlab.py build-client                 # testlab player (CUVARA_TESTLAB)
python3 testlab/testlab.py run S01                      # one scenario
python3 testlab/testlab.py suite readiness              # S11 S12 S13 S08G S14 S15
python3 testlab/testlab.py run S09 --duration 3600 --snapshot-trace
python3 testlab/testlab.py analyze --delivery <run_dir> # diagnostic only, never PASS
python3 testlab/testlab.py leak-check <player build>    # no test code in a shipping player
```

Sources: `lab:testlab/README.md:13-16`, `:66-75`, `:82-86`, `:206-212`; `lab:testlab/testlab.py:322-330`;
suites in `lab:testlab/suites.json`. `run`, `suite`, `sweep`, `stack` and the builds work only in
Windows Python; `analyze`, `leak-check` and the unit tests also run in WSL (`lab:testlab/README.md:25-31`).
`--snapshot-trace` refuses to start unless the server's compose file forwards
`GAMESERVER_SNAPSHOT_TRACE`, which is true only after PR #474 (`lab:testlab/README.md:210`).
`--build-server` builds the scratch images from `--server-repo` and stamps the server SHA into the
image label.

**Which scenario covers what** (`lab:testlab/docs/COVERAGE.md:21-41`; "unrun" = scenario and checks
exist and are unit-tested, but no lab run has used them yet):

| Area | Status | Scenarios |
|---|---|---|
| Tick-rate handling | COVERED (unrun) | S12 (+S11, S13, S01, S02, S04, S05, S07, S09) |
| Reconciliation / replay | PARTIAL | S02-S06, S08-S10, S12 |
| Snaps after stalls | COVERED (unrun) | S13 |
| Interpolation at rest | COVERED | S02, S05 |
| Interpolation in motion | COVERED | S03, S06, S10 |
| Interpolation after respawn | COVERED (unrun) | S14 (+S02, S05, S09) |
| Snapshot ordering / freshness | COVERED | all (`snapshot_order`); S11, S12 (`render_freshness`) |
| Entity spawn / despawn / id reuse | COVERED (unrun) | S04, S09, S08G, S02 |
| Join | COVERED | S01, all |
| Disconnect / reconnect | COVERED | S08; S08G, S11 (unrun) |
| Resync after server restart | COVERED (unrun) | S11 |
| Multi-client convergence | COVERED (unrun) | S06, S09, S11, S12, S03, S10 |
| Player spawn distribution | COVERED | S01, S04, S05, S10 |
| Player respawn | COVERED (unrun) | S14 |
| Enemy target acquisition | COVERED | S02, S05, S10 |
| Enemy separation | COVERED | S02, S05, S10 |
| Enemy bounded movement | PARTIAL | S11, S02, S05, S10 |
| Enemy budget / overload | PARTIAL | S02, S04, S05, S07, S09, S10 |
| Combat authority | COVERED (unrun) | S15, S04 |

S14 and S15 need PR #475: S14 kills players through `POST /debug/kill`
(`GAMESERVER_DEBUG_ENTITIES=on`), and S15 grades the per-reason rejection series. Against `develop`
S14's kill-dependent assertions are INCONCLUSIVE by design. COVERAGE.md at `lab` still lists the
delivery trace (F8/S3) as open; it is implemented by PR #474.

**Pick scenarios for your change:**

| You changed | Run at least |
|---|---|
| Movement or a movement mode | S03, S06, S10, S12 |
| Combat, an ability, a projectile | S04, S15 |
| Death, respawn, a teleport | S14, S02 |
| Enemy AI or an enemy type | S02, S05, S10; S07 for load |
| Spawn, despawn, lifecycle, reconnect | S04, S08, S08G, S11 |
| Prediction, interpolation, clock | S13, S12, S06, then S09 |

**Reading a verdict.** Each assertion is PASS, FAIL, INCONCLUSIVE or INFO. INFO never decides a
run. Missing evidence is INCONCLUSIVE, never PASS (`lab:testlab/lab/analyze.py:1-8`). A run is FAIL
if any assertion fails; otherwise INCONCLUSIVE if any assertion is inconclusive or there are no
results; otherwise PASS (`:1426-1434`). Exit codes: 0 PASS, 1 FAIL, 2 INCONCLUSIVE, 3 harness error
(`lab:testlab/README.md:19`).

**Provenance is required.** Every run records the factory, server and client SHAs, the server image
id (and, after the stack revision commit on PR #474, its revision label), the player hashes, the
embedded Netcode commit, the scenario hash, host load, and a hash manifest of the evidence
(`lab:testlab/lab/provenance.py:148-164`). Any missing or mismatched item makes the `provenance`
assertion INCONCLUSIVE (`:176-223`), so the run cannot PASS.

**A contaminated run is INCONCLUSIVE, not PASS.** The lab enforces this for host stalls: a stall
shared by separate processes is the host, and checks it explains are INCONCLUSIVE until a rerun on a
quiet host (`lab:testlab/lab/analyze.py:1357-1358`, `:588-589`, `:1730-1731`). Apply the same rule
by hand to contamination the lab cannot see: test instrumentation that costs frame time, a second
workload on the host, or a stale image. Example: the 2026-10-22 1 h soak's snapshot FAIL was traced
to the test overlay's frame cost on the clients and is not evidence of server lateness. Record host
load with every run, and label any number from this host "shared-host".

---

## 8. Known limitations at the time of writing

1. **One-step corrections at a start or stop.** Some direction changes cost exactly one smoothed
   step (0.083 units at the defaults), never a snap. Cause: when an input arrives relative to the
   server's 60 Hz drain decides which tick applies it, and the predictor anchors on one stable
   offset. The predictor documents it as "a genuine, bounded disagreement of at most one step at a
   start or stop" (`nc:Runtime/Prediction/LocalMovePredictor.cs:2975`). Classified as inherent
   quantisation, not a defect. A phase-locked send cadence could remove it; that is a later design
   decision. Related: [Netcode#183](https://github.com/Cuvara/Netcode/issues/183).
2. **UnityDots default `MaxExtrapolation`.** `DotsNetcodeBootstrap.Install(world, view)` passes
   `default(InterpolationConfig)` through `Normalized()`, so the ECS timed path gets
   `MaxExtrapolation = 0` (never extrapolate), while the binder uses 0.050 s
   (`dots:Runtime.Netcode/DotsNetcodeBootstrap.cs:30-36`; `nc:Runtime/Interpolation/InterpolationConfig.cs:232-257`).
   The shipping client feeds the untimed path, so this does not change what it draws today. Pass a
   configured value when you use the timed path.
3. **`ability_out_of_range` cannot be produced from the client.** The client casts only a
   projectile ability, and a projectile's aim point has no range check by design
   (`develop:SGL/Systems/AbilityLogic.cs:137-142`; ADR-29 d5). Testing that rejection needs an
   entity- or ground-targeted cast path on the client, or a server-side bot.
4. **Snapshot delivery gaps in the 1 h soak** ([#473](https://github.com/Cuvara/rpg-mmo-server/issues/473)).
   TODO (Lead): fill in the status and root cause after the soak analysis with the PR #474 delivery
   trace.
5. **`snapshot_max_update_gap_ms` reads the server's uptime under shedding** on `develop`
   ([#471](https://github.com/Cuvara/rpg-mmo-server/issues/471)); fixed after PR #474.
6. **The shipping client does not adopt the server's tick rate**, and lacks the D1, D2 and binder
   tick-line fixes, until Netcode `v0.48.0` is tagged and the client pin moves (client PR #155).
7. **Respawn requires the enemy phase.** With `GAMESERVER_ENEMIES` off a killed player stays dead
   (section 5).
8. **Anti-cheat only records** ([#457](https://github.com/Cuvara/rpg-mmo-server/issues/457)).
9. **Test-lab tooling caveats** (review of rpg-factory PR #17 at `lab`):
   - `analyze --delivery` derives a relative one-way delay that is invalid under the WSL clock drift
     (section 3). Read its gap classes, not its delay trend.
   - `netcode-headless.sh` can report success after `dotnet test` fails or times out. Check the
     exit code and the TRX counts yourself.
   - S07/S07P report memory growth as INFO, so a memory regression does not fail those runs.
   - The provenance manifest does not yet hash `disruptions.ndjson`.
10. **A client that quits does not close its KCP session**
    ([Netcode#182](https://github.com/Cuvara/Netcode/issues/182)); the server sees the session time
    out instead.
