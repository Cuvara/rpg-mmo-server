# Changelog

All notable changes to the loadtest module are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Added

- **`-sealed`: the load generator can speak a sealed session**, so the gameplay hop can be
  exercised encrypted end to end rather than only in unit tests.
  - Configured on **both** ends and never negotiated on the wire — this is the counterpart
    to the server's `GAMESERVER_SEALED`. A wire-negotiated setting would be a downgrade
    attack: an attacker strips the offer and both ends conclude the other could do no
    better.
  - Only the **game-server** socket is sealed. The gateway hop has its own trust model and
    sealing it with these keys would be meaningless, since they derive from a join token
    the gateway itself issues.
  - The harness mints its own join tokens, so unlike a shipped client it **verifies the
    server's binding** — reported as `sealed_binding_verified` alongside `sealed_players`,
    both always present rather than `omitempty`, because "the field is absent" is the wrong
    answer to "was this session encrypted" and the gap between the two numbers is the
    difference between confidentiality and authenticity.

  Measured, 4 players, 40 AOI entities, same image, `-movement still`:

  | | server `off`, client plain | server `require`, client `-sealed` |
  |---|---|---|
  | readable player/entity ids in the capture | **1412** | **4** |
  | sealed frames (`0xC1`) | 0 | 870 |
  | downlink | 15 590 B/s/player | 15 979 B/s/player (**+2.5%**) |
  | uplink | 157 B/s/player | 548 B/s/player |

  The surviving 4 readable ids are the pre-handshake join responses, which are cleartext by
  design. The uplink ratio looks alarming and is not: input frames are tiny, so the 26-byte
  per-frame overhead dominates a figure whose absolute value is half a kilobyte a second.

### Added
- **`-run-id` fixes the run identifier user ids are derived from.** Default stays a random
  id per run so concurrent runs cannot collide; setting it explicitly is what makes a
  RECONNECT measurable — run, stop, run again with the same value, and the same accounts
  come back to a server still holding their entities. That is the only way to exercise the
  path where a client restarts its own input-tick counter against server state that
  remembers the old one, and it is how `BENCHMARK.md` Part XII measured it.

### Added
- **`-abuse` and `-abuse-players`: deliberately misbehaving clients.** The harness is
  otherwise scrupulously well-behaved — it answers pings, sends normalised vectors and
  disconnects politely — which is correct for a benchmark and useless for exercising the
  server's new input-rejection telemetry. `direction` sends an oversized movement vector
  (`invalid_direction`), `stale` replays one input tick for ever (`stale_tick`), `attack`
  targets an id no entity has (`attack_target_unresolved`). Abusive players are selected by
  index, the same mechanism `-movement spread` uses, so "2 of 50 players cheat" is
  expressible and reproducible.
- The oversized vector is large but **finite**. `encoding/json` cannot represent NaN or
  Inf, so a non-finite vector would fail to encode client-side on the legacy json arm and
  never reach the server at all; the server refuses both identically.
- Nothing here attempts to gain an advantage — the server refuses all of it. The point is
  to produce the signal so the counters can be tested rather than waited for.

### Fixed

- **`-movement cluster` walks players out of any crowd that does not march with them,
  and it is the default — documented, measured and pinned.** Its comment claimed players
  "stay mutually in-AOI, so this is the worst-case dense-crowd shape". True of the players;
  false of everything else. Players leave the origin along +X at 5 u/s against a 50-unit
  AOI radius, so they clear an origin-centred population in ~10s and are ~300 units away by
  the end of a default 60s window. Against server-side entities — `LOADTEST_ENTITIES`,
  which orbit the origin, or a stock map's enemy spawner — the visible set **collapses
  during the run** while the report still names the population it started with.
  - Measured (1 player, 300 `LOADTEST_ENTITIES`, server-side snapshot bytes/s every 4s):
    `still` held **117.6 kB/s flat**; `cluster` decayed **113 → 80 → 50 → 17 → 0.6 kB/s by
    t=24s**. Any default-configuration run longer than ~25s against a stationary population
    is therefore measuring a nearly empty AOI.
  - Found while acceptance-testing the game server's downlink budget, where it silently
    made a 60s run report a fifth of the bandwidth the population implied.
  - **Behaviour is unchanged** — `cluster` is still correct for the player-vs-player density
    it was built for, and changing its trajectory would change what every existing
    BENCHMARK.md figure taken under it means. What changed is that the trap is written down
    at the constant, in the README, and pinned by `TestClusterLeavesAStationaryCrowd`, which
    fails if the speed, the AOI radius, the default window or the default mode move far
    enough to invalidate the warning.

