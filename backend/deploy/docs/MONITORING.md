# Monitoring — dev observability stack

One container gives the whole backend a metrics UI:

```bash
cd backend/deploy
make monitoring-up          # docker compose --profile monitoring up -d
```

| URL | What |
|-----|------|
| http://localhost:3000 | Grafana (`admin` / `admin`, override with `GRAFANA_USER` / `GRAFANA_PASSWORD`) |
| http://localhost:9090/targets | Bundled Prometheus — scrape health |
| `localhost:4317` / `localhost:4318` | OTLP gRPC / HTTP ingest (traces, logs, metrics — for later) |

`make monitoring-down` stops it, `make monitoring-logs` tails it, `make monitoring-targets` prints target health from the CLI.

> **Port already in use?** Grafana's 3000 is a popular port (Cocos Creator, Node dev
> servers…). Set `GRAFANA_PORT=3001` in `backend/deploy/.env` and re-run
> `make monitoring-up`. Docker Desktop silently drops a host binding it cannot
> take, so a missing mapping in `docker ps` is the symptom.

---

## Why `grafana/otel-lgtm` and not a hand-rolled stack

The obvious build is `prometheus` + `grafana` services with our own provisioning
files, and that was the first attempt. It was dropped in favour of the official
`grafana/otel-lgtm` image because:

- **One container instead of two-plus.** Grafana, Prometheus, Loki, Tempo,
  Pyroscope and an OpenTelemetry Collector, already wired to each other
  (datasources provisioned, exemplars linked Prometheus→Tempo→Loki).
- **Nothing to maintain.** Datasource UIDs, versions and cross-links are the
  image's problem. We own exactly three small files (see below).
- **OTLP is already listening.** The moment the C# game server or the gateway
  emits traces or structured logs, `:4317`/`:4318` accept them — no second
  round of infrastructure work. A hand-rolled Prometheus would have needed
  Loki + Tempo + a collector bolted on later.
- **It is Grafana's own dev-stack image**, so the graduation path to Grafana
  Cloud (below) is the documented one.

Trade-off: it is a *dev* stack. Single binary set, single node, no HA, no
retention policy tuning. Production goes to Grafana Cloud or
kube-prometheus-stack — see the graduation paths at the bottom.

## What we own

Three files under `backend/deploy/monitoring/`, mounted into the image:

| File | Mounted at | Purpose |
|------|-----------|---------|
| `prometheus.yaml` | `/otel-lgtm/prometheus.yaml` | Scrape config (the documented override point of the image) |
| `grafana-dashboards.yaml` | `/otel-lgtm/grafana/conf/provisioning/dashboards/rpg-dashboards.yaml` | Extra dashboard provider, added *alongside* the image's own |
| `dashboards/rpg-gameplay.json` | `/otel-lgtm/dashboards-rpg/` | The "RPG Gameplay" dashboard |

Plus the `lgtm-data` volume on `/data` — every bundled component persists there,
so restarts keep the TSDB, Grafana state and Loki chunks.

### How extra scrape targets work

`grafana/otel-lgtm` documents two override points: mount your own
`/otel-lgtm/prometheus.yaml` (or `/otel-lgtm/otelcol-config.yaml`), or pass
`PROMETHEUS_EXTRA_ARGS` / `OTELCOL_EXTRA_ARGS`. We mount `prometheus.yaml` —
simplest working path: pull-based scraping of processes that already speak
Prometheus, with no collector pipeline in between.

The mount **replaces** the image's file, so `monitoring/prometheus.yaml` starts
with a verbatim copy of the image's `otlp:` and `storage:` blocks (OTLP push
keeps working) and only adds `scrape_configs`. When bumping
`OTEL_LGTM_VERSION`, re-copy those two blocks:

```bash
docker run --rm --entrypoint sh grafana/otel-lgtm:<tag> -c 'cat /otel-lgtm/prometheus.yaml'
```

Targets:

| Job | Target | Notes |
|-----|--------|-------|
| `nakama` | `nakama:9100` | compose service, `--metrics.prometheus_port 9100` |
| `gateway` | `host.docker.internal:9102` | host-run `go run ./cmd/gateway/` |
| `gameserver` | `host.docker.internal:9101` | host-run C# `dotnet run` |
| `gateway-container` | `gateway:9102` | only up with `--profile realtime` |
| `gameserver-container` | `gameserver-dotnet:9101` | only up with `--profile realtime-dotnet` |

`host.docker.internal` resolves inside the container because the service
declares `extra_hosts: host.docker.internal:host-gateway` (needed on a plain
Linux engine; Docker Desktop provides it anyway). Targets for processes that are
not running show as **DOWN** — that is normal in a partial dev stack, not a
misconfiguration.

