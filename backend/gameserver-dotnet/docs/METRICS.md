# GameServer Metrics

Prometheus-compatible metrics via OpenTelemetry .NET (`System.Diagnostics.Metrics`
Meter `rpg.gameserver` + `OpenTelemetry.Exporter.Prometheus.HttpListener`).

## Endpoint

- Address: `--metrics-addr <addr>` or env `METRICS_ADDR`. Default `:9101`.
  Explicitly empty (`METRICS_ADDR=`) disables the endpoint.
- Paths: `/metrics` (Prometheus exposition), `/healthz` (200 `ok`).
- Runs on background threads; never touches the tick thread.
- Windows dev note: binding the `+` wildcard needs an admin URL ACL, so the
  endpoint automatically falls back to `http://localhost:<port>/`. Linux (the
  production target) binds all interfaces directly.
- Wildcard binding (`:9101`, `0.0.0.0:9101`, `*:9101`) resolves to the HttpListener
  prefix `http://+:<port>/`, which answers **any** `Host` header — scraping by IP
  works. A named address (`gameserver-dotnet:9101`) registers a prefix for that name
  only and answers nothing else, so Prometheus must then scrape it under exactly
  that name.
  <br>
  Wildcards were broken until 2026-08-06: OpenTelemetry builds its listener prefix
  through `UriBuilder`, which rejects `+`/`*` with
  `UriFormatException: Invalid URI: The hostname could not be parsed` — thrown in the
  `PrometheusHttpListener` constructor, so the whole endpoint failed to start on
  Linux with the default `METRICS_ADDR=:9101` (Windows hid it by falling back to
  `localhost`). The exporter is now handed a `UriBuilder`-safe placeholder host and
  the real wildcard prefix is set on the listener via `ConfigureHttpListener`, which
  runs before `Start()`. Covered by `MetricsEndpointTests`.

## `/status` — the JSON snapshot an operator and the sample client read

`GET /status` on the same listener returns a small JSON object aggregating live
server state. It is not Prometheus exposition and is not scraped; it exists for
human inspection and for the Unity DOTS sample, which polls it.

```json
{
  "ok": true,
  "tick_rate": 60,
  "achieved_tick_hz": 59.97,
  "sim_critical_hz": 60,
  "sim_world_hz": 15,
  "sim_background_hz": 5,
  "current_tick": 726335,
  "players_online": 12,
  "capacity": 100,
  "entities": 34,
  "enemies_alive": 22,
  "attacks_received": 4210,
  "attacks_unresolved": 12,
  "attacks_rejected": 3980,
  "attacks_accepted": 218,
  "attack_kills": 61,
  "last_attack_rejection": "target out of range",
  "redis": "connected",
  "event_stream": "redis",
  "events_dropped": 0,
  "event_publish_failures": 0,
  "postgres": "connected",
  "uptime_seconds": 12105
}
```

**Every rate field names its group.** The server runs three simulation groups at
three frequencies (ADR-13), so an unqualified "tick rate" is a question with three
answers rather than a fact, and whichever one is printed alone, some reader is
wrong by a factor. That is not hypothetical: until #144 this endpoint published the
legacy `--tick-rate` / `GAMESERVER_TICK_RATE` scalar, which **no deployment sets**,
so it reported the compiled-in default of 15 on servers whose prediction rate was
60 — a value that looked like a fact and was a stale default.