## [Unreleased]
### Documentation

- **Audited every published figure in `backend/docs/BENCHMARK.md` against the
  marching-crowd trap, and annotated the one live instance. No published number is
  affected and none was changed.**

  The trap: `cluster`, loadtest's **default** movement mode, drives every player +X for
  ever at 5 u/s against a 50-unit AOI radius. Its comment calls this "the worst-case
  dense-crowd shape", which is true of the players and false of any population that does
  not march with them — so against stationary entities the run measures a nearly empty
  AOI while reporting the population it started with. `spread` marches too (all headings
  sit in the +X/+Y quadrant); only `still` holds position, so the trap is **not** specific
  to `cluster`.

  **Method:** rather than trusting the prose, every result file under
  `loadtest/results/` was read for `movement`, `players`, `entities` and
  `players_online`. A stationary population shows up as entities exceeding players.
  Every published run reads **entities == players == online** — Parts I, II, III, IV
  and IX, plus the six `tick-variance` runs. Two independent things kept it that way:
  the capacity runs set `GAMESERVER_ENEMIES=false`, and BENCHMARK.md §2's protocol
  restarts the container and waits for `gameserver_entities` to read 0 before every
  level.

  **What is affected is the recipe, not the results.** BENCHMARK.md §10's stock-dev-stack
  command ran the default mode against the 6 spawned enemies and passed
  `-baseline-entities 6` to stop the level being rejected as "not empty when the level
  started" — so it is the one documented configuration that converts a contaminated run
  into a **passing** one rather than an INVALID one. The validity gate was, by accident,
  the guard; declaring the baseline defeats it. The recipe now passes `-movement still`
  and carries a warning explaining why.

  Three contaminated runs already exist in the tree
  (`results/2026-09-07-develop-c05f715/run-{10,50,100}-cluster.json`: 14/16/16 entities
  against 10 players online). They are the discarded through-the-gateway attempts
  BENCHMARK.md §26 already records as producing no valid level, and no figure derives
  from them.

  **Direction, for any figure that ever is affected:** a contaminated run measures a
  shrinking AOI, so per-client bandwidth and per-tick gather cost read **too low**, and
  the error grows through the window; a ratio between two arms measured the same way is
  largely preserved while both absolute numbers are wrong.

  Annotations added: an audit note in §8 (the "read this before quoting any number"
  section), the §10 recipe warning, a note on Part VI recording that its movement mode
  was never written down (default `cluster`, cleared anyway because the spawner was off
  and the entity leak had been fixed eight days earlier), and a note on Part X that it
  and Part XI are in-process xUnit benches which never invoke loadtest — so the trap is
  **not** an alternative explanation for Part X's ratios failing to reproduce.

### Changed
- **`-encoding` now defaults to `proto`, the wire the Unity client speaks (ADR-9); `json` stays
  reachable as the legacy A/B arm.** The 2026-09-07 sweep against `develop@c05f715` ran under the
  old `json` default and reported 274 KB/s per client at 200 players — the JSON arm's figure,
  alongside 45.9 KB/s Protobuf in `BENCHMARK.md`, so for an hour it read as a 6x bandwidth
  regression. It was the tool measuring the wrong wire. The summary header now carries
  `encoding=<proto|json|mixed>` so a table says which arm produced it. `scripts/encoding-sweep.sh`
  passes `-encoding` explicitly and is unaffected.

### Added
- **`-baseline-entities N`**, tolerated by the not-empty-at-start validity check and recorded as
  `config.baseline_entities`. The stock map server spawns 6 enemies, so against the dev stack every
  level tripped "server reported 14 entities for a 10-player level" and the sweep produced nothing.
  Players get no allowance — an extra player online is still a dirty server — and the strict
  message now names the flag, because the symptom does not. Tests cover both edges and the
  player exclusion.

### Documentation
- `BENCHMARK.md` §10 recipe brought up to date with what the server requires now: a dedicated
  `JOIN_TOKEN_SECRET` (the server refuses to start without one), `GAMESERVER_ENEMIES=false` on the
  bench container, and the gateway's 10 conn/min/IP admission rate as the reason the stock-stack
  example stops at 10 players and the sweep joins direct.