## Metrics contract

### Gateway (Go, `backend/gateway/metrics`)

Exposed on a **separate listener** from the realtime port — `--metrics-addr`
(env `METRICS_ADDR`), default `:9102`; `off`/`none`/empty disables it.
`/metrics` is promhttp, `/healthz` returns 200 while the process lives
(liveness probe; use it for k8s `livenessProbe`).

| Metric | Type | Labels | Meaning |
|--------|------|--------|---------|
| `gateway_connections_active` | gauge | — | client sockets currently held |
| `gateway_auth_total` | counter | `result=ok\|fail` | MsgAuth outcomes |
| `gateway_enter_world_total` | counter | `result=ok\|fail` | MsgEnterWorld / map assignment outcomes |
| `gateway_allocations_total` | counter | `result=ok\|fail` | allocator (Agones) requests |
| `gateway_relay_events_total` | counter | — | cross-server events delivered by the relay |

Plus the standard `go_*` and `process_*` collectors. Both `result` label values
are pre-created at startup so `rate()` has a zero baseline instead of a series
that pops into existence on the first failure.

### Game server (C#, `:9101`)

Owned by the game server module. The dashboard consumes
`gameserver_tick_duration_seconds` (histogram), `gameserver_players_online`
(gauge) and `gameserver_save_errors_total` (counter). Panels stay empty until
that exporter is live.

### Nakama (`:9100`)

Built-in Prometheus endpoint; useful series are `nakama_api_count`,
`nakama_socket_count`, `nakama_db_*`.

## Dashboard — "RPG Gameplay"

Provisioned (uid `rpg-gameplay`), deliberately minimal — the five numbers that
tell you whether the game is healthy:

| Panel | Query | Read it as |
|-------|-------|-----------|
| Tick duration p99 | `histogram_quantile(0.99, sum by (le, map_id) (rate(gameserver_tick_duration_seconds_bucket[5m])))` | Must stay far below the tick budget (66ms at 15Hz). Crossing it = the sim is late, clients rubber-band. |
| Players online | `sum by (map_id) (gameserver_players_online)` | Load per map + total CCU. |
| Gateway connections active | `sum by (instance) (gateway_connections_active)` | Should track players online; a growing gap means sockets leak or clients stall before EnterWorld. |
| Auth / EnterWorld failure ratio | `rate(gateway_auth_total{result="fail"}[5m]) / rate(gateway_auth_total[5m])` | Sustained >10% = JWT secret mismatch with Nakama, expired tokens, or (EnterWorld) no server has capacity. |
| Save errors + allocation failures | `rate(gameserver_save_errors_total[5m])`, `rate(gateway_allocations_total{result="fail"}[5m])` | Anything non-zero is player progress at risk / Agones unable to give us pods. |
| Scrape targets up | `max by (job) (up)` | Which exporters are alive. |

Editing: change it in Grafana, then **Export → Save to file** back into
`monitoring/dashboards/rpg-gameplay.json` (provisioned dashboards are read-only
on disk — UI edits are lost on container recreate).

### Infra dashboards (import by ID, not bundled)

Host/DB/cache dashboards are community-maintained and version-churn a lot, so
they are not vendored. Once the matching exporter exists, import in Grafana via
**Dashboards → New → Import**:

| ID | Dashboard | Requires |
|----|-----------|----------|
| 1860 | Node Exporter Full | `node_exporter` on the VPS |
| 763 | Redis Dashboard for Prometheus Redis Exporter | `redis_exporter` |
| 9628 | PostgreSQL Database | `postgres_exporter` |
| 12740 | Kubernetes / Agones-friendly cluster view | kube-state-metrics (k3s tier) |

## Graduation paths

**Grafana Cloud free tier (VPS / soft-launch).** Keep the exact same exporters;
replace the local stack with **Grafana Alloy** on the host: Alloy scrapes
`:9100/:9101/:9102`, remote-writes to Grafana Cloud Prometheus, ships logs to
Loki. The dashboard JSON imports unchanged (datasource is a `prometheus` uid).
Free tier covers ~10k series / 50GB logs — enough through soft launch.

**k3s + Agones (growth).** Install `kube-prometheus-stack` (Prometheus Operator
+ Grafana + Alertmanager) and add a `PodMonitor` per workload:

- gateway pods → port `9102`
- game server pods → port `9101` (Agones fleets; keep `map_id`/`server_id` as labels)
- Agones ships its own controller metrics + a published Grafana dashboard set

Alerting starts there too: tick p99 over budget, allocation failure rate,
save errors > 0, gateway auth failure spike.
