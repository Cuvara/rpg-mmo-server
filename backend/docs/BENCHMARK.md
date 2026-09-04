# Benchmark — measured game-server capacity

> **Which number should you plan on? Bandwidth.** Measured across six repeats of
> a fixed level on this host:
>
> | statistic | spread across 6 runs | under a deploy sharing the host |
> |---|--:|--:|
> | **KB/s per client** | **0.3%** | barely moved |
> | tick p99 | 5.1% | **3.3×** |
> | tick mean | 5.9% | 1.7× |
>
> Bytes on the wire do not care how busy the machine is; tick timings do, by
> multiples. Bandwidth is also the *binding* constraint — it breaks ADR-7's
> mobile threshold at roughly a third of the tick ceiling. **Size a fleet on the
> bandwidth figure; treat every tick/CCU number here as a lower bound.**

> **⚠️ Part I below measures the pre-Protobuf server and is kept as the
> historical baseline. For current numbers see
> [Part II](#part-ii--protobuf-vs-json-2026-08-07): after the Protobuf migration
> downstream is **~55% smaller** — reproduced to within 0.4% across two sweeps on
> different builds ([§16](#16-reproduction-a-withdrawn-claim-and-a-run-that-lied-in-our-favour)) —
> and the tick ceiling roughly doubles. Treat the ceiling figures as approximate:
> §16 shows they are single threshold crossings of a noisy p99 and one of them
> was withdrawn on re-run. **The mobile bandwidth ceiling is still only ~93
> players, and that, not the tick ceiling, is what should size a fleet.**

> **⚠️ The ~150-player figure in Part I is STALE.** It predates Protobuf, the
> entity-type enum and id interning — three changes that removed 81% of the wire
> and with it the constraint that produced 150. **The current tick ceiling is
> unknown**, and cannot be measured on this host: the load generator shares the
> machine with the server under test and uses more CPU than it. Bandwidth, by
> contrast, is solved — 45.9 KB/s per client at 200 players, inside ADR-7's
> threshold, measured to 0.3%. See [Part IV](#part-iv--entity-id-interning-2026-08-07)
> and ADR-7's CURRENT STATE block.

> **Headline (Part I, pre-Protobuf): one game server holds ~150 concurrent players in the worst-case
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

- Client-observed **snapshot interval** = **monotonic** gap between consecutive
  `MsgSnapshot` arrivals, per player, pooled across players. Monotonic, not
  wall-clock: the generator takes both endpoints from Go's `time.Now()` and
  subtracts them with `Time.Sub`, which uses the monotonic reading Go embeds in
  every `time.Time`. On this host that distinction is worth 10-17% — see
  [the host clock](#the-host-clock-and-which-figures-it-touches).
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
(Measured before `speed` was added as field 9 in 2026-08; the entity is 7 fields now,
+5 bytes in Protobuf. The figures below are left as captured rather than rescaled —
a benchmark is a record of a run, not a live estimate.)
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

> **This is not the host-clock bug (#153), and the arithmetic is how you tell.**
> Both figures are measured against `Stopwatch`/Go-monotonic, so the 10-17% fast
> `CLOCK_REALTIME` never enters them; had it, 15Hz would have read as **~12.9**,
> not 14.7. A reading near 54Hz on a 60Hz loop, or near 12.9 on a 15Hz one, is
> the *instrument* — see
> [the host clock](#the-host-clock-and-which-figures-it-touches). A reading of
> 14.7 on a 15Hz loop is the *server*.

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
7. **`CLOCK_REALTIME` on this host runs 10-17% fast** against `CLOCK_MONOTONIC`,
   by an amount that drifts between sessions. Nothing in this document is
   computed from it — every figure here is monotonic-derived, audited
   figure-by-figure below — but any *new* measurement taken with `date`, a shell
   `$SECONDS`, or a Prometheus `rate()` over server-assigned scrape timestamps
   will be wrong by that much. See
   [the host clock](#the-host-clock-and-which-figures-it-touches). Refs #153.
8. **A measurement taken through the k3d serverlb measures the proxy.** Every
   gameplay packet to an Agones pod on k3d crosses an nginx TCP proxy that does
   not exist on a real node, and it triples snapshot jitter. Nothing in this
   document was measured that way — Part I and Part II both ran against a
   directly-dialled server — but the local Agones rig is the obvious place to
   sweep capacity next. See
   [the k3d serverlb](#the-k3d-serverlb-is-in-the-gameplay-data-path). Refs #143.

**How to read the result:** treat 150 as a *floor* for a quiet dedicated VPS core
and a *ceiling* for anything noisier. The bottleneck identification
(serialization ≫ AOI, 5:1) is far more robust than the absolute number, because
it is a ratio measured within the same run conditions and reproduced two
independent ways.

### The host clock, and which figures it touches

**Rule: never derive a rate from a wall clock on this box.** Not `date`, not
`$SECONDS`, not `time.time()`, not `DateTime.UtcNow`, not a Prometheus `rate()`
that leans on server-assigned scrape timestamps.

On this WSL2 host `CLOCK_REALTIME` runs *fast* relative to `CLOCK_MONOTONIC`, and
**the amount is not stable** — measured at **+11.1%**, **+16.7%** and **+16.65%**
in three sessions on different days. It cannot be corrected with a constant, only
avoided. `timedatectl` has reported `System clock synchronized:` both `no` and
`yes` at different moments; clocksource is `tsc` under Hyper-V. **k3d pods share
this kernel**, so a measurement taken inside a pod carries the identical artifact.

Reproduce it in twenty seconds:

```bash
python3 -c "
import time
m0=time.monotonic(); r0=time.time()
time.sleep(20)
print('monotonic', time.monotonic()-m0, 'realtime', time.time()-r0)"
# monotonic 20.00009545798821 realtime 23.331291913986206   -> +16.65%
```

**This has already cost real work.** Issue #147 reported the game server ticking
at **54 Hz** against an advertised 60, was filed, propagated into an ADR in an
open PR, and was cited as the likely root cause of a client prediction defect.
All of it was the instrument: `TickLoop` paces on `Stopwatch`
(`CLOCK_MONOTONIC`), and the observer timed it with `date` (`CLOCK_REALTIME`).
The same idle process, measured against both clocks at once, read 59.99 Hz and
54.57 Hz simultaneously. #147 is closed as not-a-defect. Refs #153.

#### Audit: every figure in this document, against its clock

Traced to source, not assumed. **No figure in this document is known to be
affected, and exactly one class could not be traced — it is named and marked in
the table rather than quietly passed.** The load generator is Go, where
`time.Now()` embeds a monotonic reading and `Time.Sub`/`time.Since` use it in
preference to the wall clock; the server is C#, where the tick histogram and the
loop pacing both come from `Stopwatch.GetTimestamp()`. Neither ever reads
`CLOCK_REALTIME` for an interval.

**The audit covers every Part, not only the loadtest sweeps.** Parts III, IV and
VI report the same statistics as Part I from the same harness, so the first six
rows below carry them. Parts covering in-process measurements (§23, Part V) do
not go through the loadtest at all and are audited separately in the last three
rows.

| Figure | Interval comes from | Affected? |
|---|---|---|
| tick p50 / p99 / mean, % ticks over budget | C# `Stopwatch.GetTimestamp()` / `Stopwatch.Frequency` (`GameMetrics.RecordTickDuration`, `TickLoop`) | **No** |
| achieved `ticks/s` | scrape-to-scrape `afterGS.At.Sub(beforeGS.At)`, both `At` set from Go `time.Now()` at scrape time, never round-tripped through a string | **No** |
| snapshot interval p50 / p99 | Go `now.Sub(lastSnap)`, both endpoints from `time.Now()` | **No** |
| ack latency p99 | Go `now.Sub(pi.sent)`, same | **No** |
| join latency | Go `time.Since(start)` | **No** |
| KB/s per client, MB/s total | byte counters ÷ `time.Since(windowStart).Seconds()` | **No** |
| peak RSS (MiB) | a byte count — no interval in it at all | **No** |
| `host.load_avg_1` / `_5` / `_15` ([§16](#16-reproduction-a-withdrawn-claim-and-a-run-that-lied-in-our-favour)) | read verbatim from `/proc/loadavg` (`loadtest/load/host.go:60`) — a kernel-computed figure, no interval taken in the harness | **No** |
| allocation B/tick ([§23](#23-snapshot-allocation-what-pooling-and-buffer-reuse-actually-removed)) | `GC.GetAllocatedBytesForCurrentThread` — a byte count over a fixed 60-tick loop, no clock read at all. §23 states outright that **no wall-clock claim is made** | **No** |
| brute-vs-indexed µs, Part V ([Part V](#part-v--the-spatial-index-that-lost-2026-08-14)) | ⚠️ **Not traceable.** The paired in-process A/B harness was never committed — `2e3e5db` carries `SpatialGrid`, `EcsWorld` and `AoiIndexDifferentialTests` and no benchmark file — so the clock behind the µs columns cannot be read back from the repo | ⚠️ **Ratios: no.** The 1.42–2.92× / 0.32–0.45× / 0.81–0.89× ratios are taken within one run, so a shared skew cancels and the *conclusion* of Part V stands. **Absolute µs: unverified** — see below |
| Protobuf-vs-JSON savings %, the 5:1 `still`-vs-`cluster` ratio, run-to-run spread % | ratios of the rows above, taken within one run | **No** — and a ratio would cancel a shared skew even if there were one |

**The one unverified figure, kept rather than deleted.** Part V's absolute
microsecond columns (77–136 µs brute, 38–84 µs indexed, and the rest) are the
only figures in this document whose clock cannot be traced to source, because the
harness that produced them was not kept. They are **not** withdrawn and **not**
removed: deleting them would destroy the record of a measurement that was
actually taken, and the skew band on this host is 10-17%, far narrower than the
2.8× effect Part V reports. Read them as **order-of-magnitude only** — they are
there to show that the distance tests cost microseconds against a 66 ms budget, a
claim three orders of magnitude clear of any clock artefact. What would replace
them: re-run the A/B with a committed harness that states its clock, the way
[§23](#23-snapshot-allocation-what-pooling-and-buffer-reuse-actually-removed)'s
`SnapshotAllocationTests.cs` does. Until then, quote Part V's **ratios**, never
its microseconds. Refs #153.

**Scope of the audit: the gateway was swept too, and no figure here depends on
it.** Nothing in this document is measured through the gateway — confound 6
records that Part I and Part II both ran with no gateway in the path — but it was
checked rather than assumed. The gateway derives **no rates at all**: every
`rate` in it is a rate *limiter* (a configured policy), and `shared/ratelimit`
refills from `now.Sub(b.last)` with both endpoints from `time.Now()`, so it is
monotonic and correct. Session `CreatedAt`/`LastActivity` are wall-clock
*stamps*, not intervals, and expiry is enforced by Redis' own TTL rather than by
arithmetic in Go.

The sweep did turn up one true wall-clock interval, which is a robustness matter
rather than a measurement one and is recorded here only so the audit is closed
rather than left open: `gateway/server/connection.go` compares
`time.Since(time.UnixMilli(last)) > pongTimeout`. `time.UnixMilli` returns a
`time.Time` with **no** monotonic reading — verified, not assumed: a
monotonic-bearing `time.Time` renders a trailing `m=+…` and the rebuilt one does
not — so `time.Since` falls back to wall-clock subtraction there. See
[the gateway heartbeat](#the-gateway-heartbeat-is-the-one-true-wall-clock-interval).

Two corollaries worth stating, because both are easy to get backwards:

- **The 14.7Hz drift ([§5](#the-15hz-that-is-really-147hz)) is real and is *not*
  this bug.** A 16.7% fast wall clock would have made 15Hz read as **~12.9**, not
  14.7. The 2% shortfall is measured against `Stopwatch` and stands; confound 4
  (Windows/WSL timer granularity) remains the live explanation.
- **The re-check #153 asked for is done and it changed no number.** The issue was
  right that the hazard is severe and right to demand the audit; the audit's
  answer happens to be that every harness still in the repo was already
  clock-correct. That is a property of the harnesses, not a reason to relax the
  rule — the rule protects the *next* measurement, which may well be taken by
  hand at a shell prompt. The audit did downgrade one figure class it could not
  trace (Part V's absolute microseconds, above) from *verified* to
  *order-of-magnitude only*, without changing its value or the conclusion it
  supports.


#### To check the tick rate, read `achieved_tick_hz`

The server publishes its **measured** rate, so an observer never has to supply a
clock:

| Surface | Field |
|---|---|
| `/status` JSON | **`achieved_tick_hz`** |
| `/metrics` | **`gameserver_achieved_tick_hz`** (gauge, labelled `map_id`) |

Both come from an `AchievedRateMeter` fed once per base tick from
`Stopwatch.GetTimestamp()` — the *same* timestamps that pace the loop, so the
measurement and the schedule it measures cannot disagree about what a second is.
It is a **2 second sliding window**, and there is deliberately no `DateTime`
overload.

Read it against the **configured** rate on the same endpoint: `sim_critical_hz`
(with `tick_rate` the same number, and `sim_world_hz` / `sim_background_hz` for
the other groups). A healthy server has `achieved_tick_hz ≈ sim_critical_hz`.
Configured and measured are now distinct fields, which is the whole point.

> **`achieved_tick_hz == 0` means "not measured yet"** — no window has completed,
> i.e. the process is younger than ~2s. It does **not** mean the loop has
> stopped; `current_tick` distinguishes those. This is the one way to misread the
> field.

**Do not compute the rate yourself from `current_tick / uptime_seconds`.** This
is the arithmetic that produced #147, and it is wrong in one of two ways
depending on which build you are on:

- **Before the #144 fix**, `uptime_seconds` came from `DateTime.UtcNow` —
  `CLOCK_REALTIME` — so on this host the quotient returned **~51 Hz on a healthy
  60 Hz loop** and **~12.9 Hz on a healthy 15 Hz loop**. Reproduced live at
  **54.10 Hz** (`38250 / 707`) on a loop genuinely running 60. That is #147 from
  inside the server's own endpoint, with no `date` involved.
- **After it**, `uptime_seconds` is `Stopwatch`-derived, so the quotient is no
  longer clock-skewed — but it is a **since-boot average**, which hides a loop
  that degraded recently. Merely worse, rather than wrong.

Either way `achieved_tick_hz` is the answer. Note the behaviour change in the
second case: `uptime_seconds` is now elapsed *process* time, so it no longer
follows a clock step (NTP correction, suspend/resume) and can legitimately
disagree with `date`-derived arithmetic on a drifting host. That disagreement is
the intended outcome.

Full treatment, including the two-clock account of the old arithmetic:
[`gameserver-dotnet/docs/METRICS.md`](../gameserver-dotnet/docs/METRICS.md#do-not-compute-a-rate--read-achieved_tick_hz).

**Scope:** the gauge measures the **base timeline only**. World and background are
exact integer divisors of the base rate, so publishing three measured rates would
be one measurement plus two pieces of arithmetic — three things that can drift
instead of one. For per-group measured rates use
`rate(gameserver_sim_group_runs_total[...])`.

#### The gateway heartbeat is the one true wall-clock interval

Found by the sweep above; **not** a figure in this document, and it changes no
number here. Recorded because the audit named the gateway as unchecked and this
is what checking it produced.

`gateway/server/connection.go` enforces the heartbeat as:

```go
if time.Since(time.UnixMilli(last)) > pongTimeout {   // wall clock, not monotonic
```

`pingInterval` is 10s and `pongTimeout` 30s, so the code documents a margin of
one full `pingInterval`: `MaxHandlerBlockingWait = pongTimeout - pingInterval` =
**20s**. The gateway refuses to start with `--allocation-wait-timeout` above that
value, precisely because the allocation wait blocks the read loop that records
`MsgPong`.

**The two sides of that margin run on different clocks.** The allocation wait is
a Go timer/context deadline, which is monotonic; the pong timeout is wall-clock.
On this host, where `CLOCK_REALTIME` runs ~16.65% fast, a nominal 30s pong budget
elapses in about **25.7s** of real time, so the enforced margin is nearer **17.1s**
than the 20s the constant asserts. The default 15s allocation wait still fits, so
nothing is broken today — but the guard compares a monotonic duration against a
wall-clock-enforced constant, and the safety margin it computes is not the one in
force.

This is not specific to this box. A wall clock can also be *stepped* by NTP, in
either direction, on any host; that is the standard reason timeouts are taken
from a monotonic source. The fix is to store the pong time as a
`Stopwatch`-equivalent monotonic stamp — in Go, keep the `time.Time` from
`time.Now()` (or `time.Since` a stored one) rather than round-tripping it through
`UnixMilli`, which is exactly what strips the monotonic reading.

**Left unfixed deliberately:** it is gateway runtime behaviour, not a document
figure, and changing a heartbeat timeout wants its own change and tests.

#### Rate skew and clock steps are different faults — check the sign

This host has two distinct clock problems, and matching a discrepancy on
*magnitude alone* attributes one to the other. Before blaming either, check which
direction the error runs:

| | Rate skew | Clock step |
|---|---|---|
| What it is | `CLOCK_REALTIME` advances ~10-17% too fast, continuously | The clock jumps, e.g. on an NTP resync (`timedatectl` here has flapped between `synchronized: no` and `yes`) |
| Effect on a measured **duration** | inflated — a real 20s reads as ~23.3s | one-off offset, either direction |
| Effect on a measured **rate** | **understated** — a real 60 Hz reads as ~51 Hz | one-off, either direction |
| Effect on a **countdown/TTL** | decays faster, so remaining time reads **lower** | remaining time can read **higher** (backward step) or lower |
| Does "never derive a rate from a wall clock" fix it? | Yes | **No** — a step corrupts a single reading, not a rate |

**Worked example, because this caught a real wrong hypothesis.** A Redis TTL
assertion failed reading **16.87s against a 15s ceiling**, and +12.5% sits neatly
inside the 10-17% skew band — so it looked like this bug. It is not. The registry
sets a **relative** expiry (`KeyExpireAsync(key, ttl)` → `PEXPIRE`), so Redis
computes the deadline on its own clock and the remainder cannot exceed 15s by
construction. And decisively, **a fast clock makes a TTL decay faster, so it
reads lower, never higher.** The observation ran the wrong way for the mechanism
it was attributed to. A backward *step* between the `PEXPIRE` and the read is the
leading explanation — same unhealthy-clock family, different fault, and untouched
by the rule above. (Leading hypothesis, not established: the flap is not
reproducible on demand.)

The general lesson is cheap to apply: **magnitude matching is not diagnosis.**
Confirm the sign before attributing a discrepancy to a known clock fault — which
is the same discipline #147 failed, in a different costume.

If you add a measurement to this document, state the clock it came from.

### The k3d serverlb is in the gameplay data path

**Do not take capacity numbers through an Agones pod on k3d.** Use the compose
path, or dial a node directly. Same binary, same load, same box — only the
network path differs:

| 50 players, 20s, proto | snapshot interval p99 | tick p99 | recv | verdict |
|---|--:|--:|--:|---|
| Agones pod via k3d serverlb (`127.0.0.1:7069`) | **211.9 ms** | — | — | DEGRADED — over 2x the 133.3ms budget |
| compose server, direct (`127.0.0.1:9200`) | **72.7 ms** | 0.58 ms | 100% | healthy |

At 80 players the compose path still reports tick p99 **3.06ms** and **0% of
ticks over budget**, so the simulation is not the constraint in either row. The
proxy is.

**Cause.** On k3d the Agones dynamic port range (7000-7100) is published by the
`k3d-<cluster>-serverlb` container, an nginx TCP proxy, so every gameplay packet
to an Agones pod traverses it. On a real node the client dials the node directly
and the hop does not exist. This is the same mechanism that makes k3d usable for
us at all — Docker Desktop never publishes Kubernetes `hostPort`, k3d does
([ADR-16](ARCHITECTURE-DECISIONS.md) decision 1) — so it is a property of the
local rig, not a regression.

**Why it is worth a section.** It runs in the opposite direction from the
distortion people expect. ADR-7's confound depresses numbers through *CPU*
contention; this one depresses them through the *network path*, and it does so
by roughly 3x. Without it written down, the first person to sweep capacity on
k3d reports a ceiling about a third of the truth and attributes it to the game
server. That makes three local-measurement traps on this box, all of which make
a healthy system look broken: the co-located load generator (ADR-7, confound 1),
the host clock ([#153](#the-host-clock-and-which-figures-it-touches)), and this
one. Refs #143.

---

---

## 9. What to do about it

Ordered by measured impact:

1. **Fix the entity leak** (`gameserver-dotnet`). Unbounded O(n²) growth beats
   every optimisation below.
2. ~~**Replace JSON with Protobuf/FlatBuffers on the snapshot path.**~~ ✅ **Done**
   — see [Part II](#part-ii--protobuf-vs-json-2026-08-07). Delivered ~55% less
   downstream and a 150 → 300 tick ceiling, but *less* than this entry assumed:
   the saving is ~55% not ~70%, because entity ID strings cost the same in both
   encodings, and bandwidth remains the binding constraint at ~93 players.
3. ~~**Move serialization off the tick.**~~ ✅ **Done** — the tick thread now only
   stages a gather and a marker (`Connection.GatherSnapshotView`); encoding and
   protobuf serialization run on each connection's own write task
   (`Connection.WriteLoopAsync`, `TickLoop`'s broadcast phase). An earlier
   revision of this line still said "still outstanding"; the 2026-08 gameplay
   perf audit found the code had done it (issue #237 notes the stale line).
   `TickBreakdownBench` measures the tick with and without the write tasks live.
4. **Stagger keyframe counters per connection** to kill the stampede.
5. **Reduce what is sent, not how it is encoded** — AOI radius, distance-tiered
   update rates, interned entity IDs. Part II shows this is now the only lever
   left on the bandwidth ceiling: at 150 players Protobuf still costs 80.7 KB/s
   per client against a 50 KB/s target, and **61%** of a packed entity is string
   data that no encoding can compress away (measured: 17.0 bytes of `id` plus 8.0
   of `type`, against 41.2 bytes marginal cost per entity).
6. ~~**Spatial-grid AOI**~~ ❌ **Measured and rejected** — see
   [Part V](#part-v--the-spatial-index-that-lost-2026-08-14). It was built,
   verified correct against the brute-force scan, and is **2.8x slower** at
   realistic density. The AOI scan's cost is not the distance tests.
7. **⛔ BLOCKER — put the generator on a separate machine.** Not "before
   publishing a tier table": before *any* further capacity work. Bandwidth is now
   solved to the threshold and tick binds instead, and tick is the statistic a
   co-located generator distorts — measured 3.3x on p99 with a dose-response
   against host load. Optimising against it would be tuning to an instrument
   known to be measuring the wrong thing. See
   [Part IV](#part-iv--entity-id-interning-2026-08-07) and ADR-7's CURRENT STATE
   block.

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

### Part II — the encoding comparison

```bash
cd backend/loadtest
JWT_SECRET=dev-secret-change-me ./scripts/encoding-sweep.sh 50 100 150 200
python3 scripts/encoding-report.py results/encoding
```

The script builds nothing: it expects the baseline image
(`rpg-mmo/gameserver-dotnet:<develop-sha>`, override with `BASELINE_IMAGE`) and
the branch image (`rpg-mmo/gameserver-dotnet:proto`, override with `NEW_IMAGE`)
to exist already. It restarts the container and waits for `gameserver_entities`
to read 0 before every level, for the same reason as Part I.

---

# Part II — Protobuf vs JSON (2026-08-07)

> **Headline: Protobuf cuts downstream bandwidth by ~55% and doubles the
> tick-budget ceiling from 150 to 300 players. But the win is smaller than the
> naive estimate in §5 predicted, roughly half the tick improvement is not
> actually the encoding, and bandwidth remains the binding constraint.**

Measured on the same machine, same protocol and same 35s/8s windows as Part I,
against `develop` @ `f4d5561` (baseline) and `feat/shared/protobuf-wire`.
Raw results: [`backend/loadtest/results/encoding/`](../loadtest/results/encoding/).
Reproduce with [`backend/loadtest/scripts/encoding-sweep.sh`](../loadtest/scripts/encoding-sweep.sh).

## 11. Method — three arms, one load generator

| Arm | Server image | Encoding | Isolates |
|---|---|---|---|
| `baseline-json` | `develop` @ `f4d5561` | JSON | The Part I baseline |
| `new-json` | this branch | JSON | The JSON-path cleanup **only** |
| `new-proto` | this branch | Protobuf | The encoding change |

The **same** `loadtest` binary drove all three: `-encoding json` emits
byte-identical legacy frames, and the server answers in whatever encoding it is
addressed in, so `new-json` and `new-proto` are the *same running binary* under
one flag. Only the server changes between arms, and between the last two, nothing
changes at all except what the client asked for.

**The middle arm exists for honesty.** This branch also removed a
`JsonDocument.Parse` round-trip from the JSON path (the envelope used to re-parse
its own freshly serialized payload just to nest it). That is a real improvement
but it is *not* Protobuf, and folding it into the headline would overstate the
encoding's contribution. It turns out to be about half the tick win.

**Baseline reproduction.** `baseline-json` reproduced Part I closely enough to
trust the comparison: 61.1 vs 61 KB/s at 50 players, 183.8 vs 184 at 150, and
tick p99 at 150 of 49.77ms against Part I's 49.77ms.

## 12. Results

### Bandwidth — the thing this was for

| Players | KB/s/client JSON | KB/s/client Protobuf | saved |
|--:|--:|--:|--:|
| 50 | 60.3 | 27.4 | **54.6%** |
| 100 | 121.6 | 53.8 | **55.7%** |
| 150 | 183.4 | 80.7 | **56.0%** |
| 200 | 243.1 | 109.2 | **55.1%** |
| 250 | 281.3 | 131.3 | **53.3%** |

Flat at ~55% across the whole range, which is what a pure re-encoding should look
like — the saving is per byte, not per player.

### Tick time

| Players | tick mean baseline | tick mean new-json | tick mean proto | tick p99 baseline | tick p99 new-json | tick p99 proto |
|--:|--:|--:|--:|--:|--:|--:|
| 50 | 4.46ms | 3.19ms | 1.66ms | 14.05ms | 9.90ms | 7.93ms |
| 100 | 14.33ms | 10.47ms | 4.44ms | 25.00ms | 24.73ms | 17.38ms |
| 150 | 29.27ms | 20.63ms | 10.58ms | 49.77ms | 47.51ms | 24.81ms |
| 200 | 50.79ms | 34.40ms | 17.26ms | 85.32ms | 53.46ms | 40.80ms |
| 250 | — | 60.34ms | 25.80ms | — | 98.99ms | 49.58ms |
| 300 | — | 95.42ms | 34.29ms | — | 245.64ms | 49.99ms |
| 400 | — | — | 72.27ms | — | — | 239.73ms |

### The new ceilings

| Arm | Highest passing (tick p99 < 66.67ms) | First breach |
|---|--:|--:|
| `baseline-json` | **150** | 200 (p99 85.32ms) |
| `new-json` | **200** | 250 (p99 98.99ms) |
| `new-proto` | **300** | 400 (p99 239.73ms) |

**150 → 300 players, a 2× ceiling.** Attributed honestly below — but see
[§16](#16-reproduction-a-withdrawn-claim-and-a-run-that-lied-in-our-favour),
which **withdraws the middle step** (it did not reproduce) and shows that ceiling
figures generally are threshold readings on a statistic too noisy to carry them:

- `baseline` → `new-json`: **150 → 200 (+33%)** from removing the
  `JsonDocument.Parse` round-trip. Not the encoding.
- `new-json` → `new-proto`: **200 → 300 (+50%)** from Protobuf itself.

At 300 players the JSON arm is fully broken (100% of ticks over budget, p99
245.64ms) while the proto arm is still comfortably inside budget at p99 49.99ms
on the *same binary* — the single clearest side-by-side in this document.

At 400 players the proto arm collapses (p99 239.73ms, 84.7% of ticks over
budget), and its measured bandwidth *drops* from 147.2 to 140.0 KB/s — the server
is no longer keeping up, so it emits fewer snapshots. A falling bandwidth number
at a rising player count is a degradation signature, not an improvement.

## 13. Where the win is smaller than predicted — read this

**§5 estimated an `EntitySnapshot` at ~95 bytes of JSON versus "~30 bytes
packed", implying a ~70% saving. The measured saving is ~55%.** The estimate was
wrong about what dominates.

Protobuf removes field names, punctuation, and decimal float formatting. It
cannot remove *identifiers*. A realistic entity ID (`lt-000000000042`, 15 chars)
costs 15 bytes in both encodings, and is ~15 of the ~40 bytes a Protobuf entity
occupies. The floor is the string data, and **61% of a packed entity is string**
— measured marginally at 50 entities: 41.2 bytes each, of which `id` is 17.0 and
`type` is 8.0. (An earlier revision of this section said ~40%; it counted only
`id`.) This is asserted, not just described, in
`shared/messages` `TestProtoIsSmallerThanJSON` and measured on the real wire by
`TestDotnetInterop_MixedEncodingsOnOneServer`.

The lever that would move it further is interning entity IDs (and making
`entity.type` an enum). Both change what the field *means* rather than how it is
encoded, so both were deliberately left out of this change — see ADR-9.

## 14. ADR-7 thresholds, re-checked

| Threshold | Before | After | Verdict |
|---|--:|--:|:--|
| Tick p99 < 66.67ms @ 15Hz | 150 players | **300 players** | improved 2× |
| Downstream < 50 KB/s per client | ~41 players | **~93 players** | improved 2.3×, **still fails** |

**Bandwidth is still the binding constraint, and by a wider margin than before.**
The tick ceiling moved to 300 while the mobile-viable bandwidth ceiling moved
only to ~93 — the gap between "the server can simulate it" and "a phone can
receive it" widened from 3.7× to 3.2× in ratio but grew from 109 to 207 players
in absolute terms. Protobuf did not solve the bandwidth problem; it bought
roughly a 2.3× and moved the next bottleneck nowhere.

**A dense-crowd capacity plan should still use ~93, not 300**, on the mobile
assumption. If crowds of 150+ in one AOI are a real design requirement, the next
work is not another encoding — it is reducing *what* is sent (AOI radius, update
rate tiering by distance, ID interning), because at 150 players Protobuf still
costs 80.7 KB/s per client.

## 15. Additional confounds for Part II

Everything in §8 still applies. Two more:

1. **Another agent was running its own load tests against a different game
   server container on this same host during parts of this sweep.** Host load was
   therefore higher and less stable than in Part I. This inflates absolute tick
   times in an unknown and non-uniform way. The *ratios* between arms are more
   robust than the absolute numbers, and the ~55% bandwidth saving is a property
   of the encoding that host load cannot affect at all.
2. **The entity leak (§7.1) was still live when this sweep ran.** Every level
   restarted the container and waited for `gameserver_entities` to read 0, and
   clients stay connected for the whole window, so within-level contamination
   should be near zero — but "should be" is not "was measured to be". The leak
   fix changes tick cost characteristics, so **these numbers must be re-taken
   after rebasing onto it** before any of them are quoted as current.
3. ~~**A single client failed to join in three of the levels.**~~ **Explained and
   fixed** — see §16. It was not load: the sweep waited for `/metrics` before
   starting a level, but `Program.cs` starts the metrics endpoint well before the
   game listener, so a level could begin while the game port was still not
   accepting and lose a client to `connection reset by peer` during join.
   Reproduced at roughly **1 cold start in 4**, and eliminated (0 in 5) by probing
   the game port itself before the level. The readiness check was waiting on the
   wrong port.

---

## 16. Reproduction, a withdrawn claim, and a run that lied in our favour

Part II was re-run in full after the entity-leak fix, with the baseline image
rebuilt from `7c4108b` so the leak fix sits on **both** sides of the comparison.
Raw data: [`backend/loadtest/results/encoding-rerun/`](../loadtest/results/encoding-rerun/),
kept beside the first run rather than overwriting it so both are quotable.
That matters: after rebasing, the Protobuf branch carried the leak fix while the
original baseline image did not, so comparing against the old image would have
credited someone else's fix to Protobuf — the same error the middle arm exists to
prevent, one level up.

### What reproduced: the bandwidth saving

| Players | baseline | new-json | new-proto | saved |
|--:|--:|--:|--:|--:|
| 50 | 60.9 | 60.1 | 26.8 | **55.4%** |
| 100 | 121.7 | 120.1 | 54.4 | **54.8%** |
| 150 | 183.1 | 181.5 | 81.4 | **55.1%** |
| 200 | 238.9 | 242.1 | 108.8 | **55.1%** |

Within 0.4% of the first sweep at every level, on a different build. **This is
the number to quote.** It is a property of the encoding, it reproduced across two
sweeps, and host load cannot affect it — a byte is a byte on any machine.

The baseline arm is unchanged by the leak fix, as expected: the loadtest's
clients close gracefully and never trigger the RST path the leak needed.

Tick *means* also reproduced directionally — roughly 30% off from the JSON-path
cleanup, roughly another 50% from the encoding:

| Players | baseline | new-json | new-proto |
|--:|--:|--:|--:|
| 50 | 4.93ms | 3.20ms | 1.74ms |
| 100 | 14.71ms | 9.54ms | 5.42ms |
| 150 | 30.21ms | 22.34ms | 11.13ms |
| 200 | 55.53ms | 39.28ms | 17.61ms |

### ⚠️ Withdrawn: "the JSON cleanup alone moved the ceiling 150 → 200"

[§12](#12-results) reported per-arm ceilings of 150 / 200 / 300. **The middle
number did not reproduce.** On the second sweep `new-json` breaches at 200
(p99 72.47ms) and its ceiling is 150 — the same as baseline.

| Arm | ceiling, run 1 | ceiling, run 2 |
|---|--:|--:|
| `baseline-json` | 150 | 150 |
| `new-json` | 200 | **150** |
| `new-proto` | 300 | ≥ 200 (not swept higher) |

The cause is not a measurement mistake; it is the criterion. At 150 players
`new-json` measured p99 47.51ms then 49.74ms — nearly identical. At 200 it
measured 53.46ms then 72.47ms, straddling the 66.67ms budget. **The underlying
distribution barely moved; the reported ceiling moved by 50 players**, because a
ceiling is a single threshold crossing of a noisy tail statistic.

Protobuf's advantage over JSON does not depend on this: it is large and
consistent at every level in both runs (11.13ms vs 22.34ms mean at 150, 17.61ms
vs 39.28ms at 200). It is the *attribution between baseline and new-json* that
cannot be resolved at ceiling granularity.

### The tick-ceiling criterion is not decidable as ADR-7 states it

ADR-7's acceptance threshold is "tick p99 within the 66.67ms budget", evaluated
at one level, from one run. The measurement above shows that criterion cannot
reproduce a ceiling to better than ±50 players, so every capacity number derived
from it inherits that instability.

**Recommendation, in preference order:**

1. **Report a band, not a number.** A ceiling is the highest level that passes in
   *every* run of N ≥ 3; levels that pass in some runs and not others are the
   band, and should be published as "150–200", not as either endpoint.
2. **Judge on the mean, report the tail separately.** Tick *mean* reproduced
   within 10% across runs where p99 moved 35%. A criterion of "mean within half
   the budget" plus a separately reported p99 would be decidable and would still
   catch the failure mode p99 is there to catch.
3. **At minimum, never quote a ceiling from a single run.** Every ceiling in this
   document before §16 came from one sweep and should be read as approximate.

ADR-7 has been amended to record this; see
[ADR-7](ARCHITECTURE-DECISIONS.md#adr-7--ccu-and-cost-figures-are-unbenchmarked-estimates).

### A run that reported a 97% bandwidth saving, and did not measure anything

The first attempt at re-running levels 150 and 200 produced savings of **84.7%**
and **97.0%**. Both were discarded. The tells were all present in the result
files:

```
150p  joined=150  failed=150  recv_ratio=1.40  entities=200  online=200
200p  joined=200  failed=200  recv_ratio=0.20  entities=199  online=199
```

Every client had failed mid-run, so they received almost nothing — and
**bytes-not-received are indistinguishable from bytes-not-sent**. The instrument
reported exactly what it measured; it simply was not measuring what the label
claimed. The 150-player level also ran against a container that was not empty
(200 entities for 150 players), which the clean-container precondition missed
because it checked `gameserver_entities` alone and not `gameserver_players_online`.

This is recorded here, prominently, because of the direction it points:

> **A run where every client failed can look like a 97% bandwidth win.**

It was caught only because the number was implausibly *good*. Every other
instrument failure in this project was caught by someone noticing something
broken; this one had to be caught by someone distrusting something that
confirmed what they wanted to believe. A 97% saving would have passed review.

Two instrument bugs were found while producing this document, and they point
opposite ways. The first — `json.Unmarshal` hardcoded in the loadtest's snapshot
loop — silently discarded every Protobuf snapshot and *hid* the win. The second
*invented* one. **The invented one is far more dangerous, because nobody
re-checks a flattering number.**

### The join failures in §15 were the readiness check, not load

While validating the gate above, the intermittent single-client join failures
recorded as "not explained" in [§15](#15-additional-confounds-for-part-ii) were
reproduced and root-caused.

The sweep waited for `gameserver_entities == 0` on `/metrics` and treated that as
"server ready". But `GameServer/Program.cs` starts the metrics endpoint (~line
193) well before the TCP listener, so **a level could start while the game port
was not yet accepting**. The first client then hit `connection reset by peer`
during join. Measured at roughly **1 cold start in 4**; with a TCP probe of the
game port added before each level, **0 in 5**.

Worth stating as a general lesson, because the readiness check looked correct and
was checking a real signal from the right process: *a health signal on one port
says nothing about a different port in the same process.* Every level in this
document that lost exactly one client lost it this way, and the previous
write-up's guess — "join contention under a 20/s ramp on a loaded host" — was
wrong.

**This project already knew that, somewhere else.** The CD post-deploy healthcheck
in [`.github/workflows/cd.yml`](../../.github/workflows/cd.yml) probes
`/healthz` on the metrics port **and** does a raw TCP connect on the gateway and
game-server ports, with a comment explaining that the HTTP probe is a liveness
signal "unlike a bare TCP connect" — i.e. its author understood the two answer
different questions and did both.

So the failure was not missing knowledge; it was knowledge that did not travel
from the deploy pipeline to the benchmark tooling. **If you are writing a
readiness wait, copy the `probe`/`tcp` pair from `cd.yml` rather than inventing
one.** Rediscovering this cost roughly one client in four cold starts and a
paragraph of confidently wrong speculation in an earlier revision of this
document.

This also interacts with the new gate: those levels are now `INVALID` rather than
merely reporting `fail=1`, so the sweep script reruns them instead of recording a
level that ran at 149 players under a "150" label.

`backend/loadtest` now rejects such a run rather than reporting it: a level with
any client failure, a snapshots-received ratio more than 5% off 1.0, or
server-side entity/player counts above what was requested is marked `INVALID` and
excluded from every aggregate — it is not a worse result, it is not a result. See
`Verdict.Invalid` and `validityFailure` in `load/runner.go`.

### Why a ratio of 1.40 walked past an existing check on that exact ratio

`snapshots_received_ratio` was already being checked when the 97% run happened.
It still got through, and the reason is worth keeping:

| Check | Asks | Fires when |
|---|---|---|
| `NoFrameLoss` (pre-existing) | did clients receive **less** than the server sent? | ratio &lt; 0.95 |
| `validityFailure` (new) | did clients receive **more** than the server sent? | ratio &gt; 1.05 |

Two different questions on one number, in opposite directions. The first is a
performance question — the server's bounded 64-deep send channel uses
`DropOldest`, so a client the writer cannot keep up with loses frames silently,
and that is a real result about capacity. The second is a validity question: a
client cannot receive more than was sent, so a ratio above 1 means the two
measurement windows describe different populations and *nothing* on either side
can be trusted.

A check that only looks one way down a two-sided number leaves the other half
unguarded, and the unguarded half here was the one that produced a flattering
result. When adding a bound to a ratio, ask whether the opposite bound is also
meaningful — and if it is, whether it means something so different that it needs
its own verdict rather than a wider band on the existing one.

### The ceiling criterion was the problem, and p99 was not the culprit I named

§16 above concluded that tick p99 is "noisier run-to-run than the mean" and that
ceilings should therefore be quoted loosely. **The direction of that reasoning was
wrong, and the corrected version is more useful.**

Repeated at a fixed 200-player level, six runs on a quiet machine against two
disturbed by a CD deploy sharing the host:

| statistic | 6 runs, quiet | spread | disturbed runs |
|---|---|--:|---|
| tick p99 | 67.41 – 70.84ms (median 69.48) | **5.1%** | 224.7, 240.6ms — **3.3×** |
| tick mean | 36.51 – 38.68ms (median 37.81) | **5.9%** | 60.6, 65.6ms — **1.7×** |
| **KB/s per client** | 243.7 – 244.3 | **0.3%** | 205.6, 210.7 |

Three things follow, and the second corrects an earlier claim in this document.

**p99 is not intrinsically noisy — it is contention-sensitive.** Quiet, it holds
a 5.1% spread; disturbed, it moves 3.3×. The load generator shares this machine
with the self-hosted deploy runner, so "noise" here was mostly one identifiable
process.

**But p99 is not meaningfully stabler *or* less stable than the mean when the box
is quiet** — 5.1% against 5.9% is a wash. An earlier revision of this section
claimed p99 was *tighter* than the mean (0.6% vs 2.2%); that was computed from
**two** runs and did not survive six. The mean's real advantage is narrower than
stated: it shows up only under contention (1.7× versus 3.3×), not in general.

**Bandwidth is an order of magnitude more reproducible than either**, at 0.3%
across the same six runs — and it barely moved even in the disturbed runs. This
is the empirical case for judging bandwidth-motivated work on bandwidth.

Two consequences:

1. **It flips which reading was the anomaly.** 53.46ms was the outlier and
   72.47ms the reproducible value, so `new-json` at 200 players genuinely fails
   the budget. The withdrawal in §16 stands; the reasoning underneath it did not.
2. **More repeats is the wrong fix.** Under "passes only if it passed every one of
   N runs", with 2 runs in 6 disturbed, the chance all N are clean is (4/6)^N —
   **0.44 at N=2, 0.30 at N=3, 0.20 at N=4**. Unanimity at N=3 would call a
   genuinely-passing level marginal ~70% of the time, and raising N makes it
   worse. N is no defence against an outlier process.

**What was adopted** (`backend/loadtest`, and ADR-7 amended to match): don't sweep
during a deploy (the sweep script refuses); decide a level on the **median** p99
across its runs; report the min..max bracket and name any level that straddles
the budget; record host load per run as evidence.

### A quiet box was not obtainable, and that is itself the finding

The intent was to pair the contended measurements above with a clean set. It was
not achievable on this host. With 200 virtual players the **load generator alone
consumes ~261% of a core** against the game server's ~120%, and the machine also
carries the dev stack, a k3s/Agones control plane and other agents; 1-minute load
average sat at 13.9–19.5 on 12 cores throughout, and p99 at the same level read
266–410ms rather than the 72.9–74.6ms measured earlier the same day.

`host.load_avg_1` is what made that visible. Its first use in anger was to stop
its own author from publishing 410ms as a capacity figure — which is the argument
for recording environment state alongside every measurement rather than trusting
that the box was quiet.

The practical consequence: **absolute tick figures from this host are not
trustworthy at any N**, because the disturbance is not a rare outlier but the
ambient condition. The criterion changes above are still correct and still
necessary; they bound the damage rather than remove it. A capacity number anyone
plans around needs the generator on separate hardware from the server under test
— already ADR-7 item 6, and now with a measured reason rather than a suspicion.

This does not touch the bandwidth results. Those reproduced to 0.4% across sweeps
and builds precisely because bytes on the wire do not care how busy the host is.

### The validity gate errs pessimistic, and that is the decision

The gate catches runs where the *instrument* broke. It does **not** catch runs
where the *host* was stolen: a sweep interrupted by a deploy returns
`players_failed=0`, a received ratio of 1.005 and sane entity counts, and is
recorded as merely `degraded` — as though the server could not keep up, when the
machine was busy elsewhere. That **understates** capacity.

This gap is accepted, not overlooked, and the reasoning is the point:

| | fails toward | how it fails |
|---|---|---|
| Gate as built | pessimistic — a level looks worse than it is | **visibly**: the level is reported, and a human can question a number that looks wrong |
| Tick-rate rule (rejected) | optimistic — real ceilings look like environment faults | **invisibly**: the level is `INVALID` and therefore *absent*, which reads as a sweep that did not go that high |

An absent level is the same failure shape as an integration suite that compiled
zero tests, or a restore script reporting success: the output is not false, it is
*missing*, and nobody reads missing as an error. A pessimistic number a human can
argue with is strictly preferable to an optimistic one nobody can see.

Contention is therefore handled where it can actually be observed — externally,
by refusing to sweep during a deploy, and by recording host load with every
result — rather than inferred from metrics that cannot distinguish it.

> **`host.load_avg_1`'s first use in anger was stopping its own author from
> publishing 410ms as a capacity figure.** That is the case for record-don't-judge
> in one sentence: a field that only ever describes the environment, and never
> votes on the verdict, still caught the thing a verdict would have missed.

**And one check that was tried and rejected.** Achieved tick rate looked like a
clean discriminator — 14.66–14.71/s healthy, 12.87–13.48 disturbed, 3.93 when the
host was overwhelmed — so "ticks/s well below the configured rate ⇒ INVALID"
seemed principled. It is wrong: a genuinely saturated server loses ticks the same
way, measured at **10.46 ticks/s at 300 players** and **12.51 at 400**, both real
capacity limits. That rule would have classified genuine ceilings as environment
faults and made them vanish from the results — an error in the *optimistic*
direction. **The tool cannot distinguish host contention from real saturation
using its own metrics**, so it records the host state and leaves the judgement to
a human.

---

# Part III — Entity type as an enum (2026-08-07)

> **Headline: a further ~15% off downstream, consistently across every level,
> from replacing one string field with an enum. The mobile bandwidth ceiling
> moves from ~92 to ~109 players.**

Raw data: [`backend/loadtest/results/entity-type-enum/`](../loadtest/results/entity-type-enum/).

## 17. Why the type string was worth 15% of the payload

Measured marginal cost of one `EntitySnapshot` in a 50-entity protobuf snapshot
is **41.2 bytes**, of which:

| component | bytes | share |
|---|--:|--:|
| `id` string | 17.0 | 41% |
| `type` string | 8.0 | 19% |
| numeric fields + framing | 16.2 | 39% |

`"player"` costs 8 bytes on the wire — a tag, a length, and six characters — for
a value drawn from a set of two. The enum costs 2.

## 18. Measured

Same protocol as Part II, judged on bandwidth per
[ADR-7's revised guidance](ARCHITECTURE-DECISIONS.md#adr-7--ccu-and-cost-figures-are-unbenchmarked-estimates):
bandwidth is the binding constraint and reproduces to 0.3% across runs, where
tick figures from this host are a lower bound distorted by the co-located
generator.

| Players | protobuf | + type enum | saved |
|--:|--:|--:|--:|
| 50 | 26.8 | 23.0 | 14.3% |
| 100 | 54.4 | 45.6 | 16.1% |
| 150 | 81.4 | 68.9 | 15.4% |
| 200 | 108.8 | 91.8 | 15.6% |

Consistent at ~15% across a 4× range of player counts, which is what a per-byte
saving should look like. A unit test predicted 15.0% from the schema alone; the
measurement came in at 15.4%, so the model of where the bytes go is now correct —
that model was what was missing when the earlier case rested on a "~40%" figure
that turned out to be 61%.

All four levels passed the validity gate on the first attempt, with the readiness
probe from §16 in place.

## 19. Cumulative, and the ceiling that matters

At 150 players in the worst-case dense-crowd shape:

| encoding | KB/s per client | vs JSON |
|---|--:|--:|
| JSON | 181.5 | — |
| Protobuf | 81.4 | 55% |
| Protobuf + type enum | **68.9** | **62%** |

Against ADR-7's `< 50 KB/s per client` mobile threshold:

| | bandwidth ceiling |
|---|--:|
| JSON | ~41 players |
| Protobuf | ~92 players |
| Protobuf + type enum | **~109 players** |

**Bandwidth is still the binding constraint.** ~109 players is better than ~41,
but it is still well below the tick ceiling, and the gap has to close from the
bandwidth side. The remaining lever on the wire itself is the `id` string at 17.0
bytes per entity — 41% of a packed entity and now the single largest term. That
is the interning follow-up, and unlike this change it is protocol *state* rather
than a re-encoding.

Tick figures were recorded and are not quoted here as acceptance evidence: per
§16 they are a lower bound from this host, and this change is not aimed at tick
time.

---

# Part IV — Entity-id interning (2026-08-07)

> **Headline: a further ~51% off downstream, and ADR-7's mobile bandwidth
> threshold now PASSES at 200 players — the first time any configuration has met
> it above ~41.**

Raw data: [`backend/loadtest/results/entity-id-interning/`](../loadtest/results/entity-id-interning/).

## 20. Measured

Same protocol, judged on bandwidth.

| Players | JSON | Protobuf | + type enum | **+ id interning** | vs enum | vs JSON |
|--:|--:|--:|--:|--:|--:|--:|
| 50 | 60.1 | 26.8 | 23.0 | **11.3** | 50.7% | **81.1%** |
| 100 | 120.1 | 54.4 | 45.6 | **22.4** | 50.9% | **81.3%** |
| 150 | 181.5 | 81.4 | 68.9 | **33.9** | 50.8% | **81.3%** |
| 200 | 242.1 | 108.8 | 91.8 | **45.9** | 50.0% | **81.0%** |

Flat at ~51% across a 4× player range, and **81% cumulative against the JSON
baseline** this work started from.

**`resyncs = 0` and `players_failed = 0` at every level.** The recovery path
exists and is tested, but nothing triggered it in ~40 minutes of load — the
handle tables stayed in agreement, which is what the keyframe reset is for. A
non-zero resync count here would have meant the two ends were disagreeing
routinely and the bandwidth figure was measuring a stream the client never
reconstructed.

## 21. The threshold, finally

ADR-7's acceptance criterion is `< 50 KB/s per client` on the mobile assumption.

| encoding | bandwidth ceiling |
|---|--:|
| JSON | ~41 players |
| Protobuf | ~92 |
| + type enum | ~109 |
| **+ id interning** | **> 200** (highest level swept is still under) |

At 200 players the measurement is 45.9 KB/s, inside the threshold. The ceiling is
no longer bracketed by this sweep — it is somewhere above 200 and would need
higher levels to find.

**This changes which constraint binds.** Bandwidth has been the limiting factor
throughout this work, at roughly a third of the tick ceiling. It is now the
looser of the two: the tick budget breaks before the bandwidth budget does. Any
further capacity work should target tick time, and the ranked list in §9 —
serialization off the tick, keyframe staggering, spatial-grid AOI — is where to
start.

**Caveat, unchanged and now more important:** tick figures from this host are a
lower bound because the load generator shares the machine with the server under
test. Now that tick is the binding constraint, the co-located-generator problem
(ADR-7 item 6) is on the critical path rather than a footnote.

## 22. Why the saving is ~51% rather than the ~41% the byte budget implied

The `id` string is 17.0 of a 41.2-byte packed entity — 41%. The measured saving
is higher because interning removes the id from *every* mention after the first,
while the byte budget counted a single entity in isolation. In a delta stream
most mentions are repeats, so the amortised saving exceeds the per-message share.

The remaining terms are the numeric fields and framing, which are already close
to minimal: position as two floats, hp/max_hp as varints, a handle, and (since
field 9) speed as a third float. Further
wire savings would need to change *what* is sent — delta-encoding positions
against the previous tick, dropping `max_hp` from every update when it rarely
changes, or tiering update rate by distance — not how it is encoded.

## 23. Snapshot allocation: what pooling and buffer reuse actually removed

Stage 4's breakdown left two allocation sources standing in the snapshot path —
`Encode` building a fresh `EntitySnapshot` per entity per viewer (134 699 B/tick
at 200 players) and `ToByteArray` allocating a new array per snapshot (44 280
B/tick). Both are now gone. This section is the measurement.

**Method — paired A/B inside one process.** Three arms run in the same binary
over identical prebuilt inputs, 60 ticks after a warm-up, counted with
`GC.GetAllocatedBytesForCurrentThread`. One binary, one run, so build, machine
and day cannot confound the comparison; the harness's own world-building is
hoisted out of the measured region so it is not charged to the arm that
allocates least. It lives in `GameServer.Tests/Snapshot/SnapshotAllocationTests.cs`
and runs with the suite, so the numbers can be reproduced with
`dotnet test --filter PooledPath_AllocatesFarLessPerTick`.

| viewers × 40 visible | legacy shape | pooled entities only | + reused buffers |
|--:|--:|--:|--:|
| 50 | 372 933 B/tick | 181 733 B/tick | **1 600 B/tick** |
| 200 | 1 491 733 B/tick | 726 933 B/tick | **6 400 B/tick** |

Byte-identical across three repeat runs. The residual is exactly **32 B per
viewer per tick**: one `ByteString` wrapper object per snapshot, which is what
`UnsafeByteOperations.UnsafeWrap` still allocates once the payload copy is gone.
The two changes are worth roughly half each — pooling alone leaves the
serialization arrays, buffer reuse alone leaves the per-entity objects.

**Read this before quoting the absolute numbers.** The harness is a bounded
reproduction, not the live server: AOI is pinned at 40 visible entities per
viewer and every viewer sends on every tick, whereas the real server's AOI varies
with position and the coalescing policy engages under load (§ stage 4 notes: at
200 players the write tasks already could not keep up with 15 Hz). What transfers
is the **ratio** and the **per-viewer residual**, not "1.49 MB/tick", which is a
property of the harness's fixed 40-entity AOI.

**No wall-clock claim is made, deliberately.** This host's run-to-run spread on
an *unchanged* binary is wide enough to swallow an effect this size — the
withdrawal in §16 is the precedent. Allocation is the claim; latency is not.
Less garbage should mean fewer gen-0 collections and so less tick jitter, but
that is a hypothesis this measurement does not test.

---

## Part V — the spatial index that lost (2026-08-14)

**Result: a uniform spatial grid was implemented, proved correct, measured, and
not merged.** It is slower than the brute-force scan everywhere the scan is
expensive. This entry exists so nobody builds it again on the strength of the
Big-O argument.

### What was measured

Paired in-process A/B: the same process builds a world, runs every viewer's AOI
query through the brute-force scan, then through the index, back to back. The
ratio *within* a run is the number that matters, because this host's absolute
timings swing by ±50% (see §8) while the paired ratio does not. Five runs:

| entities | spread | avg matches/query | brute | indexed | ratio (5 runs) |
|---|---|---|---|---|---|
| 200 | 1000x1000 (sparse) | 2.8 | 77–136 µs | 38–84 µs | 1.42–2.92x **faster** |
| 200 | 250x250 (realistic) | 23.0 | 483–638 µs | 1380–1514 µs | **0.32–0.45x — ~2.8x slower** |
| 400 | 250x250 (dense) | 42.9 | 782–1335 µs | 924–1606 µs | 0.81–0.89x slower |

The realistic-density ratio is 0.32–0.45 across five runs. That spread is far
tighter than the host noise, so the loss is real and reproducible, not an
artefact.

> **Quote the ratios, not the microseconds.** The harness that produced this
> table was never committed (`2e3e5db` carries `SpatialGrid`, `EcsWorld` and
> `AoiIndexDifferentialTests`, no benchmark), so the clock behind the µs columns
> cannot be traced — the one figure class in this document the #153 clock audit
> could not close. The ratio columns are unaffected: both arms run back to back in
> one process, so any skew is shared and cancels. The absolute µs are kept, not
> deleted, because the record of the measurement matters and because a 10-17%
> skew cannot touch a 2.8× result — but read them as order of magnitude. See
> [the audit](#audit-every-figure-in-this-document-against-its-clock).

### Why it lost — the premise was wrong

The case for an index was "40 000 distance tests per tick at 200 players, O(n²)".
The count is right. The cost is not: those tests are sequential reads over
contiguous chunk arrays, and 40 000 of them are worth microseconds. **The scan's
real cost is composing an `EntityState` for each match**, and that is
proportional to *matches* — a property of the game (how many players are near
you) rather than of the algorithm. No index reduces it, because the matches are
the answer.

The index then made composition *worse*. The scan composes from the chunk it is
already iterating; the index has only an entity handle, so it composes through
seven random-access component lookups per match. Add the per-query sort that
restores brute-force ordering (below) and the index pays more per match while
saving only the near-free part.

It wins in the sparse case for the same reason: at 2.8 matches per query there is
almost nothing to compose, so what is left *is* the distance tests. That is also
the case where the absolute cost is negligible — 77 µs to 38 µs on a 66 ms tick
budget, order 0.1%. (An order-of-magnitude reading, per the caveat above; a
10-17% clock skew moves it nowhere near mattering.) **The index helps only where
the cost does not matter.**

### The ordering constraint, which is a real cost

The delta encoder interns entity ids in AOI arrival order, so a change in
iteration order changes the bytes on the wire for an identical set. A grid
enumerates cell-major; the scan enumerates chunk-major. The first implementation
therefore produced the correct set in the wrong order, and the differential test
caught it on the first run. Fixing it means carrying each entity's scan ordinal
through the index and re-sorting each query's matches back — correct, and a cost
the Big-O argument never accounted for. Disabling that sort as a diagnostic did
not rescue the result (realistic density still 0.53x).

### What would have to be true to revisit

- Populations where AOI sets are genuinely small while entity counts are large —
  a much bigger map, or many more entities than viewers. The sparse row is that
  regime, and it is 1.4–2.9x faster there.
- A composition path the index can use as cheaply as the scan does, i.e. matches
  grouped by chunk rather than by cell. That is a different data structure, not a
  tuned version of this one.
- A different bottleneck. If `EntityState` composition were pooled or removed
  (Part IV's direction), the distance tests would become the dominant term and the
  index's argument would come back.

The implementation and its differential test are on `feat/aoi-spatial-index` at
`2e3e5db`, reverted by the following commit. It is correct and covered; it is
simply not worth running.

### What this leaves, now that §23 has landed

This section originally closed by pointing at `EntityState` composition as the
term measured dominant twice. **Half of it has since been removed**: §23's pooling
and buffer reuse took the encode path from 1 491 733 to 6 400 B/tick at 200
viewers, which is the per-viewer-per-tick object churn inside `Encode`.

What remains is the *other* half, which pooling does not touch: **the compose per
match inside the AOI scan itself**. Every entity that passes the distance test is
materialised into an `EntityState` — once per viewer, per tick — and that is the
cost this Part measured as dominant and the index failed to reduce. It is a
narrower and better-defined target than "stop materialising a struct per visible
entity": the encode side is done, the scan side is not.

On the evidence of this Part, and of §9 item 6 before it, that target should be
**measured before it is built**. Four changes in this sequence were commissioned
against a term that turned out not to be the expensive one.

---

## Part VI — multi-rate simulation: does replication follow the simulation rate? (2026-08-15)

The multi-rate scheduler (ADR-13) raises the simulation rate 4× by default, from a
single 15Hz loop to a 60Hz base tick with world systems every 4th tick. The design
claims replication does **not** follow it: snapshots still ship at the world rate, so
downstream bandwidth per client should be unchanged.

That claim is the one worth measuring here, and it is also the one this host can
measure honestly. Tick timings from this box are a lower bound of unknown tightness —
the load generator shares the CPU with the server under test (ADR-7) — but **bytes on
the wire do not care what else the host is doing**, and Part I established that
bandwidth reproduces here to 0.3%.

### Method

Two configurations, same binary, same load, two runs each. `SIM_CRITICAL_HZ` is the
only thing that changes between them:

```
A: SIM_CRITICAL_HZ=15 SIM_WORLD_HZ=15 SIM_BACKGROUND_HZ=5   (= the pre-change server)
B: SIM_CRITICAL_HZ=60 SIM_WORLD_HZ=15 SIM_BACKGROUND_HZ=5   (= the new default)

loadtest -join direct -players 50 -duration 45s -warmup 5s -encoding proto
GAMESERVER_ENEMIES=false   (so the measurement is the player path, not wave timing)
```

`-join direct` bypasses the gateway: the gateway is not in the gameplay data path
(ADR-3), and including it would only add join-time noise to a steady-state measurement.

### Results

| | A: 15/15/5 | B: 60/15/5 | change |
|---|---|---|---|
| Achieved base rate (ticks/s) | 15.02, 15.02 | 60.03, 59.99 | **4×, as configured** |
| **Downstream per client (B/s)** | **8068, 8000** | **8145, 8146** | **+1.4%** |
| Upstream per client (B/s) | 126, 126 | 126, 126 | none |
| Snapshot interval p50 | 66.9ms, 67.0ms | 66.9ms, 66.9ms | none |
| Snapshot interval p99 | 73.4ms, 73.6ms | 72.5ms, 73.3ms | none |
| Input→ack p50 | 35.6ms, 35.6ms | 33.5ms, 34.8ms | −1.5ms |
| Input→ack p99 | 68.1ms, 69.0ms | 68.8ms, 69.2ms | none |
| Base tick duration p99 | 0.49ms, 0.49ms | 0.49ms, 0.50ms | none |
| Base tick duration mean | 0.06ms | 0.03ms | halved |
| Ticks over budget | 0% | 0% | none |

### What this shows

1. **Replication did not follow the simulation rate.** Simulation runs 4× more often and
   downstream bandwidth moved by **1.4%**, against a run-to-run spread of 0.85% in
   configuration A. The snapshot interval is unchanged at 66.9ms p50 — still 15 sends a
   second. Had the two been coupled, this row would read ~32 KB/s per client and would
   have blown ADR-7's `< 50 KB/s` mobile budget at a fraction of 200 players.

2. **The 1.4% is real and explainable, not noise.** Movement is now continuous: a player
   holding a direction moves on every base tick instead of only on packet arrival, so
   marginally more entities have changed state when each snapshot is built, and the delta
   encoder sends them. It is the cost of the movement model, not of the send rate.

3. **The base tick keeps its schedule.** 60.03 and 59.99 ticks/s, not 62.5 — which is what
   the previous `1000 / rate` integer-millisecond sleep would have produced at 60Hz
   (`1000/60 = 16ms`, a 4% fast clock). The deadline scheduling in ADR-13 decision 3 is
   what closes that gap, and this row is the evidence it works.

4. **Mean tick cost halved** (0.06ms → 0.03ms) because three base ticks in four skip the
   world group and the entire snapshot phase. Per *second* the server does more work, as
   it must — the point is that the extra work is only the critical group.

5. **Input acknowledgement improved slightly**, p50 35.6ms → 34.2ms averaged. Directionally
   what a 4× input rate should do, but 1.5ms is close enough to the noise floor that it is
   reported rather than claimed.

### What this does NOT show

- **No capacity claim.** 50 players on a host shared with the load generator says nothing
  about the ceiling, which remains unmeasurable here (ADR-7). Multi-rate is a scheduling
  change, not a throughput change, and no throughput improvement is asserted.
- **Both levels are marked INVALID by the harness**, because all 50 clients report
  `run: read: server closed the connection` at teardown. Joins, the 45s measurement window
  and the snapshot reconciliation (99.97–100.26% of enqueued snapshots received) are all
  clean, so the per-run numbers above stand; but the harness's own verdict is withheld and
  is reported as withheld rather than being talked past.
- **The harness's verdict thresholds are not rate-aware.** It prints `tick budget 66.67ms
  @ 15Hz` regardless of the configured rate, so at a 60Hz base it is comparing against a
  budget four times too generous. It did not affect these runs — nothing came close to
  either budget — but it must be fixed before the harness is used to qualify a 60Hz
  configuration.

---

## Part VII — trimmed AOI compose and int-keyed delta state (2026-08-27, issue #237)

**Result: both changes shipped.** The AOI gather now composes a 7-field
`EntityView` (exactly what the snapshot encoder consumes, plus a world-stable
integer key) instead of the full 11-field `EntityState`, dropping the `Combat`
and `InputCursor` span fetches per chunk; and `SnapshotDeltaState` keys
`_lastSent`/`_seen`/`_handles` on that integer key instead of hashing the
entity-id string 2–3 times per visible entity per viewer per tick. Wire output
is **byte-identical** — proved frame-for-frame by
`TrimmedGatherByteIdentityTests` (old pipeline vs new pipeline over a 120-tick
scenario with keyframes, deltas, despawns and a same-id respawn, both Protobuf
and JSON), and the pre-change pinned digests in `SnapshotByteIdentityTests`
still pass untouched.

Paired in-process A/B (`GameServer.Tests/Bench/AoiComposeBench.cs`,
`BENCH_TICK=1`): 200 entities, 200 viewers, radius 50, mean AOI occupancy 15.3,
arms interleaved within each of 600 rounds, four independent runs. Medians are
microseconds per 200-viewer pass; per Part V's discipline only the within-run
ratios are quotable:

| pair | old median | new median | ratio (4 runs) |
|---|---|---|---|
| gather: full compose → trimmed | 598–630 µs | 526–576 µs | **1.07–1.17× faster** |
| encode: string-keyed → int-keyed | 1059–1186 µs | 998–1023 µs | **1.05–1.16× faster** |

Modest but real and consistently positive in every run, on the two phases that
matter: the gather runs on the tick thread (77–83% of a 200-viewer tick), the
encode on the write pool that shares the same 2–4 vCPU budget. The string-keyed
arm is a verbatim replica of the replaced encoder kept inside the bench file,
so the A arm is the code that actually ran.

**Part V's caveat now applies**: compose got cheaper, so the spatial index's
losing margin has narrowed. Re-measure the index only against these numbers,
not the pre-#237 ones — and only if AOI cost resurfaces as a bound.

## Part VIII — allocated bytes on the tick path (2026-09-04)

**Result: the steady-state tick thread allocates zero bytes per tick, and the
harness that proves it is now committed.** One real per-tick allocation was
found and removed — 72 B per parallel-gather dispatch, in the configuration
that exists to protect the tick budget — and the write path's remaining
32 B/frame is identified, attributed, and deliberately kept. Full test suite
after the change: 892 passed / 20 skipped / 0 failed.

**Why bytes are the metric.** Every duration measured on this host is a lower
bound of unknown tightness — tick p99 swings 3.3× with co-tenant load (ADR-7,
Part VI) — so a timing benchmark cannot prove an allocation fix did anything.
Allocated bytes are deterministic: `GC.GetAllocatedBytesForCurrentThread()`
counts exactly what the measuring thread allocated, and the same code over the
same world allocates the same bytes on a quiet box and a thrashing one. This
is the same reasoning that made bandwidth the quotable number in Parts II–IV.
The module's `CLAUDE.md` has required "no allocations in hot paths" since the
beginning; this is the first committed instrument for the rule.

**The harness** (`GameServer.Tests/Bench/TickAllocationBench.cs`, gated on
`BENCH_TICK=1` like the other benches, never runs in CI):

```
BENCH_TICK=1 dotnet test -c Release --filter FullyQualifiedName~TickAllocationBench \
  --logger "console;verbosity=detailed"
```

Same rig and entry points as `TickBreakdownBench` (50/200/500 viewers on the
disc placement, 15 Hz uniform, AOI radius 50), with two differences: every
fourth viewer also sends an attack input per tick, so the combat branch — the
path #249's interned rejection constant lives on — is measured rather than
assumed; and each arm is wrapped in a per-iteration allocation delta, so the
report separates mean bytes, zero-allocation iteration count, and the largest
single iteration. `BENCH_ALLOC_WARMUP=<ticks>` overrides the warm-up, which is
the tool that separates ramp allocation from steady-state allocation.

**Steady state (warm-up 10 000 ticks, 2 000 measured iterations per arm):**

| phase | 50 viewers | 200 viewers | 500 viewers |
|---|---|---|---|
| TickOnce (whole) | 8.6 B/tick | 6.2 B/tick | 6.2 B/tick |
| AOI gather (serial) | 0 | 0 | 0 |
| Input drain | 0 | 0 | 0 |
| Input apply (movement + combat, write scope) | 0 | 0 | 0 |
| Enemy AI (RunDue) | 8.6 B/tick | 6.2 B/tick | 6.2 B/tick |
| ApplyStructuralChanges | 0 | 0 | 0 |
| ConnectionManager.CopyTo | 0 | 0 | 0 |
| Encode+frame, run inline (write-task path) | 32 B/frame | 32 B/frame | 32 B/frame |

The whole-tick residue is entirely the enemy AI's wave spawn: 96–144 B on
exactly 134 of 2 000 iterations — one per wave, every 22.5 ticks at 15 Hz —
and zero on the other 1 866. That is the two `enemy-N` id strings a spawn
mints, which is content creation, not tick overhead: a new entity needs a new
identity, and the cost is event-driven, bounded by the wave cadence, and
independent of viewer count.

**Ramp vs steady state.** With the default 600-tick warm-up the whole-tick arm
reads 1 209 B/tick at 200 viewers (max 33 048), all of it in spikes clustered
where the enemy population and player drift push AOI occupancy past each
connection's high-water mark: the per-connection `EntityView[]` regrows (with
the 25 % headroom policy from #249) and never shrinks, so the cost decays to
the zero above as high-waters are reached. That is ramp allocation — paid
during population growth, exactly when #249's headroom already bounds the
re-scans — not a steady-state leak. The two runs are distinguishable only
because the harness lets the warm-up be varied; a single-number benchmark
would have reported either figure as "the" allocation rate.

**The fix: 72 B → 0 B per parallel region.** In the parallel-gather
configuration (`--gather-workers`, engaged at 500+ viewers), every
`ReadAllParallel` — once per broadcast tick — allocated 72 B on the tick
thread, measured by the `ParallelRegionAllocationMicro` arm and bisected to
two sources:

1. **40 B** — `new Exception?[workerCount]` per region for worker-failure
   cells. Now one array per world, allocated at slot capacity and cleared over
   the slots a region uses. Safe to share for the same reason the pool's own
   `_failures` field already is: regions are dispatched by one thread at a
   time.
2. **32 B** — a closure display class allocated by `EnsureStarted`'s loop *on
   the `continue` path*. The C# compiler allocates a closure's display class
   at the entry of the scope declaring the captured variable, not at the
   lambda expression — so the `new Thread(() => WorkerLoop(captured))` that
   runs once per world charged every dispatch that merely checked the slot
   was already started. Thread creation now lives in its own method
   (`StartWorker`), entered only when a thread genuinely starts. The XML doc
   on that method says why it must stay one.

`UpdateComponentsParallel` had both allocations too and is fixed by the same
two changes. After: 0 B/region on both, 2 000 regions per arm. Wire output is
untouched by construction — nothing here is on an encode path — and the
byte-identity suites (`SnapshotByteIdentityTests`,
`TrimmedGatherByteIdentityTests`), the golden vectors, and the full suite all
pass unchanged.

**The remainder that is kept: 32 B/frame on the write path.** The
`EncodeFramePathMicro` arm attributes it exactly: `SnapshotDeltaState.Encode`
is 0 B/call in every mode (delta-unchanged, delta-all-changed, keyframe — the
pools from the stage-4 work hold), and `SnapshotFrameWriter.WriteFrame` is a
flat 32 B/call: the `ByteString` wrapper `UnsafeByteOperations.UnsafeWrap`
creates so the envelope can reference the payload buffer without copying it.
Removing it means hand-writing the envelope's tags and varints, which
`SnapshotFrameWriter`'s own doc rejects as the variant of this optimisation
that puts the wire at risk — and the stake is small: at 200 players × 15 Hz
the write pool produces ~94 KB/s of gen-0 garbage from this, against the
44 280 B/tick (~650 KB/s) the frame writer already removed. The keyframe
figures in the aggregate arm read higher (43–176 B/frame) only while world
density is still shifting — per-connection dictionary growth chasing rising
AOI occupancy — and are ramp, not floor, by the same warm-up test as above.

**Caveats.** The counter is thread-local, so work production runs on other
threads (write tasks, the death drain, Redis publish) is seen only where an
arm runs it inline deliberately — the encode arm exists for exactly that
reason. `metrics` is null, matching `TickBreakdownBench`; `GameMetrics`
records through pre-built `TagList`s and is designed alloc-free, but that is
asserted, not measured here. The JSON snapshot path is not measured — it is
not the production encoding and keeps its allocating serializer by documented
choice. And per Part V's rule: these are allocation figures, not time — they
say nothing about the tick ceiling, which remains blocked on ADR-7.