| Field | Meaning |
|-------|---------|
| `tick_rate` | The rate movement is integrated at and the tick counter advances at — the **critical** group. **Defined to be the same number as the wire field `join_token_resp.tick_rate`** (normative definition in `API.md`); both read `SimulationRates.MovementHz`, and `ServerStatusRatesTests` fails if either grows its own source. Kept under this name because clients already read it under this name from both surfaces |
| `achieved_tick_hz` | The rate the base timeline is **actually** advancing at, measured by the server over a 2s sliding window on the monotonic clock. Compare against `sim_critical_hz`: a healthy server has them equal to within rounding. **`0` means "not measured yet"** — no window has completed, i.e. the process is younger than ~2s; it does not mean the loop has stopped, and `current_tick` distinguishes those |
| `sim_critical_hz` | Critical-group Hz — input, movement, combat. Equal to `tick_rate`; published separately so a reader after "the critical rate" need not know that `tick_rate` happens to be it. `current_tick` counts these |
| `sim_world_hz` | World-group Hz — AI, spawning, **and the snapshot broadcast cadence**. This, not `tick_rate`, is what a client's interpolation buffer is sized against and what governs bandwidth per client |
| `sim_background_hz` | Background-group Hz |
| `capacity` | The admission limit (`GAMESERVER_CAPACITY`) this server enforces and publishes into the registry |
| `attacks_received` / `attacks_unresolved` / `attacks_rejected` / `attacks_accepted` / `attack_kills` | Attack-path counters since process start. Every input carrying an attack target lands in exactly one of *unresolved* (target id no longer resolves — despawned or bogus), *rejected* (refused by `CombatLogic.ValidateAttack`: range, cooldown, dead attacker or target), or *accepted* (dealt damage); `attack_kills` counts accepted attacks that killed. These exist because a rejected attack is dropped with a Debug-level log on servers running at Information — without the counters, a client attacking out of range is indistinguishable from a client not attacking at all, which is precisely the ambiguity that stalled a live zero-kills investigation |
| `last_attack_rejection` | Verbatim reason of the most recent rejection (e.g. `target out of range` — an interned constant since #249; the measured distance moved to the Debug-guarded rejection log), `null` until something is rejected. One string, most-recent-wins — a breadcrumb naming *why* attacks are being refused, not a log |
| `event_stream` | Which `IEventStream` backs cross-server events: `redis` (publishing into `events:game`, the stream the gateway relay consumes — ADR-5) or `noop` (`REDIS_ADDR` unset, or the connection could not be built at startup — events are discarded) |
| `events_dropped` / `event_publish_failures` | Loss counters of the Redis event stream, since process start — the same values as `gameserver_events_dropped_total` / `gameserver_events_publish_failures_total` above. Always `0` under `"event_stream": "noop"` |
| `uptime_seconds` | Seconds since process start on a **monotonic** clock (`Stopwatch`), not wall time — see below |

### Do not compute a rate — read `achieved_tick_hz`

With a configured rate, a tick counter and an uptime on one object and no measured
rate, the obvious move is:

```
achieved Hz  =  current_tick / uptime_seconds        # DON'T
```

That division used to mix two clocks: `current_tick` is advanced by a
`Stopwatch`-paced loop (`CLOCK_MONOTONIC`) and `uptime_seconds` came from
`DateTime.UtcNow` (`CLOCK_REALTIME`). On a host whose realtime clock runs 10-17%
fast — this one does, see #153 — the quotient reports a **healthy 60 Hz loop as
~54 Hz**. That is issue #147 in full: a defect filed against a server that did not
have one, propagated into an ADR and blamed for a client prediction defect, then
closed as not-a-defect. The loop was never wrong; the instrument was.

Two changes close that off, and both are needed:

1. **`uptime_seconds` is now monotonic** (`Stopwatch`, not `DateTime.UtcNow`), so
   the quotient no longer straddles two clocks. This *changes the meaning of a
   documented field*: it is elapsed process time, not a wall-clock difference, so
   it no longer tracks a clock step (NTP correction, suspend/resume) and can
   disagree with `date`-derived arithmetic on a drifting host. That disagreement is
   the point — an interval should never have come from a wall clock.
2. **`achieved_tick_hz` is published**, so nobody has to do the arithmetic at all.
   An observer that must supply a clock will eventually supply a bad one, and the
   result looks exactly like a server defect.

`achieved_tick_hz` is measured over a **2 second sliding window**, entirely from
`Stopwatch.GetTimestamp()`, sampled once per base tick inside the loop itself
(`AchievedRateMeter`). It is O(1) and allocation-free per tick, so it does not
perturb the budget it measures.

**Base timeline only, deliberately.** The world and background groups are exact
integer divisors of the base rate, so publishing three measured rates would be
publishing one measurement and two pieces of arithmetic — three things that can
drift instead of one. For a per-group measured rate use
`rate(gameserver_sim_group_runs_total[...])` on `/metrics`, which is measured
against Prometheus' own timestamps.

The same value is exported as the Prometheus gauge `gameserver_achieved_tick_hz`.

## Metric reference (scraped names)

| Metric | Type | Labels | Meaning |
|--------|------|--------|---------|
| `gameserver_tick_duration_seconds` | histogram | `map_id` | Wall time of one **base** tick. Explicit buckets 0.5 ms → 1 s, so they still bracket the 16.7 ms @60 Hz base budget as well as the 66 ms @15 Hz one |
| `gameserver_tick_processed_inputs_total` | counter | `map_id` | Inputs applied by the tick loop |
| `gameserver_sim_group_duration_seconds` | histogram | `map_id`, `group` | Wall time of one run of a simulation group — `group=critical\|world\|background` |
| `gameserver_sim_group_runs_total` | counter | `map_id`, `group` | Times a simulation group has run. The ratio between groups **is** the configured rate ratio |
| `gameserver_tick_overruns_total` | counter | `map_id` | Base ticks whose work exceeded the base period — see below |
| `gameserver_tick_backlog_dropped_total` | counter | `map_id` | Base ticks discarded because the loop fell too far behind the wall clock — see below |
| `gameserver_achieved_tick_hz` | gauge | `map_id` | **Measured** base-tick rate over a 2s window, from the monotonic clock. Compare with the configured `SIM_CRITICAL_HZ` — a healthy server has them equal. Never derived from wall time: a wall-clock rate on a host with a fast `CLOCK_REALTIME` reports a healthy loop as slow (#147/#153). `0` = not measured yet |
| `gameserver_players_online` | gauge | `map_id` | Connected players |
| `gameserver_entities` | gauge | — | Entities in the world |
| `gameserver_snapshots_sent_total` | counter | `map_id` | Snapshot messages sent |
| `gameserver_player_saves_total` | counter | `status=ok\|error` | Persistence results from the async saver |
| `gameserver_events_published_total` | counter | `type` | Cross-server events handed to the event stream by `EventPublisher`. With the Redis backend this counts hand-offs into the publish queue, not confirmed `XADD`s — subtract the two counters below for what actually reached the stream |
| `gameserver_events_dropped_total` | counter | — | Events dropped **oldest-first** because the Redis event stream's bounded publish queue (4096) was full — i.e. Redis was unreachable long enough to fill it — or the event was offered after shutdown. Zero forever on a healthy server; any non-zero rate means the gateway relay is missing events |
| `gameserver_events_publish_failures_total` | counter | — | Events dropped after exhausting the `XADD` retry budget (3 attempts with short backoff). Distinct from `dropped`: these reached the head of the queue and still could not be written. Sustained increments alongside a flat `dropped` means Redis is up but refusing writes (e.g. OOM under `noeviction`) |
| `gameserver_resyncs_total` | counter | `map_id` | Keyframes **requested by a client** — see below |

Useful queries:

```promql
histogram_quantile(0.99, rate(gameserver_tick_duration_seconds_bucket[5m]))  # tick p99
rate(gameserver_player_saves_total{status="error"}[5m])                      # save error rate
sum(gameserver_players_online)                                               # CCU
rate(gameserver_resyncs_total[5m])                                           # interning health
rate(gameserver_tick_overruns_total[5m])                                     # base rate sustainable?
rate(gameserver_tick_backlog_dropped_total[5m])                              # sim time vs real time
sum by (group) (rate(gameserver_sim_group_runs_total[5m]))                    # observed group Hz
```

### The multi-rate group metrics

The simulation runs three groups — `critical`, `world`, `background` — at
independently configured frequencies (`SIM_CRITICAL_HZ` / `SIM_WORLD_HZ` /
`SIM_BACKGROUND_HZ`, defaults 60/15/5; see `docs/README.md` and the scheduler
section of `docs/DESIGN.md`). Both group instruments carry a `group` label rather
than being three separately-named metrics: the groups are configuration, so a name
that encoded the group would have to change whenever a group did, and a dashboard
cannot sum across differently-named series.

**`gameserver_sim_group_runs_total` is the scheduler's self-check.** The groups run
on one integer tick timeline, so where two groups both have systems their run counts
are in exactly the configured rate ratio (at 60/15/5 that would be 12 : 3 : 1) over
any window long enough to smooth the edges. Drift in that ratio is a *scheduler*
defect, not a load symptom — load makes every group slower together, because they
run inside the same tick.

**Which series actually appear today.** The counter is incremented per group by
`SimulationSchedule.RunDue`, which skips a group with no registered systems
entirely. Every `IEcsSystem` in the current build declares
`SimulationGroup.World` (the three enemy-AI systems), so **`group="world"` is the
only series a stock server emits.** The critical group's work — input processing and
held-direction movement — runs directly in the tick-loop body rather than as a
declared system, so it is counted in `gameserver_tick_duration_seconds` and not
here; the background group ships empty by design. A `group="critical"` or
`group="background"` series appearing means systems were registered in those
groups, not that something broke.

**`gameserver_sim_group_duration_seconds` attributes tick cost.** It is what turns
a rising `gameserver_tick_duration_seconds` into an answer. A tick that overruns
while `group="world"` accounts for most of its duration is a world-simulation
problem, and lowering `SIM_WORLD_HZ` is a real remedy — it spreads that cost over
more base ticks without touching prediction latency. An overrun where the group
durations account for very little of the tick is the opposite finding: the cost is
in the loop body (input drain, held movement, snapshot gather), and the only lever
there is `SIM_CRITICAL_HZ` itself, because that work runs on every base tick by
construction.

> **Bucket caveat.** Only `gameserver_tick_duration_seconds` has an explicit-bucket
> view (`MetricsEndpoint.TickDurationBuckets`). The group histogram uses the SDK's
> default boundaries, which are sized for *milliseconds* while these values are
> recorded in *seconds* — so nearly every observation lands in the lowest bucket and
> `histogram_quantile` over it is not informative. Use `_sum` / `_count` for a mean
> per group until a view is added.

### `gameserver_tick_overruns_total` — the configured critical rate is too fast

Incremented once for every base tick whose work took longer than the base period
(`1 / SIM_CRITICAL_HZ` — 16.7 ms at the default 60 Hz). It is logged as a WARNING
only past twice the budget, so the counter sees overruns the log does not.

**Expected value: approximately zero.** A non-zero sustained rate means one thing:
**`SIM_CRITICAL_HZ` is not sustainable on this host at this load.** It is not a
transient, and it is not something more players will fix. The responses, in order
of directness:

1. **Lower `SIM_CRITICAL_HZ`** to the next divisor that still divides
   `SIM_WORLD_HZ` and `SIM_BACKGROUND_HZ` cleanly (30 works with 15/5; 45 does
   not — the server refuses to start rather than round). This is the honest fix
   when the host simply cannot do 60 Hz.
2. **Move work off the base tick.** Compare
   `gameserver_sim_group_duration_seconds{group="world"}` against the tick duration:
   if the world group accounts for the overrun, it is landing on those base ticks
   and a lower `SIM_WORLD_HZ` spreads it out without touching prediction latency.
3. **Suspect the host, not the server.** Tick timing on a box shared with a load
   generator or a deploy moves by more than 3× (`backend/docs/BENCHMARK.md`,
   ADR-7). An overrun rate measured on a contended host is a fact about the host.

A rising overrun rate with `gameserver_tick_backlog_dropped_total` still at zero is
the survivable state: individual ticks are late, the loop absorbs them by not
sleeping, and simulation time is still tracking real time.

### `gameserver_tick_backlog_dropped_total` — simulation time is behind real time

Incremented **by the number of base ticks dropped**, not by one, each time the loop
finds itself `MaxLagTicks` (8) or more base ticks behind the wall clock, gives up on
the backlog and resynchronises its deadline to now. Always accompanied by a WARNING.

**This is the serious one.** It means the server stopped simulating some interval of
time altogether. Every consequence is visible to players and none of them is
recoverable after the fact:

- **The world ran slower than the clock.** Cooldowns, spawn timers and every other
  tick-based timer (`docs/DESIGN.md`, "Tick-based timers") advanced less than the
  elapsed wall time. Two servers that dropped different amounts are no longer in
  the same time base.
- **Inputs in the dropped window were never applied.** Clients predicted them and
  will be reconciled backwards, which reads as rubber-banding.
- **The snapshot stream skipped.** Snapshots are gated to the world rate, so a
  dropped backlog removes whole world intervals from the delta stream.

The counter is deliberately a *drop* rather than a catch-up: running the missed
ticks back to back costs more than the budget it reclaims, so a server that falls
behind falls further behind and never recovers (the comment on `MaxLagTicks` in
`TickLoop.cs` states this as the reason). The design chooses a bounded, measurable
loss over an unbounded spiral — which is exactly why this counter must be alerted
on. Any value above zero deserves investigation; a sustained rate means the server
is not keeping up and needs either a lower `SIM_CRITICAL_HZ` or fewer players.

**Capacity figures measured while this is non-zero are invalid**, in the same way
and for the same reason as figures measured under a high resync rate: the server
was not doing the work the numbers claim to describe.

### `gameserver_entities` vs `gameserver_players_online` — disagreement is CORRECT

**These two gauges are expected to differ, and a difference is not a leak.** They
count different things: `players_online` counts live *connections*,
`gameserver_entities` counts *entities in the world*. On disconnect the
connection goes immediately but the entity is held for the reconnect grace
period, so `entities` stays above `players_online` for the length of that hold
and then converges.

Measured on the live local stack, 2026-08-12, sampling both gauges every 2 s
across two disconnects:

```
07:28:17  player_count=2  players_online=2  entities=2   both clients connected — all agree
07:28:42  player_count=1  players_online=1  entities=2   one disconnects; its entity is held
07:29:11  player_count=1  players_online=1  entities=1   hold expires 29 s later
07:30:47  player_count=0  players_online=0  entities=1   second disconnects
07:31:19  player_count=0  players_online=0  entities=0   hold expires 32 s later
```

Two things to take from it. While both clients were steadily connected the
gauges agreed *exactly*, in every sample — so a disagreement **while everyone is
connected** is a real signal, not this effect. And the divergence closed after
~29 s and ~32 s, matching the 30 s map-server hold, so the lag is bounded by a
known constant rather than open-ended.

What WOULD be a defect: `entities` staying above `players_online` well past the
hold (60 s+ on a map server) with no one connected, or the two disagreeing while
connection count is stable. Seeing `entities > 0` with `players_online = 0` for a
few tens of seconds after everyone leaves is the hold doing its job.

The registry's `player_count` field (Redis hash `servers:id:<id>`) tracks
`players_online`, not `entities` — it agreed with it in all 450 samples above.

### `gameserver_resyncs_total` — what a rising rate means

**Expected value: approximately zero.** A healthy fleet does not resync.

A client sends `MsgResync` only when it cannot reconstruct world state from the
delta stream. Since entity-id interning shipped, the overwhelmingly likely cause
is that a snapshot referenced an entity handle the client had no binding for —
**the server and that client disagree about the interning table.**

If this rate is non-zero and sustained, look here first:

1. **Interning is misbehaving.** Handles are per connection and reset at every
   keyframe; a client that keeps failing to resolve them is either losing
   snapshots or disagreeing about where the interval boundary is. See
   `shared/docs/DESIGN.md`.
2. **What it costs you.** Every resync forces a full keyframe, which is the most
   expensive snapshot the server sends. A fleet resyncing constantly is doing
   keyframe work at delta frequency — bandwidth and tick cost both rise, and the
   delta encoding is buying nothing.
3. **What it hides.** A high resync rate means clients are reconstructing far
   less state than the snapshot count suggests. Any capacity or bandwidth figure
   measured while this is elevated is describing a stream nobody successfully
   consumed. Treat such a measurement as invalid, not merely as worse.

**This counter deliberately excludes the periodic keyframe** (every N snapshots,
by design). Counting routine keyframes here would bury the signal under a
constant background rate, and the signal is the entire reason the counter exists.

The client-side counterpart is `resyncs` in `backend/loadtest`'s JSON result,
which counts the same event from the other end. The two should agree; if the
loadtest sees resyncs and the server does not, the requests are not arriving.

**There is deliberately no gateway-side equivalent.** The gateway handles only
`MsgAuth`, `MsgEnterWorld` and `MsgDisconnect` — `MsgResync` travels client to
game server directly, because the gateway is a redirector and not in the gameplay
data path ([ADR-3](../../docs/ARCHITECTURE-DECISIONS.md#adr-3--gateway-is-a-redirector-not-a-router)).
A gateway counter here would always read zero, which is worse than absent: a
permanently-zero series looks like a healthy signal rather than a missing one.

## Testing

`GameServer.Tests/Observability/GameMetricsTests.cs` collects points through the
OpenTelemetry SDK's in-memory reader — no HTTP server involved.
