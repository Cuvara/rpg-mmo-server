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

## Metric reference (scraped names)

| Metric | Type | Labels | Meaning |
|--------|------|--------|---------|
| `gameserver_tick_duration_seconds` | histogram | `map_id` | Wall time of one simulation tick. Buckets sized for the 66 ms @15 Hz budget |
| `gameserver_tick_processed_inputs_total` | counter | `map_id` | Inputs applied by the tick loop |
| `gameserver_players_online` | gauge | `map_id` | Connected players |
| `gameserver_entities` | gauge | — | Entities in the world |
| `gameserver_snapshots_sent_total` | counter | `map_id` | Snapshot messages sent |
| `gameserver_player_saves_total` | counter | `status=ok\|error` | Persistence results from the async saver |
| `gameserver_events_published_total` | counter | `type` | Cross-server events published |
| `gameserver_resyncs_total` | counter | `map_id` | Keyframes **requested by a client** — see below |

Useful queries:

```promql
histogram_quantile(0.99, rate(gameserver_tick_duration_seconds_bucket[5m]))  # tick p99
rate(gameserver_player_saves_total{status="error"}[5m])                      # save error rate
sum(gameserver_players_online)                                               # CCU
rate(gameserver_resyncs_total[5m])                                           # interning health
```

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