### Documentation
- **Closed the last gap in the #153 clock audit: `BENCHMARK.md` claimed to cover "every figure
  in this document" and its table covered only the loadtest-derived ones.** Three figure
  classes were unaudited: `host.load_avg_1/_5/_15` (read verbatim from `/proc/loadavg` in
  `load/host.go`, no interval taken — clean), §23's allocation B/tick
  (`GC.GetAllocatedBytesForCurrentThread` over a fixed 60-tick loop, no clock read — clean),
  and Part V's brute-vs-indexed microsecond columns. The last one **cannot be traced**: the
  paired in-process A/B harness was never committed (`2e3e5db` carries `SpatialGrid`,
  `EcsWorld` and `AoiIndexDifferentialTests` and no benchmark), so the clock behind
  77-136 µs / 1380-1514 µs is unreadable from the repo. The numbers are **kept and marked**,
  not deleted — deleting them would destroy the record of a measurement that was taken, and a
  10-17% skew cannot touch a 2.8x result. They are downgraded to order-of-magnitude, Part V's
  conclusion is unchanged because it rests on within-run ratios where a shared skew cancels,
  and the replacement is named: re-run the A/B with a committed harness that states its clock,
  the way `SnapshotAllocationTests.cs` does. Refs #153.
- **Audited the generator's clock discipline against the host-clock hazard (#153), and it is
  clean — no code change needed.** `CLOCK_REALTIME` on this box runs 10-17% fast against
  `CLOCK_MONOTONIC`, which would corrupt any rate derived from it, so every interval the
  generator computes was traced to source. All of them are monotonic: the measurement window
  (`time.Since(windowStart)`), the server-side scrape window
  (`afterGS.At.Sub(beforeGS.At)`, both `At` set from `time.Now()` at scrape time), snapshot
  interval (`now.Sub(lastSnap)`), ack latency and join latency. Go embeds a monotonic reading
  in every `time.Now()` and `Time.Sub`/`time.Since` prefer it, and nothing in `load/` strips
  that reading — no `.UTC()`, `.Round()`, `.Truncate()` or string round-trip stands between a
  timestamp and its subtraction. Recorded here because the property is load-bearing and
  silent: introducing any of those calls on a `time.Time`, or reading a duration off a
  server-supplied wall-clock field, would degrade every rate the tool reports by 10-17% with
  no test failing. Refs #153.

