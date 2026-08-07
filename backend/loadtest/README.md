# loadtest — realtime-path load generator

N concurrent virtual players driving the real wire protocol, with client-observed
latency/throughput and server-side Prometheus counters reconciled in one report.

`smoketest` answers *"does one player work"*. `loadtest` answers *"how many
players before it stops working"*.

Built to close [ADR-7](../docs/ARCHITECTURE-DECISIONS.md#adr-7--ccu-and-cost-figures-are-unbenchmarked-estimates).
Measured results: **[../docs/BENCHMARK.md](../docs/BENCHMARK.md)**.

## Quick start

```bash
go build -o loadtest ./cmd/loadtest

# 10 players for 60s through the full gateway path
JWT_SECRET=dev-secret-change-me ./loadtest -players 10 -duration 60s

# Sweep until something degrades, machine-readable output
JWT_SECRET=dev-secret-change-me ./loadtest -sweep 1,10,50,100 -json sweep.json
```

## What each player does

```
[presign JWT | Nakama device auth]        -auth
   -> MsgAuth / MsgEnterWorld (gateway)   -join=gateway   (skipped by -join=direct)
   -> MsgJoinToken (game server)
   -> MsgInput every tick  +  consume MsgSnapshot, merging deltas
   -> MsgDisconnect
```

## What it measures

| Reported | Source |
|---|---|
| Snapshot interval p50/p95/p99 | client-side, gap between `MsgSnapshot` arrivals |
| Input→ack latency p50/p95/p99 | client-side, `MsgInput` send → first snapshot whose `ack_tick` covers it |
| Join latency | client-side |
| Bytes/sec per client, both directions | client-side, full wire frames incl. length prefix |
| Connection failures by phase | client-side (`auth`, `gateway`, `join`, `run`) |
| Tick duration p50/p95/p99 + exact over-budget fraction | `gameserver_tick_duration_seconds`, bucket-differenced |
| Achieved tick rate | `gameserver_tick_duration_seconds_count` / window |
| players_online, entities, snapshots_sent | game server `/metrics` |
| gateway_connections_active, auth/enter-world ok+fail, rate-limited | gateway `/metrics` |
| Snapshots received ÷ snapshots the server enqueued | cross-check for silent frame loss |

Output is a compact table plus JSON (`-json`, schema `rpg-mmo.loadtest/v1`) so
runs are directly comparable.

### Verdict

A level is **DEGRADED** if any of these fails:

- tick p99 > 66.67ms (the 15Hz budget — ADR-7's acceptance threshold), or more
  than 1% of ticks above the 50ms histogram edge;
- client snapshot interval p99 > 2× the tick period;
- any player failed to join or dropped mid-run;
- clients received < 95% of the snapshots the server says it enqueued
  (`Connection.cs` uses a 64-deep bounded channel with `DropOldest`, so a client
  the writer cannot keep up with loses frames silently).

A mid-run server restart is detected via counter reset and reported as
`INVALID`, outranking every other verdict — on a shared dev box a concurrent
redeploy otherwise looks exactly like a load-induced failure.

## Key flags

| Flag | Default | Why it matters |
|---|---|---|
| `-players`, `-ramp`, `-duration`, `-warmup` | 10, 20/s, 60s, 5s | Load shape. Warmup is discarded so join keyframe storms are not counted as steady state. |
| `-sweep 1,10,50,100` | — | Run several levels in one go. `-cooldown` between them. |
| `-auth presigned\|nakama` | `presigned` | **Pre-signed is the default on purpose.** See below. |
| `-join gateway\|direct` | `gateway` | `direct` skips the gateway. See below. |
| `-movement cluster\|still\|spread` | `cluster` | The bottleneck experiment control. See below. |
| `-tick-rate` | 15 | Client input rate. Matches the server; sending faster gains nothing (the tick loop coalesces to the newest input per player per tick). |
| `-json`, `-label` | — | Machine-readable output. |
| `-fail-on-degraded` | off | Exit 1 on a degraded level, for CI. |

### Why pre-signed JWTs are the default

The mission is to benchmark the **game path**. Driving real Nakama logins folds
Nakama's HTTP stack, its Postgres round-trips and account creation into a number
that is supposed to describe the game server's tick loop — a 200-player ramp
would measure Nakama's login throughput instead. A pre-signed token is the exact
token Nakama's `gateway_token` RPC would mint (same HS256 secret, same claims),
so the gateway's verification path is unchanged.

Use `-auth=nakama` when login throughput *is* the question.

### Why `-join=direct` exists

Per [ADR-3](../docs/ARCHITECTURE-DECISIONS.md#adr-3--gateway-is-a-redirector-not-a-router)
the gateway is a redirector deliberately *not* in the gameplay data path, so a
game-server capacity number should not include it. `-join=direct` mints the same
`sid`-bound join token the gateway would and dials the game server directly; the
server-side path from join onward is byte-for-byte production.

It is also **required** above ~10 players, because two config ceilings stop the
gateway path long before any performance limit:

- `GATEWAY_CONN_RATE_PER_MIN` = 10/min per source IP — every virtual player
  shares one IP, so player 11 is rate-limited;
- the registry advertises `capacity=100` and the gateway refuses allocation past
  it (the game server independently refuses at `GAMESERVER_CAPACITY`).

### Why `-movement` is an experiment, not a knob

The tick does three O(n) things per client — AOI distance scan, delta diff, and
snapshot build + JSON — so the tick is O(n²) three times over. Only the third
depends on whether entities moved:

- `still` — zero-vector input. Ack still advances, positions do not, so deltas
  come out empty and the serialization term collapses. Scan and diff stay at full
  cost.
- `cluster` — everyone moves every tick and everyone is in everyone's AOI. All
  three terms at full cost.

The difference at equal player count is a direct measurement of serialization
cost, with no server-side change. This is how BENCHMARK.md attributes ~80% of
tick time to serialization and ~20% to the AOI scan.

`spread` gives each player a distinct heading. Note it is **not** a low-density
control: at 5 u/s with a 50-unit AOI radius, a 60s run cannot separate more than
~9 players out of AOI. Use `still` as the cheap-serialization control.

## Benchmarking protocol

`scripts/bench.sh` runs one level while sampling `docker stats` and the
generator's own CPU (the confound — on a dev box the generator can cost more CPU
than the server under test).

**Restart the game server between levels and wait for `gameserver_entities` to
read 0.** Entities currently leak on disconnect, so without this each level
inherits the previous one's entities and measures the wrong entity count. See
BENCHMARK.md §7.

```bash
docker restart rpg-gameserver
until [ "$(curl -s localhost:9101/metrics | awk '/^gameserver_entities/{print $2}')" = "0" ]; do sleep 2; done
```

## Tests

```bash
go test ./...            # CI adds -race
```

Percentiles, Prometheus parsing, histogram-quantile estimation, counter
differencing, verdict logic, sweep-flag parsing and frame decoding are all
covered. The network path is exercised by running it — there is no mock server.
