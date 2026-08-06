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

Useful queries:

```promql
histogram_quantile(0.99, rate(gameserver_tick_duration_seconds_bucket[5m]))  # tick p99
rate(gameserver_player_saves_total{status="error"}[5m])                      # save error rate
sum(gameserver_players_online)                                               # CCU
```

## Testing

`GameServer.Tests/Observability/GameMetricsTests.cs` collects points through the
OpenTelemetry SDK's in-memory reader — no HTTP server involved.
