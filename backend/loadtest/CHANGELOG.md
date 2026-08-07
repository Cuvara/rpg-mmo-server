# Changelog

All notable changes to the loadtest module are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

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