### Fixed
- **Virtual players are no longer evicted for outliving the heartbeat (#142).**
  Both peers send `MsgPing` every 10s and close any connection that has not
  answered with `MsgPong` within 30s (`Connection.cs`, `gateway/server/connection.go`),
  and the generator answered on neither hop: `readLoop` skipped every non-snapshot
  frame, and the `-hold-gateway` socket had no reader at all. Every run longer
  than the 30s timeout therefore lost players mid-flight while the server itself
  was healthy — with the giveaway that a *slower* ramp failed *more*, because it
  kept the run alive longer (80 players lost 30 at ramp 10/s and 65 at ramp 3/s).
  Players now answer on the game-server socket, on the held gateway socket, and
  during the handshake round-trips, echoing the probe's timestamp unchanged and
  replying in the connection's own encoding.

### Added
- `heartbeats_total` and `gateway_heartbeats_total` in the JSON result. Heartbeat
  frames are deliberately excluded from `snapshots_total`, the snapshot-interval
  distribution, `recv%` and both byte counters — a `MsgPing`/`MsgPong` is harness
  upkeep, not gameplay, and folding it in would bias the very throughput figures
  this harness exists to make trustworthy. Zero on a run longer than 10s is the
  signal that the heartbeat is unanswered again.
- The generator now reconstructs authoritative state with `SnapshotState`, so it
  resolves interned entity handles the way a real client must. Without this it
  would consume handle-only entities with empty ids and report a smaller
  bandwidth figure for a stream it never actually reconstructed.
- `resyncs` in the JSON result: keyframes requested because a snapshot referenced
  an unknown handle. Non-zero means the two ends disagreed about interning state.
- `results/entity-id-interning/` — the four-level sweep, ~51% saved at every
  level with zero resyncs.

### Added
- `results/entity-type-enum/` — the four-level sweep measuring the entity-type
  enum at ~15.4% of downstream bandwidth. All four levels passed the validity
  gate on the first attempt.

### Added

- **`-repeat N` and a ceiling that is a decidable output rather than a one-shot
  threshold crossing.** `ComputeCeiling` groups repeated runs of a level and
  decides it on the **median** tick p99, reporting the min..max bracket alongside
  and naming any level whose runs straddle the budget.

  Unanimity was rejected with arithmetic, not taste: the disturbance on this host
  is bimodal (a CD deploy sharing the machine), and with 2 runs in 6 disturbed the
  chance that all N are clean is (4/6)^N — 0.44 at N=2, **0.30 at N=3**, 0.20 at
  N=4. A unanimity rule marks a genuinely-passing level marginal ~70% of the time
  at N=3 and gets worse as N grows. A median cannot be moved by a minority.

  Levels are repeated in the **outer** loop, so run k of every level happens
  before run k+1 of any: repeating a level back to back would correlate its runs
  with whatever the host was doing for that one minute, which is the variance the
  repeat exists to measure.

- **`HostStats`** — load average and core count recorded per run, sampled after
  the measurement window. Evidence for a human comparing two sweeps, explicitly
  **never** an input to the verdict.

  A tempting rule was tried and rejected: "achieved tick rate below the configured
  rate means the process was starved, so the run is INVALID". A genuinely
  saturated server loses ticks the same way — measured 10.46 ticks/s at 300
  players and 12.51 at 400, both real capacity limits, against 12.87–13.48 for a
  quiet box disturbed by a deploy. That rule would have classified real ceilings
  as environment faults and removed them from the results, an error in the
  optimistic direction. The tool cannot tell the two apart from its own metrics
  and no longer pretends it can.

### Changed

- `scripts/encoding-sweep.sh` refuses to start while a `cd.yml` run is in progress
  or queued. The load generator and the self-hosted deploy runner share a host, so
  an overlapping deploy contaminates the sweep *and* the sweep can make the
  deploy's smoke test flaky — which under the merge gate reads as a broken deploy
  rather than a busy box. `SKIP_CD_CHECK=1` overrides, and says to record the
  overlap. Only `cd.yml` is checked, deliberately: its deploy jobs are the only
  self-hosted ones, while ci.yml/_go-module.yml/ci-dotnet.yml are all
  `ubuntu-latest`.

### Added

- **`INVALID` verdicts: a level that did not measure what it claims is now
  excluded from aggregates rather than reported as a worse result.** `Verdict`
  gained a machine-readable `Invalid` field (previously INVALID existed only as a
  string prefix on `Reason`, for mid-run server restarts). A level is INVALID when
  any client failed, when the snapshots-received ratio is more than 5% off 1.0, or
  when the server reports more entities or players online than were requested.

  This is a property of the tool, not of a benchmark script, because a broken run
  does not announce itself in the headline numbers and can look *better* than a
  healthy one: a sweep level whose clients all died mid-run reported a **97%
  bandwidth saving**, since bytes-not-received are indistinguishable from
  bytes-not-sent. It was caught only because the number was implausibly good.
  See `docs/BENCHMARK.md` §16.

  Note the received-ratio check looks **upward** (> 1.05). The pre-existing
  `NoFrameLoss` check looks downward (< 0.95) and answers a different question —
  did the server's bounded send channel drop frames — which is why a ratio of
  1.40 previously passed unremarked.

### Changed

- `scripts/encoding-sweep.sh` waits for `gameserver_players_online == 0` as well
  as `gameserver_entities == 0` before each level. Checking entities alone let a
  container that was still carrying players through as "clean".

### Added

- **`-encoding json|proto`** — selects the wire encoding virtual players speak,
  and the choice is recorded in the JSON result so a result file is
  self-describing. The server answers in whatever encoding it is addressed in, so
  one *unchanged* server binary can be measured under both: the before/after is a
  controlled comparison instead of one spanning two builds.
- `scripts/encoding-sweep.sh` — the three-arm capacity sweep at matched player
  counts: `develop`'s image on JSON (the `BENCHMARK.md` baseline), this branch's
  image on JSON (isolates the JSON-path cleanup), and this branch's image on
  protobuf (isolates the encoding change). One loadtest binary drives all three,
  so only the server under test varies. The middle arm exists for honesty —
  folding the removed `JsonDocument.Parse` round-trip into "what Protobuf bought"
  would flatter the result.
- `results/encoding/` — the raw JSON from that sweep.

### Fixed

- **The snapshot read loop hardcoded `json.Unmarshal`**, so under `-encoding
  proto` every snapshot failed to parse and was skipped by the `continue`. It
  surfaced as `recv%=0` with zeroed snapshot/ack percentiles rather than as an
  error. `decodeCounted` now goes through `messages.DecodeBody` and sniffs the
  encoding like everything else.

### Added

- **New module `backend/loadtest`** — a load generator that drives N concurrent
  virtual players through the real wire protocol (gateway `MsgAuth` /
  `MsgEnterWorld` → game server `MsgJoinToken` → `MsgInput` at tick rate →
  delta-merged `MsgSnapshot`), using the same `shared/messages` codec as
  `smoketest` and the Unity client. Closes the tooling half of ADR-7.
- Client-observed measurements: snapshot-interval and input→ack latency
  percentiles (exact, over raw samples), join latency, per-client bytes/sec in
  both directions, and connection failures bucketed by lifecycle phase.
- Server-side measurements scraped from `/metrics` and **differenced across the
  measurement window**: `gameserver_tick_duration_seconds` p50/p95/p99 plus the
  exact fraction of ticks over budget, achieved tick rate, `players_online`,
  `entities`, `snapshots_sent_total`, and the gateway's `connections_active`,
  auth/enter-world ok+fail and rate-limit counters.
- Cross-check between the two sides: snapshots received ÷ snapshots the server
  enqueued, which detects the silent frame loss the game server's bounded
  64-deep `DropOldest` send channel would otherwise hide.
- Acceptance verdict per run against ADR-7's thresholds (tick p99 vs the 66.67ms
  15Hz budget, snapshot cadence vs 2× the tick period, zero connection errors,
  no frame loss), with `-fail-on-degraded` for CI.
