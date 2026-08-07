# Benchmark — measured game-server capacity

> **Headline: one game server holds ~150 concurrent players in the worst-case
> dense-crowd shape before the 15Hz tick budget breaks. The bottleneck is
> per-client snapshot construction and JSON serialization, not the brute-force
> AOI scan.** At 160 players tick p99 crosses 66.67ms; at 150 it sits at 49.8ms.
>
> ⚠️ **These numbers were measured on a WSL2 developer workstation that was
> simultaneously running the load generator, Docker Desktop, Kubernetes, Agones
> and an AI agent. They are a LOWER BOUND, not a production capacity figure.**
> See [Confounds](#confounds-read-this-before-quoting-any-number).

Measured 2026-08-07 against `develop` @ `a19bfed`, game server image
`rpg-mmo/gameserver-dotnet:a19bfed`. Replaces the unbenchmarked estimates called
out in [ADR-7](ARCHITECTURE-DECISIONS.md#adr-7--ccu-and-cost-figures-are-unbenchmarked-estimates).

Raw results: [`backend/loadtest/results/`](../loadtest/results/) (one JSON per
level, schema `rpg-mmo.loadtest/v1`). Tool: [`backend/loadtest`](../loadtest/).

---

## 1. The number

| Question | Answer |
|---|---|
| Players per game server before tick p99 > 66.67ms | **150** (160 breaches) |
| What breaks first | `gameserver_tick_duration_seconds` p99 |
| Why | Per-client snapshot build + `JsonSerializer` inside the tick loop |
| AOI scan share of tick cost | ~20% |
| Snapshot build + JSON share | ~80% |
| Downstream bandwidth at 150 players | **184 KB/s per client** (~1.5 Mbps) |
| Bandwidth ceiling (ADR-7's own < 50 KB/s target) | **~41 players** |

The bandwidth figure fails ADR-7's own acceptance threshold (< 50 KB/s per
client) at **~41 players**, long before the tick budget does. On the stated
mobile-network assumption, bandwidth — not tick time — is the real ceiling.

---

## 2. Methodology

### What was driven

`backend/loadtest` runs N virtual players concurrently. Each one walks the real
wire protocol using the same `shared/messages` codec the Unity client will use:
join → `MsgInput` at 15Hz → consume and delta-merge `MsgSnapshot`.

- **Auth: pre-signed JWTs** (`-auth=presigned`, the default). Tokens are minted
  locally with the shared HS256 secret, exactly as Nakama's `gateway_token` RPC
  would. This is deliberate: driving real Nakama logins would fold Nakama's HTTP
  stack, its Postgres round-trips and account creation into a number that is
  supposed to describe the game server's tick loop. A 200-player ramp would have
  measured Nakama's login throughput. Use `-auth=nakama` when login throughput is
  the question.
- **Join: direct** (`-join=direct`). The join token is minted locally with the
  same `sid`-bound HS256 scheme the gateway uses, and the client dials the game
  server directly. Per [ADR-3](ARCHITECTURE-DECISIONS.md#adr-3--gateway-is-a-redirector-not-a-router)
  the gateway is a redirector that is deliberately *not* in the gameplay data
  path, so a game-server capacity number should not include it. This was also
  **forced** by two deployment ceilings — see [§6](#6-ceilings-that-are-config-not-capacity).
- **Movement: two modes**, which are the experiment control, not cosmetics:
  - `cluster` — every player moves every tick, and all spawn at the origin so
    every entity is inside every other entity's 50-unit AOI. Worst-case density.
  - `still` — zero-vector input. `LastInputTick` still advances (so `ack_tick`
    still works), but no position changes, so delta snapshots come out empty.

### Why those two modes isolate the bottleneck

The tick loop (`GameServer/Server/TickLoop.cs`) does three O(n) things per
connected client, making the whole tick O(n²) three times over:

1. **AOI scan** — `AoiLogic.GetNearbyEntities` compares squared distance against
   every entity in the world. Runs regardless of whether anything moved.
2. **Delta diff** — `SnapshotDeltaState.EncodeDelta` walks the nearby list and
   hash-compares each entity against the last view sent. Also runs regardless.
3. **Snapshot build + JSON** — `ToMsg` allocations plus
   `JsonSerializer.SerializeToUtf8Bytes`, **inside the tick**
   (`WireProtocol.NewEnvelope`). Only pays for entities that actually changed.

`still` mode keeps terms 1 and 2 at full cost and collapses term 3. The
difference between the two modes at the same player count is therefore a direct
measurement of what serialization costs. No server-side change was needed.

### Measurement mechanics

- Client-observed **snapshot interval** = wall-clock gap between consecutive
  `MsgSnapshot` arrivals, per player, pooled across players.
- Client-observed **ack latency** = time from an `MsgInput` leaving the socket to
  the first snapshot whose `ack_tick` covers that input's tick. Each input
  contributes exactly one sample.
- **Server counters** are scraped from `/metrics` before and after the window and
  **differenced**. The tick histogram is differenced bucket-by-bucket, so the
  reported p99 describes the measured window only — reading the histogram once
  would average in every idle tick since process start and hide the peak.
- Clients start recording *before* the first scrape and stop *after* the last, so
  the server-side window sits strictly inside the client-side one. A
  `snapshots_received_ratio` below 1 is therefore real frame loss, not scrape
  skew.
- Percentiles over client samples are **exact** (sorted raw samples, nearest
  rank), not histogram estimates.

### Run protocol

- 35-second measurement window per level, after an 8-second warmup discarded so
  join keyframe storms are not counted as steady state.
- Ramp 100 players/sec.
- **The game server container is restarted before every level and the run waits
  until `gameserver_entities` reads 0.** This is not hygiene theatre — see the
  entity leak in [§7](#7-bugs-found-while-benchmarking). Early sweeps that
  skipped this were contaminated: a level nominally at 100 players was scanning
  350 entities, and its numbers were discarded.
- Break-point levels were repeated; 140/150/160 reproduced within ±0.3ms of tick
  p99 across runs.

---

## 3. Results — `cluster` (worst-case density)

All players mutually in AOI, all moving every tick. Tick budget 66.67ms @ 15Hz.

| Players | tick p50 | **tick p99** | tick mean | ticks over budget | ticks/s | snap p50 | snap p99 | ack p99 | KB/s/client | MB/s total | peak RSS | verdict |
|--------:|---------:|-------------:|----------:|------------------:|--------:|---------:|---------:|--------:|------------:|-----------:|---------:|:--------|
| 50 | 4.18ms | 19.11ms | 4.62ms | 0% | 14.57 | 68.3ms | 76.4ms | 73.5ms | 61 | 3.0 | 31 MiB | ok |
| 100 | 17.56ms | 28.62ms | 14.50ms | 0% | 14.66 | 68.1ms | 79.7ms | 81.9ms | 122 | 11.9 | 50 MiB | ok |
| 120 | 18.29ms | 47.38ms | 20.20ms | 0% | 14.69 | 67.8ms | 82.3ms | 87.4ms | 146 | 17.1 | 48 MiB | ok |
| 140 | 26.59ms | 49.53ms | 25.99ms | 0% | 14.77 | 67.7ms | 80.5ms | 90.9ms | 172 | 23.5 | 54 MiB | ok |
| **150** | 35.58ms | **49.77ms** | 29.15ms | 0.19% | 14.83 | 67.4ms | 80.6ms | 93.7ms | 184 | 27.0 | 43 MiB | **ok — highest passing** |
| **160** | 37.78ms | **67.61ms** | 35.00ms | 2.92% | 14.68 | 67.7ms | 83.0ms | 105.1ms | 195 | 30.4 | 58 MiB | **DEGRADED** |
| 200 | 50.96ms | 78.67ms | 51.59ms | 51.95% | 14.63 | 68.1ms | 87.1ms | 118.5ms | 242 | 47.3 | 82 MiB | DEGRADED |

"ticks over budget" is exact, not interpolated: it is the fraction of ticks above
the 50ms histogram edge (the largest edge at or below the budget).

## 4. Results — `still` (control: AOI scan + delta diff, no serialization)

| Players | tick p50 | tick p99 | tick mean | ticks over budget | KB/s/client | peak RSS | verdict |
|--------:|---------:|---------:|----------:|------------------:|------------:|---------:|:--------|
| 50 | 0.51ms | 4.50ms | 0.79ms | 0% | 2.7 | 25 MiB | ok |
| 100 | 3.18ms | 14.01ms | 3.42ms | 0% | 4.5 | 30 MiB | ok |
| 150 | 7.16ms | 23.85ms | 6.90ms | 0% | 6.2 | 43 MiB | ok |
| 200 | 8.67ms | 24.62ms | 9.80ms | 0% | 8.0 | 58 MiB | ok |
| 250 | 17.27ms | 45.65ms | 14.62ms | 0.39% | 9.9 | 78 MiB | ok |

---

## 5. Where it breaks, and why

### The bottleneck is serialization, not the AOI scan

Comparing mean tick time at equal player counts, `cluster` ÷ `still`:

| Players | still mean | cluster mean | ratio |
|--------:|-----------:|-------------:|------:|
| 50 | 0.79ms | 4.62ms | **5.8×** |
| 100 | 3.42ms | 14.50ms | **4.2×** |
| 200 | 9.80ms | 51.59ms | **5.3×** |

Both modes pay the identical brute-force AOI scan and delta diff. The extra
4–6× in `cluster` is snapshot construction and JSON serialization alone.
**The AOI scan accounts for roughly 20% of tick cost; building and serializing
the payload accounts for roughly 80%.**

This contradicts the expectation recorded in ADR-7, which named brute-force AOI
as "the most likely first failure". It is a real cost and it is genuinely
O(n²) — but it is the smaller of the two O(n²) terms by a factor of five.

### Second, independent confirmation — from a single run

Within `still` mode at 200 players, p50 is 8.67ms but p99 is 24.62ms. Nothing is
moving, so delta ticks do almost no serialization — but every 30th snapshot is a
**keyframe** carrying the complete AOI set, which does. The p50/p99 gap inside
one run is the same serialization cost measured without any cross-run comparison.

It also exposes a **keyframe stampede**: every client's `_sinceKeyframe` counter
starts when it joins, so a cohort that joins together keyframes together. One
tick in 30 does the full-serialization work for *every* client at once. That is
why p99 is ~3× p50 while the mean stays low, and it is a production concern
independent of load — staggering the initial counter per connection would
flatten it.

### Bandwidth is the ceiling that bites first

Downstream per client grows linearly with in-AOI entity count, and total grows
quadratically:

- 50 players → 61 KB/s per client
- 150 players → 184 KB/s per client (~1.5 Mbps), 27 MB/s off one server
- 200 players → 242 KB/s per client (~1.9 Mbps), 47 MB/s off one server

ADR-7's own acceptance threshold is **< 50 KB/s per client** on the mobile-network
assumption. Interpolating the measured curve, that is breached at **~41 players**
— less than half the tick-budget ceiling. Any capacity plan should treat 65, not
150, as the mobile-viable dense-crowd number until the encoding changes.

This is the JSON encoding, exactly as the extension-seam table predicts. An
`EntitySnapshot` on the wire is ~95 bytes of JSON (`{"id":"lt-…","type":"player",
"x":…,"y":…,"hp":100,"max_hp":100}`) for what is 6 fields, ~30 bytes packed.
Protobuf/FlatBuffers is the fix and it attacks the tick-time bottleneck and the
bandwidth bottleneck at once.

### What did NOT break

- **RAM is bounded and small.** 25–82 MiB resident across every level, growing
  roughly linearly with players and flat within a run. No unbounded growth. The
  contested "30–45MB vs 50MB" claim resolves to: **~30 MiB idle, ~50 MiB at 100
  players, ~82 MiB at 200**, comfortably inside the 128Mi Agones pod limit.
- **No connection failures** at any level up to 200 (`fail=0` throughout).
- **No frame loss** — `snapshots_received_ratio` stayed at 1.00, so the bounded
  64-deep per-connection send channel (`DropOldest`) never discarded anything.
- **Client-observed snapshot cadence stayed healthy even while the tick budget
  broke.** At 200 players snapshot p99 was 87ms against a 133ms threshold. This
  is worth understanding: the tick loop serializes on-tick but *writes* off-tick,
  and it does not skip ticks — it runs them late. Clients therefore see a nearly
  regular cadence while the simulation itself drifts. **A client-side cadence
  check cannot detect this failure; only `gameserver_tick_duration_seconds` can.**

### The 15Hz that is really ~14.7Hz

At every level including a single idle player, achieved tick rate is 14.6–14.8/s,
never 15.0, and client snapshot interval p50 is ~68ms rather than 66.67ms. That is
a ~2% drift present at zero load, so it is timer granularity in the tick loop's
sleep, not a load effect. Harmless now, but it means the effective budget is
~68ms and any future "we tick at exactly 15Hz" assumption is wrong.

---

## 6. Ceilings that are config, not capacity

Two limits stop a load test long before the tick budget does. Neither is a
performance property and both are one env var away from moving.

| Ceiling | Value | Where | Effect |
|---|---|---|---|
| Gateway connections per source IP | **10 per minute** (burst 10) | `GATEWAY_CONN_RATE_PER_MIN`, `shared/config/config.go` | Player 11 from one IP is rate-limited. Makes the gateway path untestable at scale from a single host. |
| Game server capacity | **100** | `GAMESERVER_CAPACITY`, `GameServer.cs:372` | Player 101 is rejected with "Server is full" — cleanly, at join, with no degradation. |

The capacity default of 100 is **conservative but well-placed**: measurement puts
the tick-budget ceiling at 150, so the shipped default leaves 33% headroom.
Raising it to match the measured ceiling would remove that headroom on a box
quieter than the one measured here — leave it at 100 unless a production-hardware
benchmark says otherwise.

The benchmark used a dedicated unregistered game server with
`GAMESERVER_CAPACITY=2000` on separate ports, so the shared dev stack was never
disturbed.

---

## 7. Bugs found while benchmarking

1. **Entities leak on disconnect.** After every client disconnects,
   `gameserver_players_online` correctly returns to 0 but `gameserver_entities`
   stays at the peak value indefinitely (observed: 400 entities with 0 players,
   persisting for minutes — well past the 30s reconnect hold). Consequences:
   - The AOI scan keeps paying for ghost entities forever, so a long-lived server
     degrades with *cumulative* joins rather than *concurrent* players.
   - Any capacity measurement is silently contaminated. This is why every level
     here restarts the container first.

   This is the single most important follow-up: it converts a bounded O(n²) cost
   into an unbounded one on a server that is never restarted.

2. **Keyframe stampede.** Per-connection keyframe counters are not staggered, so a
   cohort that joins together triggers full-state serialization for every client
   on the same tick, every 30 snapshots. Visible as `still` p99 being ~3× p50.

Both are in `gameserver-dotnet`, which was out of scope for this change — they are
reported, not fixed.

---

## 8. Confounds — read this before quoting any number

**The machine:**

| | |
|---|---|
| CPU | 12th Gen Intel Core i5-12400F, 12 logical cores |
| RAM | 15 GiB available to WSL2 |
| OS | WSL2, kernel 6.6.87.2-microsoft-standard-WSL2 |
| Runtime | Docker Desktop 29.1.3 |
| Network | loopback only — **no real network latency, loss or MTU** |

**This is a developer workstation, not a VPS.** Everything below inflates or
deflates the numbers in ways that are not quantified away:

1. **The load generator ran on the same box as the server under test, and cost
   more CPU than it.** Measured at 150 players: generator **123%** of one core,
   game server **108%**. Host load average during runs was **7.3 on 12 cores**,
   against a 3.5 baseline before any load. A generator on separate hardware would
   plausibly move the break point up.
2. **Docker Desktop, a Kubernetes control plane, Agones controllers and an AI
   coding agent were all running throughout.** They are the 3.5 baseline load.
3. **Loopback networking.** No RTT, no packet loss, no MTU fragmentation, and the
   242 KB/s per client at 200 players never touched a NIC. Real clients on mobile
   networks will behave worse in ways this run cannot show — especially given the
   bandwidth finding.
4. **Windows/WSL timer granularity** is the likely cause of the 14.7Hz drift; a
   Linux VPS may not show it.
5. **Postgres writes were live** (`GAME_DB_URL` set, async saver every 30s), so
   the 30-second save sweep is included — but a 35s window may catch one sweep or
   two, adding variance.
6. **Single map, single server, no Agones allocation, no gateway in the path.**
   Nothing here measures allocation latency, map transfer, or gateway login
   throughput; those are ADR-7 items 4 and 5 and remain unmeasured.

**How to read the result:** treat 150 as a *floor* for a quiet dedicated VPS core
and a *ceiling* for anything noisier. The bottleneck identification
(serialization ≫ AOI, 5:1) is far more robust than the absolute number, because
it is a ratio measured within the same run conditions and reproduced two
independent ways.

---

## 9. What to do about it

Ordered by measured impact:

1. **Fix the entity leak** (`gameserver-dotnet`). Unbounded O(n²) growth beats
   every optimisation below.
2. **Replace JSON with Protobuf/FlatBuffers on the snapshot path.** This is the
   80% term in the tick and the whole of the bandwidth problem. Already the
   planned production encoding — the measurement says it is the *first* thing to
   do, not a later polish.
3. **Move serialization off the tick.** `WireProtocol.NewEnvelope` serializes
   inside the tick loop while `Connection.Send` only enqueues. Serializing in the
   writer task instead would take the dominant term off the critical path without
   changing the encoding.
4. **Stagger keyframe counters per connection** to kill the stampede.
5. **Spatial-grid AOI** — worth doing, but it targets the 20% term. ADR-7 ranked
   it first; the measurement demotes it to fourth.
6. **Re-run on real VPS hardware** with the generator on a separate host before
   any tier CCU number is published.

---

## 10. Reproducing

```bash
cd backend/loadtest
go build -o loadtest ./cmd/loadtest

# Single level against the stock dev stack, full gateway path.
JWT_SECRET=dev-secret-change-me ./loadtest -players 10 -duration 60s

# The capacity sweep as run here: dedicated server, direct join.
docker run -d --name rpg-gs-bench --network rpg-mmo-meta_default \
  -p 9300:9000 -p 9301:9101 \
  -e JWT_SECRET=dev-secret-change-me -e GAMESERVER_ADDR=:9000 \
  -e GAMESERVER_MAP_ID=map_bench -e GAMESERVER_ID=gs-bench \
  -e GAMESERVER_CAPACITY=2000 -e METRICS_ADDR=:9101 \
  rpg-mmo/gameserver-dotnet:dev

JWT_SECRET=dev-secret-change-me ./loadtest \
  -join direct -gameserver-addr 127.0.0.1:9300 -server-id gs-bench \
  -gameserver-metrics http://localhost:9301/metrics -gateway-metrics "" \
  -sweep 50,100,150,200 -duration 35s -warmup 8s -movement cluster \
  -json sweep.json
```

Restart the container and wait for `gameserver_entities` to read 0 between
levels, or the leak in §7 will contaminate the results.
