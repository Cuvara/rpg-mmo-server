# Baseline sweep — `develop@3378bc9`, 2026-09-18

The control run taken **before** any importance-driven replication work starts, so
that later sweeps have something to be compared against. Nothing about the server
was changed to produce it.

## What was run

```
loadtest -sweep 50,100,150,200 -repeat 3 -cooldown 40s \
         -join direct -encoding proto -transport tcp \
         -movement {cluster,spread} -baseline-entities 0 \
         -ramp 20 -warmup 8s -duration 35s
```

- **Dedicated bench server**, started fresh for each movement mode and confirmed
  at `entities == 0` before the sweep began: `GAMESERVER_ENEMIES=false`,
  `GAMESERVER_CAPACITY=2000`, no registry, TCP, sealed off, `SIM 60/15/5`.
  Release build, run directly rather than through a container.
- **`-join direct`**: the gateway is deliberately not in the gameplay data path
  (ADR-3), so a game-server capacity number must not include it.
- **`-cooldown 40s`** rather than a container restart between levels. It has to
  exceed the 30 s entity hold, or the next level starts against a world still
  holding the previous level's disconnected players and the validity gate trips.

## Reading these files

`sweep-cluster.json` and `sweep-spread.json` each hold 12 runs (4 levels x 3
repeats). Each run records, for the first time, the rates it was judged against:

```
tick_budget_sec      1/SIM_CRITICAL_HZ   the budget for ONE BASE TICK
snapshot_period_sec  1/SIM_WORLD_HZ      the expected gap between snapshots
sim_critical_hz / sim_world_hz / rates_source
```

Earlier result files in this directory have none of these and were judged against
a single 66.67 ms constant that was right only for the pre-ADR-13 single-rate
server. **Their tick verdicts are not comparable with these**: at 60/15 that
constant is four times the real base-tick budget. Their bandwidth figures are
comparable, because bytes do not depend on either rate.

## Health warning, and which column to quote

The load generator shares this workstation with the server under test (ADR-7), so
**every tick figure here is a lower bound of unknown tightness.** Bandwidth is
not: it reproduces to 0.3 % across repeats on this host and does not care what
else the machine is doing. Size anything on the bandwidth column.

See `backend/docs/BENCHMARK.md` Part XIII for the analysis.