- Mid-run server-restart detection via counter reset, reported as `INVALID` and
  outranking every other verdict — on a shared box a concurrent redeploy is
  otherwise indistinguishable from a load-induced failure.
- `-sweep 1,10,50,100` to run several player counts in one invocation, with
  `-cooldown` between levels, a comparison table and a single JSON document
  (schema `rpg-mmo.loadtest/v1`).
- `-auth presigned|nakama`. Pre-signed is the default so a run measures the game
  path rather than Nakama's login throughput; `-auth=nakama` drives the real
  device-auth + `gateway_token` RPC path when login throughput is the question.
- `-join gateway|direct`. `direct` mints the same `sid`-bound join token the
  gateway would and dials the game server directly, which is both correct per
  ADR-3 (the gateway is not in the gameplay data path) and necessary above ~10
  players, since `GATEWAY_CONN_RATE_PER_MIN` defaults to 10/min per source IP.
- `-movement cluster|still|spread` as the bottleneck experiment control: `still`
  keeps the AOI scan and delta diff at full cost while collapsing the
  serialization term, so the difference between modes at equal player count
  measures serialization directly.
- `scripts/bench.sh` — runs one level while sampling `docker stats` for the
  server containers and the load generator's own CPU/RSS.
- `results/` — the raw JSON from the 2026-08-07 benchmark run.

### Documented

- `README.md` — usage, what each flag measures, and why the non-obvious defaults
  (pre-signed auth, direct join, movement modes) are what they are.
- `backend/docs/BENCHMARK.md` (new) — methodology, machine, measured tables,
  break point and bottleneck analysis, plus an explicit confounds section.

### Findings

Recorded here because they are properties of other modules, discovered by this
one. Full detail in `backend/docs/BENCHMARK.md`.

- **One game server holds ~150 concurrent players** in the worst-case dense-crowd
  shape before `gameserver_tick_duration_seconds` p99 crosses the 66.67ms budget
  (160 breaches at 67.6ms; 150 sits at 49.8ms). Measured on a WSL2 dev
  workstation — a lower bound, not a production figure.
- **The bottleneck is snapshot construction + JSON serialization (~80% of tick
  cost), not the brute-force AOI scan (~20%)**, contradicting ADR-7's prediction.
  Confirmed two independent ways: cluster-vs-still at equal player count (4-6×),
  and the p50/p99 gap inside a single still-mode run (keyframe ticks vs delta
  ticks).
- **Downstream bandwidth is 1.22 KB/s per in-AOI player** and near-perfectly
  linear, so ADR-7's own < 50 KB/s per client threshold breaks at ~41 players —
  well before the tick budget does.
- **Entities leak on disconnect** in `gameserver-dotnet`: `players_online`
  returns to 0 but `gameserver_entities` stays at its peak indefinitely, turning
  a bounded O(n²) tick cost into an unbounded one on a long-lived server.
- **Keyframe stampede**: per-connection keyframe counters are not staggered, so a
  cohort that joins together triggers full-state serialization for every client
  on the same tick every 30 snapshots.
- **The tick loop runs at ~14.7Hz, not 15Hz**, even at zero load — timer
  granularity, so the effective budget is ~68ms.
- **Client-side snapshot cadence cannot detect a blown tick budget.** At 200
  players the tick was 51ms mean against a 66.67ms budget while clients still saw
  an 87ms p99 snapshot interval, because the loop runs ticks late rather than
  skipping them. Only `gameserver_tick_duration_seconds` catches this.
